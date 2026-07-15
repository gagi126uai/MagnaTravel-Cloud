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
    private readonly IInvoiceService _invoiceService;

    public MessageService(
        AppDbContext db,
        IWhatsAppGateway whatsAppGateway,
        IVoucherService voucherService,
        IInvoiceService invoiceService)
    {
        _db = db;
        _whatsAppGateway = whatsAppGateway;
        _voucherService = voucherService;
        _invoiceService = invoiceService;
    }

    public async Task<IReadOnlyList<MessageRecipientDto>> GetRecipientsAsync(
        string? search,
        OperationActor actor,
        CancellationToken cancellationToken)
    {
        await EnsureActorCanAsync(actor, Permissions.MessagesView, cancellationToken);

        var query = _db.Reservas
            .AsNoTracking()
            .Include(r => r.Payer)
            .Include(r => r.Passengers)
            .Include(r => r.Vouchers)
                .ThenInclude(v => v.PassengerAssignments)
                    .ThenInclude(a => a.Passenger)
            .Where(r => r.Status != EstadoReserva.Cancelled && r.Status != "Archived");

        if (!await ActorHasPermissionAsync(actor, Permissions.ReservasViewAll, cancellationToken))
        {
            query = query.Where(r => r.ResponsibleUserId == actor.UserId);
        }

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
        await EnsureActorCanAccessReservaAsync(actor, recipient.ReservaId, Permissions.ReservasViewAll, cancellationToken);
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
        await EnsureActorCanAccessReservaAsync(actor, recipient.ReservaId, Permissions.ReservasViewAll, cancellationToken);
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

    /// <summary>
    /// Paso 5 (2026-06-24): envia el PDF de una factura EMITIDA al cliente de la reserva por WhatsApp.
    ///
    /// <para>Reglas de seguridad/negocio (en este orden):
    /// <list type="number">
    ///   <item>El actor debe tener permiso de envio de mensajes (mismo gate que el voucher).</item>
    ///   <item>La factura debe existir y pertenecer a ESA reserva (no se puede mandar la factura de otra).</item>
    ///   <item>La factura debe estar EMITIDA: Resultado == "A" y con CAE. No se manda una factura en
    ///         proceso, rechazada, ni una NC/ND (solo el documento de venta).</item>
    ///   <item>El actor debe poder acceder a la reserva: dueño (ResponsibleUserId) o permiso view_all.</item>
    ///   <item>El cliente debe tener un contacto cargado; si no, error claro y legible.</item>
    /// </list></para>
    ///
    /// El PDF se obtiene de <c>IInvoiceService.GetPdfAsync</c>, la MISMA generacion que usa "Ver PDF"
    /// (no se duplica nada). La entrega se registra como un <see cref="MessageDelivery"/> con
    /// Kind = "Invoice", igual que el voucher registra la suya.
    /// </summary>
    public async Task<MessageDeliveryDto> SendInvoiceMessageAsync(
        SendInvoiceMessageRequest request,
        OperationActor actor,
        CancellationToken cancellationToken)
    {
        await EnsureActorCanAsync(actor, Permissions.MessagesSend, cancellationToken);

        // Resolvemos al destinatario reusando la misma logica que el voucher (valida que la persona
        // corresponda a la reserva y que tenga telefono). Para la factura el caso normal es el cliente.
        var recipient = await ResolveRecipientAsync(request.PersonType, request.PersonId, request.ReservaId, cancellationToken);

        // La factura debe pertenecer a la MISMA reserva del envio. Cargamos por PublicId/legacy y
        // validamos el vinculo: nunca se manda la factura de otra reserva.
        var invoiceId = await ResolveInvoiceIdAsync(request.InvoicePublicId, cancellationToken);
        var invoice = await _db.Invoices
            .AsNoTracking()
            .Include(i => i.Reserva)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw new KeyNotFoundException("Factura no encontrada.");

        if (invoice.ReservaId != recipient.ReservaId)
        {
            throw new InvalidOperationException("La factura no corresponde a la reserva indicada.");
        }

        // Ownership de la reserva: un vendedor solo puede mandar facturas de SUS reservas; el permiso
        // view_all (back-office/admin) saltea ese limite. Mismo criterio que los GET de facturas.
        await EnsureActorCanAccessReservaAsync(actor, invoice.Reserva, Permissions.CobranzasViewAll, cancellationToken);

        // Estado fiscal: solo se envia una factura EMITIDA por ARCA (aprobada y con CAE). Una factura en
        // proceso o rechazada todavia no es un comprobante valido para entregar al cliente.
        var fiscalStatus = InvoiceFiscalStatusMapper.FromResultado(invoice.Resultado);
        if (fiscalStatus != InvoiceFiscalStatus.Issued || string.IsNullOrWhiteSpace(invoice.CAE))
        {
            throw new InvalidOperationException(
                "La factura todavia no esta emitida (sin CAE). No se puede enviar una factura en proceso o rechazada.");
        }

        // CRITICO (fiscal/privacidad): NC y ND viven en la MISMA tabla Invoices (se distinguen por
        // TipoComprobante) y cuando ARCA las aprueba TAMBIEN quedan con Resultado="A" + CAE. Sin este
        // guard, pasar el publicId de una NC/ND mandaria su PDF al cliente rotulado como "factura"
        // (caption + fileName dicen "Factura"). Una NC ademas delata una anulacion/reembolso. Solo se
        // envian facturas de venta (tipos 1/6/11/51 -> InvoiceComprobanteHelpers.IsInvoice).
        if (!InvoiceComprobanteHelpers.IsInvoice(invoice.TipoComprobante))
        {
            throw new InvalidOperationException(
                "Solo se puede enviar una factura, no una nota de credito/debito.");
        }

        // No se manda al cliente una factura ya ANULADA (NC total aprobada por ARCA) como si fuera una
        // venta valida. AnnulmentStatus.Succeeded es el unico estado que confirma la anulacion.
        if (invoice.AnnulmentStatus == AnnulmentStatus.Succeeded)
        {
            throw new InvalidOperationException(
                "La factura fue anulada. No se puede enviar una factura anulada.");
        }

        // Mismo generador que "Ver PDF": no duplicamos la composicion del PDF.
        var pdfBytes = await _invoiceService.GetPdfAsync(invoice.Id, cancellationToken);

        // Numero de comprobante con el formato estandar AFIP "PtoVta-Numero" (4 + 8 digitos), el mismo
        // que muestra el PDF (ver InvoicePdfService: PuntoDeVenta:0000 / NumeroComprobante:00000000).
        var numeroComprobante = $"{invoice.PuntoDeVenta:0000}-{invoice.NumeroComprobante:00000000}";
        var fileName = $"Factura-{numeroComprobante}.pdf";
        var caption = string.IsNullOrWhiteSpace(request.Caption)
            ? $"Te compartimos la factura {numeroComprobante} de la reserva {invoice.Reserva?.NumeroReserva}."
            : request.Caption.Trim();

        var result = await _whatsAppGateway.SendDocumentAsync(
            recipient.Phone,
            caption,
            fileName,
            "application/pdf",
            pdfBytes,
            cancellationToken);

        var delivery = new MessageDelivery
        {
            ReservaId = recipient.ReservaId,
            CustomerId = recipient.CustomerId,
            PassengerId = recipient.PassengerId,
            Channel = MessageDeliveryChannels.WhatsApp,
            Kind = MessageDeliveryKinds.Invoice,
            Status = MessageDeliveryStatuses.Sent,
            Phone = recipient.Phone,
            MessageText = caption,
            AttachmentName = fileName,
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

    public async Task<MessageDeliveryDto> SendPartialCreditNoteMessageAsync(
        Guid bookingCancellationPublicId,
        OperationActor actor,
        CancellationToken cancellationToken)
    {
        await EnsureActorCanAsync(actor, Permissions.MessagesSend, cancellationToken);

        var bc = await _db.BookingCancellations
            .AsNoTracking()
            .Include(item => item.Reserva).ThenInclude(reserva => reserva.Payer)
            .Include(item => item.Lines)
            .Include(item => item.CreditNotes).ThenInclude(note => note.CreditNoteInvoice)
            .FirstOrDefaultAsync(item => item.PublicId == bookingCancellationPublicId, cancellationToken)
            ?? throw new KeyNotFoundException("Cancelación no encontrada.");

        if (bc.Lines.Count == 0
            || bc.Lines.Any(line => line.Scope != BookingCancellationLineScope.Partial))
            throw new InvalidOperationException("Esta cancelación no corresponde a una devolución parcial.");

        var succeeded = bc.CreditNotes
            .Where(note => note.Status == BookingCancellationCreditNoteStatus.Succeeded
                && note.CreditNoteInvoice != null)
            .ToList();
        if (succeeded.Count != 1)
            throw new InvalidOperationException("La nota de crédito todavía no está emitida o no es un documento único.");

        var invoice = succeeded[0].CreditNoteInvoice!;
        if (invoice.ReservaId != bc.ReservaId
            || !InvoiceComprobanteHelpers.IsCreditNote(invoice.TipoComprobante)
            || InvoiceFiscalStatusMapper.FromResultado(invoice.Resultado) != InvoiceFiscalStatus.Issued
            || string.IsNullOrWhiteSpace(invoice.CAE))
            throw new InvalidOperationException("La nota de crédito todavía no está emitida correctamente.");

        await EnsureActorCanAccessReservaAsync(actor, bc.Reserva, Permissions.CobranzasViewAll, cancellationToken);

        var customer = bc.Reserva.Payer
            ?? throw new InvalidOperationException("La reserva no tiene cliente para enviar la nota de crédito.");
        var phone = WhatsAppPhoneHelper.Canonicalize(customer.Phone)
            ?? WhatsAppPhoneHelper.Canonicalize(bc.Reserva.WhatsAppPhoneOverride);
        if (string.IsNullOrWhiteSpace(phone))
            throw new InvalidOperationException("El cliente no tiene teléfono asociado.");

        var pdfBytes = await _invoiceService.GetPdfAsync(invoice.Id, cancellationToken);
        var number = $"{invoice.PuntoDeVenta:0000}-{invoice.NumeroComprobante:00000000}";
        var fileName = $"Nota-de-credito-{number}.pdf";
        var caption = $"Te compartimos la nota de crédito {number} por la devolución de un servicio de la reserva {bc.Reserva.NumeroReserva}.";
        var result = await _whatsAppGateway.SendDocumentAsync(
            phone, caption, fileName, "application/pdf", pdfBytes, cancellationToken);

        var delivery = new MessageDelivery
        {
            ReservaId = bc.ReservaId,
            CustomerId = customer.Id,
            Channel = MessageDeliveryChannels.WhatsApp,
            Kind = MessageDeliveryKinds.CreditNote,
            Status = MessageDeliveryStatuses.Sent,
            Phone = phone,
            MessageText = caption,
            AttachmentName = fileName,
            BotMessageId = result.MessageId,
            SentByUserId = actor.UserId,
            SentByUserName = actor.UserName,
            CreatedAt = DateTime.UtcNow,
            SentAt = DateTime.UtcNow,
        };
        _db.MessageDeliveries.Add(delivery);
        await _db.SaveChangesAsync(cancellationToken);
        return MapDelivery(delivery);
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

    private async Task<int> ResolveInvoiceIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var resolved = await _db.Invoices.AsNoTracking().ResolveInternalIdAsync(publicIdOrLegacyId, cancellationToken);
        if (!resolved.HasValue && int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException("Factura no encontrada.");
    }

    /// <summary>
    /// Paso 5 (2026-06-24): valida que el actor pueda operar sobre la reserva de la factura. Mismo
    /// criterio que los GET de facturas: el dueño (ResponsibleUserId) siempre puede; el resto necesita
    /// el permiso view_all (back-office/admin). Evita que un vendedor mande la factura de la reserva de
    /// otro vendedor aunque tenga permiso de enviar mensajes.
    /// </summary>
    private async Task EnsureActorCanAccessReservaAsync(
        OperationActor actor,
        int reservaId,
        string viewAllPermission,
        CancellationToken cancellationToken)
    {
        var reserva = await _db.Reservas
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == reservaId, cancellationToken)
            ?? throw new KeyNotFoundException("Reserva no encontrada.");

        await EnsureActorCanAccessReservaAsync(actor, reserva, viewAllPermission, cancellationToken);
    }

    private async Task EnsureActorCanAccessReservaAsync(
        OperationActor actor,
        Reserva? reserva,
        string viewAllPermission,
        CancellationToken cancellationToken)
    {
        if (reserva is null)
        {
            throw new InvalidOperationException("La factura no tiene reserva asociada.");
        }

        if (actor.IsAdmin)
        {
            return;
        }

        // El dueño de la reserva siempre puede.
        if (!string.IsNullOrWhiteSpace(reserva.ResponsibleUserId) &&
            string.Equals(reserva.ResponsibleUserId, actor.UserId, StringComparison.Ordinal))
        {
            return;
        }

        // Si no es el dueño, necesita el permiso de ver todas las cobranzas.
        if (!await ActorHasPermissionAsync(actor, viewAllPermission, cancellationToken))
        {
            throw new UnauthorizedAccessException("El usuario no tiene acceso a esta reserva.");
        }
    }

    private async Task<bool> ActorHasPermissionAsync(
        OperationActor actor,
        string permission,
        CancellationToken cancellationToken)
    {
        if (actor.IsAdmin)
        {
            return true;
        }

        var roleNames = actor.Roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .ToArray();

        return roleNames.Length > 0 && await _db.RolePermissions
            .AsNoTracking()
            .AnyAsync(item => roleNames.Contains(item.RoleName) && item.Permission == permission, cancellationToken);
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
