using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class MessageService : IMessageService
{
    private readonly AppDbContext _db;
    private readonly IWhatsAppGateway _whatsAppGateway;
    private readonly IVoucherService _voucherService;

    public MessageService(
        AppDbContext db,
        IWhatsAppGateway whatsAppGateway,
        IVoucherService voucherService)
    {
        _db = db;
        _whatsAppGateway = whatsAppGateway;
        _voucherService = voucherService;
    }

    public async Task<IReadOnlyList<MessageRecipientDto>> GetRecipientsAsync(string? search, CancellationToken cancellationToken)
    {
        var query = _db.Reservas
            .AsNoTracking()
            .Include(r => r.Payer)
            .Include(r => r.Passengers)
            .Include(r => r.Vouchers)
                .ThenInclude(v => v.PassengerAssignments)
                    .ThenInclude(a => a.Passenger)
            .Where(r => r.Status != EstadoReserva.Cancelled && r.Status != "Archived");

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLower();
            query = query.Where(r =>
                r.NumeroReserva.ToLower().Contains(normalized) ||
                r.Name.ToLower().Contains(normalized) ||
                (r.Payer != null && r.Payer.FullName.ToLower().Contains(normalized)) ||
                r.Passengers.Any(p => p.FullName.ToLower().Contains(normalized)));
        }

        var reservas = await query
            .OrderByDescending(r => r.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var recipients = new List<MessageRecipientDto>();
        foreach (var reserva in reservas)
        {
            if (reserva.Payer is not null)
            {
                recipients.Add(new MessageRecipientDto
                {
                    PersonType = "customer",
                    PersonPublicId = reserva.Payer.PublicId,
                    ReservaPublicId = reserva.PublicId,
                    NumeroReserva = reserva.NumeroReserva,
                    DisplayName = reserva.Payer.FullName,
                    Phone = WhatsAppPhoneHelper.Canonicalize(reserva.Payer.Phone) ?? WhatsAppPhoneHelper.Canonicalize(reserva.WhatsAppPhoneOverride),
                    Vouchers = reserva.Vouchers
                        .Where(v => v.Status != VoucherStatuses.Revoked &&
                            (v.Scope == VoucherScopes.Reservation || v.Scope == VoucherScopes.AllPassengers))
                        .Select(MapVoucher)
                        .ToList()
                });
            }

            foreach (var passenger in reserva.Passengers.OrderBy(p => p.FullName))
            {
                recipients.Add(new MessageRecipientDto
                {
                    PersonType = "passenger",
                    PersonPublicId = passenger.PublicId,
                    ReservaPublicId = reserva.PublicId,
                    NumeroReserva = reserva.NumeroReserva,
                    DisplayName = passenger.FullName,
                    Phone = WhatsAppPhoneHelper.Canonicalize(passenger.Phone),
                    Vouchers = reserva.Vouchers
                        .Where(v => v.Status != VoucherStatuses.Revoked &&
                            (
                            v.Scope == VoucherScopes.Reservation ||
                            v.Scope == VoucherScopes.AllPassengers ||
                            v.PassengerAssignments.Any(a => a.PassengerId == passenger.Id)))
                        .Select(MapVoucher)
                        .ToList()
                });
            }
        }

        return recipients;
    }

    public async Task<MessageDeliveryDto> SendSimpleMessageAsync(
        SendSimpleMessageRequest request,
        OperationActor actor,
        CancellationToken cancellationToken)
    {
        await EnsureActorCanAsync(actor, Permissions.MessagesSend, cancellationToken);

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new InvalidOperationException("El mensaje es obligatorio.");
        }

        var recipient = await ResolveRecipientAsync(request.PersonType, request.PersonId, request.ReservaId, cancellationToken);
        var result = await _whatsAppGateway.SendTextAsync(recipient.Phone, request.Message.Trim(), cancellationToken);

        var delivery = new MessageDelivery
        {
            ReservaId = recipient.ReservaId,
            CustomerId = recipient.CustomerId,
            PassengerId = recipient.PassengerId,
            Channel = MessageDeliveryChannels.WhatsApp,
            Kind = MessageDeliveryKinds.Text,
            Status = MessageDeliveryStatuses.Sent,
            Phone = recipient.Phone,
            MessageText = request.Message.Trim(),
            BotMessageId = result.MessageId,
            SentByUserId = actor.UserId,
            SentByUserName = actor.UserName,
            CreatedAt = DateTime.UtcNow,
            SentAt = DateTime.UtcNow
        };

        _db.MessageDeliveries.Add(delivery);
        await _db.SaveChangesAsync(cancellationToken);
        return MapDelivery(delivery);
    }

    public async Task<IReadOnlyList<MessageDeliveryDto>> SendVoucherMessageAsync(
        SendVoucherMessageRequest request,
        OperationActor actor,
        CancellationToken cancellationToken)
    {
        await EnsureActorCanAsync(actor, Permissions.MessagesSend, cancellationToken);

        if (request.VoucherIds.Count == 0)
        {
            throw new InvalidOperationException("Debe seleccionar al menos un voucher.");
        }

        var recipient = await ResolveRecipientAsync(request.PersonType, request.PersonId, request.ReservaId, cancellationToken);
        var deliveries = new List<MessageDeliveryDto>();

        foreach (var voucherId in request.VoucherIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var passengerId = string.Equals(request.PersonType, "passenger", StringComparison.OrdinalIgnoreCase)
                ? request.PersonId
                : null;

            var voucher = await _voucherService.EnsureVoucherCanBeSentAsync(
                request.ReservaId,
                voucherId,
                passengerId,
                request.Exception,
                actor,
                cancellationToken);

            var file = await _voucherService.DownloadVoucherAsync(voucherId, cancellationToken);
            var caption = string.IsNullOrWhiteSpace(request.Caption)
                ? $"Te compartimos el voucher {voucher.FileName} de la reserva {voucher.NumeroReserva}."
                : request.Caption.Trim();

            var result = await _whatsAppGateway.SendDocumentAsync(
                recipient.Phone,
                caption,
                file.FileName,
                file.ContentType,
                file.Bytes,
                cancellationToken);

            var internalVoucherId = await ResolveVoucherIdAsync(voucherId, cancellationToken);
            var delivery = new MessageDelivery
            {
                ReservaId = recipient.ReservaId,
                CustomerId = recipient.CustomerId,
                PassengerId = recipient.PassengerId,
                VoucherId = internalVoucherId,
                Channel = MessageDeliveryChannels.WhatsApp,
                Kind = MessageDeliveryKinds.Voucher,
                Status = MessageDeliveryStatuses.Sent,
                Phone = recipient.Phone,
                MessageText = caption,
                AttachmentName = file.FileName,
                BotMessageId = result.MessageId,
                SentByUserId = actor.UserId,
                SentByUserName = actor.UserName,
                CreatedAt = DateTime.UtcNow,
                SentAt = DateTime.UtcNow
            };

            _db.MessageDeliveries.Add(delivery);
            await _voucherService.RecordVoucherSentAsync(voucherId, actor, caption, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            deliveries.Add(MapDelivery(delivery));
        }

        return deliveries;
    }

    private async Task<ResolvedMessageRecipient> ResolveRecipientAsync(
        string personType,
        string personPublicIdOrLegacyId,
        string reservaPublicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        var reservaId = await ResolveReservaIdAsync(reservaPublicIdOrLegacyId, cancellationToken);
        var reserva = await _db.Reservas
            .Include(r => r.Payer)
            .Include(r => r.Passengers)
            .FirstOrDefaultAsync(r => r.Id == reservaId, cancellationToken)
            ?? throw new KeyNotFoundException("Reserva no encontrada.");

        if (string.Equals(personType, "customer", StringComparison.OrdinalIgnoreCase))
        {
            var customerId = await ResolveCustomerIdAsync(personPublicIdOrLegacyId, cancellationToken);
            if (reserva.PayerId != customerId)
            {
                throw new InvalidOperationException("La persona seleccionada no corresponde a la reserva.");
            }

            var phone = WhatsAppPhoneHelper.Canonicalize(reserva.Payer?.Phone) ?? WhatsAppPhoneHelper.Canonicalize(reserva.WhatsAppPhoneOverride);
            if (string.IsNullOrWhiteSpace(phone))
            {
                throw new InvalidOperationException("La persona seleccionada no tiene telefono asociado.");
            }

            return new ResolvedMessageRecipient(reserva.Id, customerId, null, phone);
        }

        if (string.Equals(personType, "passenger", StringComparison.OrdinalIgnoreCase))
        {
            var passengerId = await ResolvePassengerIdAsync(personPublicIdOrLegacyId, cancellationToken);
            var passenger = reserva.Passengers.FirstOrDefault(item => item.Id == passengerId)
                ?? throw new InvalidOperationException("El pasajero seleccionado no corresponde a la reserva.");

            var phone = WhatsAppPhoneHelper.Canonicalize(passenger.Phone);
            if (string.IsNullOrWhiteSpace(phone))
            {
                throw new InvalidOperationException("La persona seleccionada no tiene telefono asociado.");
            }

            return new ResolvedMessageRecipient(reserva.Id, null, passenger.Id, phone);
        }

        throw new InvalidOperationException("Tipo de persona invalido.");
    }

    private async Task EnsureActorCanAsync(OperationActor actor, string permission, CancellationToken cancellationToken)
    {
        if (actor.IsAdmin)
        {
            return;
        }

        var roleNames = actor.Roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .ToArray();

        var allowed = roleNames.Length > 0 && await _db.RolePermissions
            .AsNoTracking()
            .AnyAsync(item => roleNames.Contains(item.RoleName) && item.Permission == permission, cancellationToken);

        if (!allowed)
        {
            throw new UnauthorizedAccessException("El usuario no tiene permisos para realizar esta accion.");
        }
    }

    private async Task<int> ResolveReservaIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var resolved = await _db.Reservas.AsNoTracking().ResolveInternalIdAsync(publicIdOrLegacyId, cancellationToken);
        if (!resolved.HasValue && int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException("Reserva no encontrada.");
    }

    private async Task<int> ResolveCustomerIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var resolved = await _db.Customers.AsNoTracking().ResolveInternalIdAsync(publicIdOrLegacyId, cancellationToken);
        if (!resolved.HasValue && int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException("Cliente no encontrado.");
    }

    private async Task<int> ResolvePassengerIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var resolved = await _db.Passengers.AsNoTracking().ResolveInternalIdAsync(publicIdOrLegacyId, cancellationToken);
        if (!resolved.HasValue && int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException("Pasajero no encontrado.");
    }

    private async Task<int> ResolveVoucherIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var resolved = await _db.Vouchers.AsNoTracking().ResolveInternalIdAsync(publicIdOrLegacyId, cancellationToken);
        if (!resolved.HasValue && int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException("Voucher no encontrado.");
    }

    private static VoucherDto MapVoucher(Voucher voucher)
    {
        return new VoucherDto
        {
            PublicId = voucher.PublicId,
            ReservaPublicId = voucher.Reserva?.PublicId ?? Guid.Empty,
            NumeroReserva = voucher.Reserva?.NumeroReserva ?? string.Empty,
            Source = voucher.Source,
            Status = voucher.Status,
            Scope = voucher.Scope,
            FileName = voucher.FileName,
            ContentType = voucher.ContentType,
            FileSize = voucher.FileSize,
            ExternalOrigin = voucher.ExternalOrigin,
            IsEnabledForSending = voucher.IsEnabledForSending,
            CanSend = voucher.CanBeSent(),
            ReservationHasOutstandingBalance = voucher.Reserva is not null && ReservationEconomicPolicy.HasOutstandingBalance(voucher.Reserva),
            OutstandingBalance = voucher.Reserva is null ? 0m : ReservationEconomicPolicy.RoundCurrency(voucher.Reserva.Balance),
            CreatedByUserName = voucher.CreatedByUserName,
            CreatedAt = voucher.CreatedAt,
            IssuedByUserName = voucher.IssuedByUserName,
            IssuedAt = voucher.IssuedAt,
            WasExceptionalIssue = voucher.WasExceptionalIssue,
            ExceptionalReason = voucher.ExceptionalReason,
            AuthorizedBySuperiorUserId = voucher.AuthorizedBySuperiorUserId,
            AuthorizedBySuperiorUserName = voucher.AuthorizedBySuperiorUserName,
            AuthorizationStatus = voucher.AuthorizationStatus,
            RejectReason = voucher.RejectReason,
            RevokedAt = voucher.RevokedAt,
            RevokedByUserId = voucher.RevokedByUserId,
            RevokedByUserName = voucher.RevokedByUserName,
            RevocationReason = voucher.RevocationReason,
            PassengerPublicIds = voucher.PassengerAssignments
                .Where(assignment => assignment.Passenger is not null)
                .Select(assignment => assignment.Passenger!.PublicId)
                .ToList(),
            PassengerNames = voucher.PassengerAssignments
                .Where(assignment => assignment.Passenger is not null)
                .Select(assignment => assignment.Passenger!.FullName)
                .OrderBy(name => name)
                .ToList()
        };
    }

    private static MessageDeliveryDto MapDelivery(MessageDelivery delivery)
    {
        return new MessageDeliveryDto
        {
            PublicId = delivery.PublicId,
            ReservaPublicId = delivery.Reserva?.PublicId,
            VoucherPublicId = delivery.Voucher?.PublicId,
            Channel = delivery.Channel,
            Kind = delivery.Kind,
            Status = delivery.Status,
            Phone = delivery.Phone,
            MessageText = delivery.MessageText,
            AttachmentName = delivery.AttachmentName,
            BotMessageId = delivery.BotMessageId,
            SentByUserName = delivery.SentByUserName,
            CreatedAt = delivery.CreatedAt,
            SentAt = delivery.SentAt,
            Error = delivery.Error
        };
    }

    private sealed record ResolvedMessageRecipient(int ReservaId, int? CustomerId, int? PassengerId, string Phone);
}
