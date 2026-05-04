using System.Text;
using System.Net;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class VoucherService : IVoucherService
{
    private readonly AppDbContext _db;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;
    private readonly IFileStoragePort _fileStoragePort;

    public VoucherService(
        AppDbContext db,
        IOperationalFinanceSettingsService operationalFinanceSettingsService,
        IFileStoragePort fileStoragePort)
    {
        _db = db;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        _fileStoragePort = fileStoragePort;
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<byte[]> GenerateVoucherHtmlAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var reservaId = await ResolveReservaIdAsync(reservaPublicIdOrLegacyId, cancellationToken);
        return await GenerateVoucherHtmlAsync(reservaId, cancellationToken);
    }

    public async Task<byte[]> GenerateVoucherHtmlAsync(int reservaId, CancellationToken cancellationToken)
    {
        var (reserva, agency) = await LoadVoucherDataAsync(reservaId, cancellationToken);

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        html.AppendLine("<style>");
        html.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; margin: 40px; color: #1e293b; }");
        html.AppendLine("h1 { color: #4f46e5; font-size: 28px; margin-bottom: 5px; }");
        html.AppendLine("h2 { color: #334155; font-size: 18px; margin-top: 30px; border-bottom: 2px solid #e2e8f0; padding-bottom: 8px; }");
        html.AppendLine(".header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 30px; border-bottom: 3px solid #4f46e5; padding-bottom: 20px; }");
        html.AppendLine(".info-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 15px; margin: 15px 0; }");
        html.AppendLine(".info-item { background: #f8fafc; padding: 12px; border-radius: 8px; }");
        html.AppendLine(".info-label { font-size: 11px; color: #94a3b8; text-transform: uppercase; font-weight: 700; letter-spacing: 0.05em; }");
        html.AppendLine(".info-value { font-size: 14px; font-weight: 600; color: #1e293b; margin-top: 4px; }");
        html.AppendLine("table { width: 100%; border-collapse: collapse; margin: 10px 0; }");
        html.AppendLine("th { background: #f1f5f9; padding: 10px; text-align: left; font-size: 11px; text-transform: uppercase; color: #64748b; }");
        html.AppendLine("td { padding: 10px; border-bottom: 1px solid #e2e8f0; font-size: 13px; }");
        html.AppendLine(".footer { margin-top: 40px; text-align: center; font-size: 11px; color: #94a3b8; border-top: 1px solid #e2e8f0; padding-top: 20px; }");
        html.AppendLine("</style></head><body>");

        html.AppendLine("<div class='header'>");
        html.AppendLine($"<div><h1>{EscapeHtml(agency?.AgencyName ?? "Agencia de Viajes")}</h1>");
        html.AppendLine($"<p style='color:#64748b;font-size:12px'>{EscapeHtml(agency?.Address)} | {EscapeHtml(agency?.Phone)}</p></div>");
        html.AppendLine($"<div style='text-align:right'><div style='font-size:12px;color:#94a3b8'>RESERVA</div>");
        html.AppendLine($"<div style='font-size:20px;font-weight:800;color:#4f46e5'>{EscapeHtml(reserva.NumeroReserva)}</div></div>");
        html.AppendLine("</div>");

        html.AppendLine("<h2>Datos del Pasajero</h2>");
        html.AppendLine("<div class='info-grid'>");
        html.AppendLine($"<div class='info-item'><div class='info-label'>Titular</div><div class='info-value'>{EscapeHtml(reserva.Payer?.FullName ?? "---")}</div></div>");
        html.AppendLine($"<div class='info-item'><div class='info-label'>Documento</div><div class='info-value'>{EscapeHtml(reserva.Payer?.DocumentNumber ?? "---")}</div></div>");
        html.AppendLine($"<div class='info-item'><div class='info-label'>Salida</div><div class='info-value'>{reserva.StartDate?.ToString("dd/MM/yyyy") ?? "---"}</div></div>");
        html.AppendLine($"<div class='info-item'><div class='info-label'>Regreso</div><div class='info-value'>{reserva.EndDate?.ToString("dd/MM/yyyy") ?? "---"}</div></div>");
        html.AppendLine("</div>");

        AppendPassengers(reserva, html);
        AppendHotels(reserva, html);
        AppendFlights(reserva, html);
        AppendTransfers(reserva, html);
        AppendPackages(reserva, html);

        html.AppendLine("<div class='footer'>");
        html.AppendLine($"<p>Voucher generado el {DateTime.UtcNow.AddHours(-3):dd/MM/yyyy HH:mm} hs (Argentina)</p>");
        html.AppendLine($"<p><strong>{EscapeHtml(agency?.AgencyName ?? "MagnaTravel")}</strong> | {EscapeHtml(agency?.Email)} | {EscapeHtml(agency?.Phone)}</p>");
        html.AppendLine("<p style='margin-top:8px;font-style:italic'>Este documento no tiene validez como comprobante fiscal.</p>");
        html.AppendLine("</div></body></html>");

        return Encoding.UTF8.GetBytes(html.ToString());
    }

    public async Task<byte[]> GenerateVoucherPdfAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var reservaId = await ResolveReservaIdAsync(reservaPublicIdOrLegacyId, cancellationToken);
        return await GenerateVoucherPdfAsync(reservaId, cancellationToken);
    }

    public async Task<byte[]> GenerateVoucherPdfAsync(int reservaId, CancellationToken cancellationToken)
    {
        var (reserva, agency) = await LoadVoucherDataAsync(reservaId, cancellationToken);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Element(header => ComposeHeader(header, reserva, agency));
                page.Content().Element(content => ComposeContent(content, reserva));
                page.Footer()
                    .AlignCenter()
                    .DefaultTextStyle(x => x.FontSize(9).FontColor(Colors.Grey.Darken1))
                    .Text(text =>
                    {
                        text.Span($"Voucher generado el {DateTime.UtcNow.AddHours(-3):dd/MM/yyyy HH:mm} hs");
                        text.Span(" | ");
                        text.Span(agency?.AgencyName ?? "MagnaTravel").SemiBold();
                    });
            });
        });

        return document.GeneratePdf();
    }

    public async Task<IReadOnlyList<VoucherDto>> GetVouchersAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var reservaId = await ResolveReservaIdAsync(reservaPublicIdOrLegacyId, cancellationToken);

        var vouchers = await _db.Vouchers
            .AsNoTracking()
            .Include(v => v.Reserva)
            .Include(v => v.PassengerAssignments)
                .ThenInclude(a => a.Passenger)
            .Where(v => v.ReservaId == reservaId)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(cancellationToken);

        return vouchers.Select(MapVoucher).ToList();
    }

    public async Task<VoucherDto> GenerateVoucherRecordAsync(
        string reservaPublicIdOrLegacyId,
        GenerateVoucherRequest request,
        OperationActor actor,
        CancellationToken cancellationToken)
    {
        await EnsureActorCanAsync(actor, Permissions.VouchersGenerate, cancellationToken);

        var reservaId = await ResolveReservaIdAsync(reservaPublicIdOrLegacyId, cancellationToken);
        var reserva = await LoadReservaWithPassengersAsync(reservaId, cancellationToken);

        EnsureReservaHasPassengers(reserva);

        var passengers = await ResolvePassengerAssignmentsAsync(reserva, request.Scope, request.PassengerIds, cancellationToken);
        await EnsureNoPassengerDuplicateAsync(reserva.Id, passengers, NormalizeScope(request.Scope), cancellationToken);

        var pdfBytes = await GenerateVoucherPdfAsync(reserva.PublicId.ToString(), cancellationToken);
        var fileName = BuildVoucherFileName(reserva);
        await using var stream = new MemoryStream(pdfBytes);
        var stored = await _fileStoragePort.SaveAsync(
            stream,
            $"vouchers/generated/{DateTime.UtcNow:yyyy}/{Guid.NewGuid():N}.pdf",
            fileName,
            "application/pdf",
            cancellationToken);

        var voucher = new Voucher
        {
            ReservaId = reserva.Id,
            Source = VoucherSources.Generated,
            Status = VoucherStatuses.Draft,
            Scope = NormalizeScope(request.Scope),
            FileName = stored.FileName,
            StoredFileName = stored.StoredFileName,
            ContentType = stored.ContentType,
            FileSize = stored.FileSize,
            IsEnabledForSending = false,
            CreatedByUserId = actor.UserId,
            CreatedByUserName = actor.UserName,
            CreatedAt = DateTime.UtcNow
        };

        foreach (var passenger in passengers)
        {
            voucher.PassengerAssignments.Add(new VoucherPassengerAssignment { PassengerId = passenger.Id });
        }

        _db.Vouchers.Add(voucher);
        AddVoucherAudit(voucher, reserva, VoucherAuditActions.Generated, actor, null, null, null, null);
        await _db.SaveChangesAsync(cancellationToken);

        return await GetVoucherDtoAsync(voucher.Id, cancellationToken);
    }

    public async Task<VoucherDto> UploadExternalVoucherAsync(
        string reservaPublicIdOrLegacyId,
        UploadExternalVoucherRequest request,
        Stream stream,
        string fileName,
        string contentType,
        long fileSize,
        OperationActor actor,
        CancellationToken cancellationToken)
    {
        await EnsureActorCanAsync(actor, Permissions.VouchersUpload, cancellationToken);

        var reservaId = await ResolveReservaIdAsync(reservaPublicIdOrLegacyId, cancellationToken);
        var reserva = await LoadReservaWithPassengersAsync(reservaId, cancellationToken);

        EnsureReservaHasPassengers(reserva);

        var passengers = await ResolvePassengerAssignmentsAsync(reserva, request.Scope, request.PassengerIds, cancellationToken);
        await EnsureNoPassengerDuplicateAsync(reserva.Id, passengers, NormalizeScope(request.Scope), cancellationToken);

        var safeFileName = SanitizeOriginalFileName(fileName);
        var normalizedContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();
        
        try
        {
            await using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            
            // Validación de integridad y tipo
            ValidateVoucherUpload(safeFileName, normalizedContentType, buffer.ToArray(), fileSize);
            buffer.Position = 0;

            var extension = Path.GetExtension(safeFileName).ToLowerInvariant();
            var stored = await _fileStoragePort.SaveAsync(
                buffer,
                $"vouchers/external/{DateTime.UtcNow:yyyy}/{Guid.NewGuid():N}{extension}",
                safeFileName,
                normalizedContentType,
                cancellationToken);

            var voucher = new Voucher
            {
                ReservaId = reserva.Id,
                Source = VoucherSources.External,
                Status = VoucherStatuses.UploadedExternal,
                Scope = NormalizeScope(request.Scope),
                FileName = stored.FileName,
                StoredFileName = stored.StoredFileName,
                ContentType = stored.ContentType,
                FileSize = stored.FileSize,
                ExternalOrigin = string.IsNullOrWhiteSpace(request.ExternalOrigin) ? "Operador externo" : request.ExternalOrigin.Trim(),
                IsEnabledForSending = true,
                CreatedByUserId = actor.UserId,
                CreatedByUserName = actor.UserName,
                CreatedAt = DateTime.UtcNow,
                IssuedByUserId = actor.UserId,
                IssuedByUserName = actor.UserName,
                IssuedAt = DateTime.UtcNow
            };

            foreach (var passenger in passengers)
            {
                voucher.PassengerAssignments.Add(new VoucherPassengerAssignment { PassengerId = passenger.Id });
            }

            _db.Vouchers.Add(voucher);
            AddVoucherAudit(
                voucher,
                reserva,
                VoucherAuditActions.UploadedExternal,
                actor,
                $"Origen: {voucher.ExternalOrigin}",
                null,
                null,
                null);

            await _db.SaveChangesAsync(cancellationToken);
            return await GetVoucherDtoAsync(voucher.Id, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Re-lanzar validaciones de negocio tal cual
            throw;
        }
        catch (Exception ex)
        {
            // Capturar errores de infraestructura (DB, MinIO, etc) y dar contexto
            throw new InvalidOperationException($"Error procesando voucher externo '{safeFileName}': {ex.Message}", ex);
        }
    }

    public async Task<VoucherDto> IssueVoucherAsync(
        string voucherPublicIdOrLegacyId,
        IssueVoucherRequest request,
        OperationActor actor,
        CancellationToken cancellationToken)
    {
        await EnsureActorCanAsync(actor, Permissions.VouchersIssue, cancellationToken);

        var voucher = await LoadVoucherGraphAsync(voucherPublicIdOrLegacyId, cancellationToken);
        if (voucher.Reserva is null)
        {
            throw new InvalidOperationException("El voucher no tiene reserva asociada.");
        }

        EnsureVoucherIsNotRevoked(voucher);

        if (voucher.Status == VoucherStatuses.Issued || voucher.Status == VoucherStatuses.UploadedExternal)
        {
            throw new InvalidOperationException("El voucher ya esta emitido o cargado como externo.");
        }

        var authorization = await ValidateExceptionalAuthorizationAsync(
            voucher.Reserva,
            request.ExceptionalReason,
            request.AuthorizedBySuperiorUserId,
            actor,
            cancellationToken);

        voucher.IssueReason = NormalizeOptionalReason(request.Reason);
        voucher.WasExceptionalIssue = authorization.WasExceptional;
        voucher.ExceptionalReason = authorization.WasExceptional ? authorization.Reason : null;
        voucher.AuthorizedBySuperiorUserId = authorization.SuperiorUserId;
        voucher.AuthorizedBySuperiorUserName = authorization.SuperiorUserName;
        voucher.OutstandingBalanceAtIssue = authorization.OutstandingBalance;

        if (authorization.WasExceptional && !actor.IsAdmin)
        {
            voucher.Status = VoucherStatuses.PendingAuthorization;
            voucher.AuthorizationStatus = VoucherAuthorizationStatuses.Pending;

            AddVoucherAudit(
                voucher,
                voucher.Reserva,
                VoucherAuditActions.AuthorizationRequested,
                actor,
                authorization.Reason,
                authorization.SuperiorUserId,
                authorization.SuperiorUserName,
                "Solicitud de autorizacion enviada al supervisor.");
        }
        else
        {
            voucher.Status = VoucherStatuses.Issued;
            voucher.IsEnabledForSending = true;
            voucher.IssuedByUserId = actor.UserId;
            voucher.IssuedByUserName = actor.UserName;
            voucher.IssuedAt = DateTime.UtcNow;

            if (authorization.WasExceptional)
            {
                voucher.AuthorizationStatus = VoucherAuthorizationStatuses.Approved;
            }

            AddVoucherAudit(
                voucher,
                voucher.Reserva,
                authorization.WasExceptional ? VoucherAuditActions.ExceptionalIssue : VoucherAuditActions.Issued,
                actor,
                authorization.WasExceptional ? authorization.Reason : voucher.IssueReason,
                authorization.SuperiorUserId,
                authorization.SuperiorUserName,
                authorization.WasExceptional ? "Reserva con saldo pendiente (Autorizado directamente por Admin)." : null);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return await GetVoucherDtoAsync(voucher.Id, cancellationToken);
    }

    public async Task<VoucherDto> ApproveVoucherIssueAsync(string voucherPublicIdOrLegacyId, OperationActor actor, CancellationToken cancellationToken)
    {
        await EnsureActorCanAsync(actor, Permissions.VouchersAuthorizeException, cancellationToken);

        var voucher = await LoadVoucherGraphAsync(voucherPublicIdOrLegacyId, cancellationToken);
        EnsureVoucherIsNotRevoked(voucher);

        if (voucher.Status != VoucherStatuses.PendingAuthorization || voucher.AuthorizationStatus != VoucherAuthorizationStatuses.Pending)
        {
            throw new InvalidOperationException("El voucher no esta pendiente de autorizacion.");
        }

        if (!actor.IsAdmin && voucher.AuthorizedBySuperiorUserId != actor.UserId)
        {
            throw new UnauthorizedAccessException("Solo el administrador o el supervisor asignado puede aprobar esta solicitud.");
        }

        voucher.Status = VoucherStatuses.Issued;
        voucher.AuthorizationStatus = VoucherAuthorizationStatuses.Approved;
        voucher.IsEnabledForSending = true;
        voucher.IssuedByUserId = actor.UserId;
        voucher.IssuedByUserName = actor.UserName;
        voucher.IssuedAt = DateTime.UtcNow;

        AddVoucherAudit(
            voucher,
            voucher.Reserva!,
            VoucherAuditActions.AuthorizationApproved,
            actor,
            "Solicitud de autorizacion aprobada.",
            voucher.AuthorizedBySuperiorUserId,
            voucher.AuthorizedBySuperiorUserName,
            null);

        await _db.SaveChangesAsync(cancellationToken);
        return await GetVoucherDtoAsync(voucher.Id, cancellationToken);
    }

    public async Task<VoucherDto> RejectVoucherIssueAsync(string voucherPublicIdOrLegacyId, RejectVoucherRequest request, OperationActor actor, CancellationToken cancellationToken)
    {
        await EnsureActorCanAsync(actor, Permissions.VouchersAuthorizeException, cancellationToken);

        var voucher = await LoadVoucherGraphAsync(voucherPublicIdOrLegacyId, cancellationToken);
        EnsureVoucherIsNotRevoked(voucher);

        if (voucher.Status != VoucherStatuses.PendingAuthorization || voucher.AuthorizationStatus != VoucherAuthorizationStatuses.Pending)
        {
            throw new InvalidOperationException("El voucher no esta pendiente de autorizacion.");
        }

        if (!actor.IsAdmin && voucher.AuthorizedBySuperiorUserId != actor.UserId)
        {
            throw new UnauthorizedAccessException("Solo el administrador o el supervisor asignado puede rechazar esta solicitud.");
        }

        voucher.Status = VoucherStatuses.Draft;
        voucher.AuthorizationStatus = VoucherAuthorizationStatuses.Rejected;
        voucher.RejectReason = request.Reason;
        
        AddVoucherAudit(
            voucher,
            voucher.Reserva!,
            VoucherAuditActions.AuthorizationRejected,
            actor,
            request.Reason,
            voucher.AuthorizedBySuperiorUserId,
            voucher.AuthorizedBySuperiorUserName,
            "Solicitud de autorizacion rechazada.");

        await _db.SaveChangesAsync(cancellationToken);
        return await GetVoucherDtoAsync(voucher.Id, cancellationToken);
    }

    public async Task<VoucherDto> RevokeVoucherAsync(string voucherPublicIdOrLegacyId, RevokeVoucherRequest request, OperationActor actor, CancellationToken cancellationToken)
    {
        await EnsureActorCanAsync(actor, Permissions.VouchersRevoke, cancellationToken);

        var reason = actor.IsAdmin
            ? NormalizeOptionalReason(request.Reason)
            : NormalizeRequiredReason(request.Reason, "Debe indicar un motivo de anulacion de al menos 10 caracteres.");
        var voucher = await LoadVoucherGraphAsync(voucherPublicIdOrLegacyId, cancellationToken);
        if (voucher.Reserva is null)
        {
            throw new InvalidOperationException("El voucher no tiene reserva asociada.");
        }

        if (voucher.Status == VoucherStatuses.Revoked)
        {
            throw new InvalidOperationException("El voucher ya esta anulado.");
        }

        var wasPendingAuthorization = voucher.Status == VoucherStatuses.PendingAuthorization &&
            voucher.AuthorizationStatus == VoucherAuthorizationStatuses.Pending;

        voucher.Status = VoucherStatuses.Revoked;
        voucher.IsEnabledForSending = false;
        voucher.RevokedAt = DateTime.UtcNow;
        voucher.RevokedByUserId = actor.UserId;
        voucher.RevokedByUserName = actor.UserName;
        voucher.RevocationReason = reason;

        if (wasPendingAuthorization)
        {
            voucher.AuthorizationStatus = VoucherAuthorizationStatuses.Cancelled;
        }

        AddVoucherAudit(
            voucher,
            voucher.Reserva,
            VoucherAuditActions.Revoked,
            actor,
            reason,
            wasPendingAuthorization ? voucher.AuthorizedBySuperiorUserId : null,
            wasPendingAuthorization ? voucher.AuthorizedBySuperiorUserName : null,
            wasPendingAuthorization
                ? "Voucher anulado; solicitud de autorizacion pendiente cancelada."
                : "Voucher anulado por correccion documental.");

        await _db.SaveChangesAsync(cancellationToken);
        return await GetVoucherDtoAsync(voucher.Id, cancellationToken);
    }

    public async Task<VoucherDto> EnsureVoucherCanBeSentAsync(
        string reservaPublicIdOrLegacyId,
        string voucherPublicIdOrLegacyId,
        string? passengerPublicIdOrLegacyId,
        VoucherExceptionRequest? exception,
        OperationActor actor,
        CancellationToken cancellationToken)
    {
        await EnsureActorCanAsync(actor, Permissions.VouchersSend, cancellationToken);

        var reservaId = await ResolveReservaIdAsync(reservaPublicIdOrLegacyId, cancellationToken);
        var voucher = await LoadVoucherGraphAsync(voucherPublicIdOrLegacyId, cancellationToken);
        EnsureVoucherIsNotRevoked(voucher);

        if (voucher.ReservaId != reservaId)
        {
            throw new InvalidOperationException("El voucher no corresponde a la reserva seleccionada.");
        }

        if (voucher.Reserva is null)
        {
            throw new InvalidOperationException("El voucher no tiene reserva asociada.");
        }

        if (!voucher.CanBeSent())
        {
            throw new InvalidOperationException("El voucher no esta emitido ni habilitado para envio.");
        }

        if (!string.IsNullOrWhiteSpace(passengerPublicIdOrLegacyId))
        {
            var passengerId = await ResolvePassengerIdAsync(passengerPublicIdOrLegacyId, cancellationToken);
            var belongsToReserva = voucher.Reserva.Passengers.Any(passenger => passenger.Id == passengerId);
            if (!belongsToReserva)
            {
                throw new InvalidOperationException("El pasajero no corresponde a la reserva seleccionada.");
            }

            var voucherForPassenger = voucher.Scope == VoucherScopes.Reservation ||
                voucher.Scope == VoucherScopes.AllPassengers ||
                voucher.PassengerAssignments.Any(assignment => assignment.PassengerId == passengerId);

            if (!voucherForPassenger)
            {
                throw new InvalidOperationException("El voucher no corresponde al pasajero seleccionado.");
            }
        }

        var authorization = await ValidateExceptionalAuthorizationAsync(
            voucher.Reserva,
            exception?.ExceptionalReason,
            exception?.AuthorizedBySuperiorUserId,
            actor,
            cancellationToken);

        if (authorization.WasExceptional)
        {
            AddVoucherAudit(
                voucher,
                voucher.Reserva,
                VoucherAuditActions.ExceptionalSend,
                actor,
                authorization.Reason,
                authorization.SuperiorUserId,
                authorization.SuperiorUserName,
                "Reserva con saldo pendiente al enviar voucher.");

            await _db.SaveChangesAsync(cancellationToken);
        }

        return MapVoucher(voucher);
    }

    public async Task RecordVoucherSentAsync(
        string voucherPublicIdOrLegacyId,
        OperationActor actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        var voucher = await LoadVoucherGraphAsync(voucherPublicIdOrLegacyId, cancellationToken);
        if (voucher.Reserva is null)
        {
            throw new InvalidOperationException("El voucher no tiene reserva asociada.");
        }

        EnsureVoucherIsNotRevoked(voucher);

        AddVoucherAudit(voucher, voucher.Reserva, VoucherAuditActions.Sent, actor, NormalizeOptionalReason(reason), null, null, null);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<(byte[] Bytes, string ContentType, string FileName)> DownloadVoucherAsync(
        string voucherPublicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        var voucher = await LoadVoucherGraphAsync(voucherPublicIdOrLegacyId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(voucher.StoredFileName))
        {
            return await _fileStoragePort.GetAsync(voucher.StoredFileName, voucher.FileName, voucher.ContentType, cancellationToken);
        }

        if (voucher.Reserva is null)
        {
            throw new InvalidOperationException("El voucher no tiene reserva asociada.");
        }

        var bytes = await GenerateVoucherPdfAsync(voucher.Reserva.PublicId.ToString(), cancellationToken);
        return (bytes, "application/pdf", voucher.FileName);
    }

    private async Task<int> ResolveReservaIdAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var resolved = await _db.Reservas
            .AsNoTracking()
            .ResolveInternalIdAsync(reservaPublicIdOrLegacyId, cancellationToken);

        if (!resolved.HasValue && int.TryParse(reservaPublicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException("Reserva no encontrada.");
    }

    private async Task<(Reserva Reserva, AgencySettings? Agency)> LoadVoucherDataAsync(int reservaId, CancellationToken cancellationToken)
    {
        var reserva = await _db.Reservas
            .Include(f => f.Payer)
            .Include(f => f.Passengers)
            .Include(f => f.HotelBookings).ThenInclude(h => h.Supplier)
            .Include(f => f.FlightSegments)
            .Include(f => f.TransferBookings).ThenInclude(t => t.Supplier)
            .Include(f => f.PackageBookings).ThenInclude(p => p.Supplier)
            .FirstOrDefaultAsync(f => f.Id == reservaId, cancellationToken)
            ?? throw new KeyNotFoundException($"Reserva {reservaId} no encontrada.");

        var agency = await _db.AgencySettings.FirstOrDefaultAsync(cancellationToken);
        return (reserva, agency);
    }

    private async Task<Reserva> LoadReservaWithPassengersAsync(int reservaId, CancellationToken cancellationToken)
    {
        return await _db.Reservas
            .Include(r => r.Payer)
            .Include(r => r.Passengers)
            .FirstOrDefaultAsync(r => r.Id == reservaId, cancellationToken)
            ?? throw new KeyNotFoundException("Reserva no encontrada.");
    }

    private async Task<Voucher> LoadVoucherGraphAsync(string voucherPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var voucherId = await ResolveVoucherIdAsync(voucherPublicIdOrLegacyId, cancellationToken);
        return await _db.Vouchers
            .Include(v => v.Reserva)
                .ThenInclude(r => r!.Passengers)
            .Include(v => v.Reserva)
                .ThenInclude(r => r!.Payer)
            .Include(v => v.PassengerAssignments)
                .ThenInclude(a => a.Passenger)
            .FirstOrDefaultAsync(v => v.Id == voucherId, cancellationToken)
            ?? throw new KeyNotFoundException("Voucher no encontrado.");
    }

    private async Task<int> ResolveVoucherIdAsync(string voucherPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var resolved = await _db.Vouchers
            .AsNoTracking()
            .ResolveInternalIdAsync(voucherPublicIdOrLegacyId, cancellationToken);

        if (!resolved.HasValue && int.TryParse(voucherPublicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException("Voucher no encontrado.");
    }

    private async Task<int> ResolvePassengerIdAsync(string passengerPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var resolved = await _db.Passengers
            .AsNoTracking()
            .ResolveInternalIdAsync(passengerPublicIdOrLegacyId, cancellationToken);

        if (!resolved.HasValue && int.TryParse(passengerPublicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException("Pasajero no encontrado.");
    }

    private async Task<VoucherDto> GetVoucherDtoAsync(int voucherId, CancellationToken cancellationToken)
    {
        var voucher = await _db.Vouchers
            .AsNoTracking()
            .Include(v => v.Reserva)
            .Include(v => v.PassengerAssignments)
                .ThenInclude(a => a.Passenger)
            .FirstAsync(v => v.Id == voucherId, cancellationToken);

        return MapVoucher(voucher);
    }

    private async Task<List<Passenger>> ResolvePassengerAssignmentsAsync(
        Reserva reserva,
        string scope,
        IReadOnlyCollection<string> passengerPublicIds,
        CancellationToken cancellationToken)
    {
        scope = NormalizeScope(scope);
        if (scope == VoucherScopes.Reservation)
        {
            return new List<Passenger>();
        }

        if (scope == VoucherScopes.AllPassengers)
        {
            return reserva.Passengers.OrderBy(p => p.FullName).ToList();
        }

        if (passengerPublicIds.Count == 0)
        {
            throw new InvalidOperationException("Debe seleccionar al menos un pasajero para este alcance.");
        }

        var passengers = new List<Passenger>();
        foreach (var passengerPublicId in passengerPublicIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var passengerId = await ResolvePassengerIdAsync(passengerPublicId, cancellationToken);
            var passenger = reserva.Passengers.FirstOrDefault(item => item.Id == passengerId);
            if (passenger is null)
            {
                throw new InvalidOperationException("Uno de los pasajeros seleccionados no corresponde a la reserva.");
            }

            passengers.Add(passenger);
        }

        return passengers.OrderBy(p => p.FullName).ToList();
    }

    private static void EnsureReservaHasPassengers(Reserva reserva)
    {
        if (reserva.Passengers.Count == 0)
        {
            throw new InvalidOperationException("No se puede crear un voucher en una reserva sin pasajeros.");
        }
    }

    private async Task EnsureNoPassengerDuplicateAsync(
        int reservaId,
        IReadOnlyCollection<Passenger> passengers,
        string scope,
        CancellationToken cancellationToken)
    {
        // Para scopes que asignan pasajeros específicos, verificar que ninguno ya tenga
        // un voucher activo (no anulado) en la misma reserva.
        if (scope == VoucherScopes.Reservation)
        {
            return;
        }

        // Obtener todos los PassengerIds activos ya asignados en esta reserva
        var alreadyAssignedPassengerIds = await _db.Vouchers
            .AsNoTracking()
            .Where(v => v.ReservaId == reservaId && v.Status != VoucherStatuses.Revoked)
            .SelectMany(v => v.PassengerAssignments.Select(a => a.PassengerId))
            .ToListAsync(cancellationToken);

        if (alreadyAssignedPassengerIds.Count == 0)
        {
            return;
        }

        var alreadyAssigned = passengers
            .Where(p => alreadyAssignedPassengerIds.Contains(p.Id))
            .Select(p => p.FullName)
            .ToList();

        if (alreadyAssigned.Count > 0)
        {
            var names = string.Join(", ", alreadyAssigned);
            throw new InvalidOperationException(
                $"Los siguientes pasajeros ya tienen un voucher activo en esta reserva: {names}.");
        }
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

    private async Task<ExceptionalAuthorization> ValidateExceptionalAuthorizationAsync(
        Reserva reserva,
        string? exceptionalReason,
        string? superiorUserId,
        OperationActor actor,
        CancellationToken cancellationToken)
    {
        if (!ReservationEconomicPolicy.HasOutstandingBalance(reserva))
        {
            return ExceptionalAuthorization.NotRequired;
        }

        var reason = NormalizeOptionalReason(exceptionalReason);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10)
        {
            throw new InvalidOperationException("Debe indicar un motivo de excepcion de al menos 10 caracteres porque la reserva tiene saldo pendiente.");
        }

        if (actor.IsAdmin)
        {
            return new ExceptionalAuthorization(true, reason, null, null, ReservationEconomicPolicy.RoundCurrency(reserva.Balance));
        }

        if (string.IsNullOrWhiteSpace(superiorUserId))
        {
            throw new InvalidOperationException("Debe indicar el superior que autoriza la excepcion.");
        }

        var superior = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == superiorUserId && user.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("El superior autorizante no existe o esta inactivo.");

        var superiorRoles = await _db.UserRoles
            .AsNoTracking()
            .Where(userRole => userRole.UserId == superior.Id)
            .Join(_db.Roles.AsNoTracking(), userRole => userRole.RoleId, role => role.Id, (_, role) => role.Name!)
            .Where(roleName => roleName != null)
            .ToListAsync(cancellationToken);

        var superiorIsAdmin = superiorRoles.Any(role => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase));
        var superiorCanAuthorize = superiorIsAdmin || await _db.RolePermissions
            .AsNoTracking()
            .AnyAsync(permission =>
                superiorRoles.Contains(permission.RoleName) &&
                permission.Permission == Permissions.VouchersAuthorizeException,
                cancellationToken);

        if (!superiorCanAuthorize)
        {
            throw new InvalidOperationException("El superior indicado no tiene permiso para autorizar excepciones de vouchers.");
        }

        return new ExceptionalAuthorization(
            true,
            reason,
            superior.Id,
            superior.FullName,
            ReservationEconomicPolicy.RoundCurrency(reserva.Balance));
    }

    private void AddVoucherAudit(
        Voucher voucher,
        Reserva reserva,
        string action,
        OperationActor actor,
        string? reason,
        string? superiorUserId,
        string? superiorUserName,
        string? details)
    {
        var hadOutstandingBalance = ReservationEconomicPolicy.HasOutstandingBalance(reserva);
        var entry = new VoucherAuditEntry
        {
            Voucher = voucher,
            ReservaId = reserva.Id,
            Action = action,
            UserId = actor.UserId,
            UserName = actor.UserName,
            OccurredAt = DateTime.UtcNow,
            Reason = NormalizeOptionalReason(reason),
            ReservationHadOutstandingBalance = hadOutstandingBalance,
            OutstandingBalance = ReservationEconomicPolicy.RoundCurrency(reserva.Balance),
            AuthorizedBySuperiorUserId = superiorUserId,
            AuthorizedBySuperiorUserName = superiorUserName,
            Details = details
        };

        voucher.AuditEntries.Add(entry);
        _db.VoucherAuditEntries.Add(entry);
    }

    private static VoucherDto MapVoucher(Voucher voucher)
    {
        var reserva = voucher.Reserva;
        var balance = reserva is null ? 0m : ReservationEconomicPolicy.RoundCurrency(reserva.Balance);
        return new VoucherDto
        {
            PublicId = voucher.PublicId,
            ReservaPublicId = reserva?.PublicId ?? Guid.Empty,
            NumeroReserva = reserva?.NumeroReserva ?? string.Empty,
            Source = voucher.Source,
            Status = voucher.Status,
            Scope = voucher.Scope,
            FileName = voucher.FileName,
            ContentType = voucher.ContentType,
            FileSize = voucher.FileSize,
            ExternalOrigin = voucher.ExternalOrigin,
            IsEnabledForSending = voucher.IsEnabledForSending,
            CanSend = voucher.CanBeSent(),
            ReservationHasOutstandingBalance = reserva is not null && ReservationEconomicPolicy.HasOutstandingBalance(reserva),
            OutstandingBalance = balance,
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
                .Where(a => a.Passenger != null)
                .Select(a => a.Passenger!.PublicId)
                .ToList(),
            PassengerNames = voucher.PassengerAssignments
                .Where(a => a.Passenger != null)
                .Select(a => a.Passenger!.FullName)
                .OrderBy(name => name)
                .ToList()
        };
    }

    private static string NormalizeScope(string? scope)
    {
        if (string.Equals(scope, VoucherScopes.AllPassengers, StringComparison.OrdinalIgnoreCase))
        {
            return VoucherScopes.AllPassengers;
        }

        if (string.Equals(scope, VoucherScopes.SelectedPassengers, StringComparison.OrdinalIgnoreCase))
        {
            return VoucherScopes.SelectedPassengers;
        }

        return VoucherScopes.Reservation;
    }

    private static string? NormalizeOptionalReason(string? reason)
    {
        var normalized = reason?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeRequiredReason(string? reason, string errorMessage)
    {
        var normalized = NormalizeOptionalReason(reason);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 10)
        {
            throw new InvalidOperationException(errorMessage);
        }

        return normalized;
    }

    private static void EnsureVoucherIsNotRevoked(Voucher voucher)
    {
        if (voucher.Status == VoucherStatuses.Revoked)
        {
            throw new InvalidOperationException("El voucher esta anulado y no admite acciones operativas.");
        }
    }

    private static void ValidateVoucherUpload(string fileName, string contentType, byte[] bytes, long declaredSize)
    {
        const long maxVoucherSizeBytes = 25 * 1024 * 1024;
        if (bytes.Length == 0 || declaredSize == 0)
        {
            throw new InvalidOperationException("El archivo esta vacio.");
        }

        if (bytes.Length > maxVoucherSizeBytes || declaredSize > maxVoucherSizeBytes)
        {
            throw new InvalidOperationException("El archivo supera el tamano maximo permitido de 25 MB.");
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var allowed = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = new[] { "application/pdf" },
            [".png"] = new[] { "image/png" },
            [".jpg"] = new[] { "image/jpeg" },
            [".jpeg"] = new[] { "image/jpeg" },
            [".doc"] = new[] { "application/msword" },
            [".docx"] = new[] { "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/zip" },
            [".xls"] = new[] { "application/vnd.ms-excel" },
            [".xlsx"] = new[] { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/zip" }
        };

        if (!allowed.TryGetValue(extension, out var allowedContentTypes) ||
            !allowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("El tipo de voucher no esta permitido.");
        }
    }

    private static string SanitizeOriginalFileName(string fileName)
    {
        var original = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(original))
        {
            original = "voucher";
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            original = original.Replace(invalidChar, '_');
        }

        return original;
    }

    private sealed record ExceptionalAuthorization(
        bool WasExceptional,
        string? Reason,
        string? SuperiorUserId,
        string? SuperiorUserName,
        decimal OutstandingBalance)
    {
        public static ExceptionalAuthorization NotRequired { get; } = new(false, null, null, null, 0m);
    }

    private static void ComposeHeader(IContainer container, Reserva reserva, AgencySettings? agency)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text(agency?.AgencyName ?? "Agencia de Viajes").FontSize(22).Bold().FontColor(Colors.Blue.Medium);
                    if (!string.IsNullOrWhiteSpace(agency?.Address))
                        left.Item().Text(agency.Address).FontSize(9).FontColor(Colors.Grey.Darken1);
                    if (!string.IsNullOrWhiteSpace(agency?.Phone) || !string.IsNullOrWhiteSpace(agency?.Email))
                        left.Item().Text($"{agency?.Phone ?? "-"} | {agency?.Email ?? "-"}").FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(170).AlignRight().Column(right =>
                {
                    right.Item().Text("VOUCHER").FontSize(20).Bold();
                    right.Item().Text($"Reserva {reserva.NumeroReserva}").FontSize(12).SemiBold().FontColor(Colors.Blue.Medium);
                    right.Item().Text($"Salida: {FormatDate(reserva.StartDate)}").FontSize(9);
                    right.Item().Text($"Regreso: {FormatDate(reserva.EndDate)}").FontSize(9);
                });
            });

            column.Item().PaddingTop(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
        });
    }

    private static void ComposeContent(IContainer container, Reserva reserva)
    {
        container.Column(column =>
        {
            column.Spacing(12);

            column.Item().Element(card =>
            {
                card.Border(1).BorderColor(Colors.Grey.Lighten2).Padding(10).Column(details =>
                {
                    details.Item().Text("Datos del pasajero").SemiBold().FontSize(13);
                    details.Item().Text($"Titular: {reserva.Payer?.FullName ?? "---"}");
                    details.Item().Text($"Documento: {reserva.Payer?.DocumentNumber ?? "---"}");
                });
            });

            if (reserva.Passengers.Any())
                column.Item().Element(x => ComposeSimpleTable(x, "Pasajeros", new[] { "Nombre", "Documento", "Nacimiento" },
                    reserva.Passengers.Select(p => new[]
                    {
                        p.FullName,
                        $"{p.DocumentType} {p.DocumentNumber}".Trim(),
                        FormatDate(p.BirthDate)
                    })));

            if (reserva.HotelBookings.Any())
                column.Item().Element(x => ComposeSimpleTable(x, "Alojamiento", new[] { "Hotel", "Fechas", "Habitacion", "Confirmacion" },
                    reserva.HotelBookings.Select(h => new[]
                    {
                        $"{h.HotelName} ({h.City})",
                        $"{FormatDate(h.CheckIn)} - {FormatDate(h.CheckOut)}",
                        $"{h.RoomType} / {h.MealPlan}",
                        // Si no hay codigo de confirmacion mostramos el estado real del servicio
                        // (ej. "Confirmado", "Solicitado", "Cancelado") en vez de "Pendiente".
                        h.ConfirmationNumber ?? h.Status ?? "-"
                    })));

            if (reserva.FlightSegments.Any())
                column.Item().Element(x => ComposeSimpleTable(x, "Vuelos", new[] { "Vuelo", "Ruta", "Salida", "PNR" },
                    reserva.FlightSegments.Select(f => new[]
                    {
                        $"{f.AirlineCode} {f.FlightNumber}",
                        $"{f.OriginCity ?? f.Origin} -> {f.DestinationCity ?? f.Destination}",
                        f.DepartureTime.ToString("dd/MM/yyyy HH:mm"),
                        f.PNR ?? "---"
                    })));

            if (reserva.TransferBookings.Any())
                column.Item().Element(x => ComposeSimpleTable(x, "Traslados", new[] { "Vehiculo", "Ruta", "Fecha", "Confirmacion" },
                    reserva.TransferBookings.Select(t => new[]
                    {
                        t.VehicleType,
                        $"{t.PickupLocation} -> {t.DropoffLocation}",
                        t.PickupDateTime.ToString("dd/MM/yyyy HH:mm"),
                        // Sin codigo => mostramos el estado real del servicio en vez de "Pendiente".
                        t.ConfirmationNumber ?? t.Status ?? "-"
                    })));

            if (reserva.PackageBookings.Any())
                column.Item().Element(x => ComposeSimpleTable(x, "Paquetes", new[] { "Paquete", "Destino", "Fechas", "Confirmacion" },
                    reserva.PackageBookings.Select(p => new[]
                    {
                        p.PackageName,
                        p.Destination,
                        $"{FormatDate(p.StartDate)} - {FormatDate(p.EndDate)}",
                        // Sin codigo => mostramos el estado real del servicio en vez de "Pendiente".
                        p.ConfirmationNumber ?? p.Status ?? "-"
                    })));

            column.Item().PaddingTop(6).Text("Este documento no tiene validez como comprobante fiscal.")
                .Italic().FontSize(9).FontColor(Colors.Grey.Darken1);
        });
    }

    private static void ComposeSimpleTable(IContainer container, string title, string[] headers, IEnumerable<string[]> rows)
    {
        container.Column(column =>
        {
            column.Item().Text(title).SemiBold().FontSize(13);
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    foreach (var _ in headers)
                        columns.RelativeColumn();
                });

                table.Header(header =>
                {
                    foreach (var item in headers)
                    {
                        header.Cell().Element(CellHeaderStyle).Text(item);
                    }
                });

                foreach (var row in rows)
                {
                    foreach (var value in row)
                    {
                        table.Cell().Element(CellBodyStyle).Text(value);
                    }
                }
            });
        });

        static IContainer CellHeaderStyle(IContainer container) =>
            container.Background(Colors.Grey.Lighten3).Padding(6).DefaultTextStyle(x => x.SemiBold().FontSize(9));

        static IContainer CellBodyStyle(IContainer container) =>
            container.BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(6).DefaultTextStyle(x => x.FontSize(9));
    }

    private static void AppendPassengers(Reserva reserva, StringBuilder html)
    {
        if (!reserva.Passengers.Any()) return;

        html.AppendLine("<h2>Pasajeros</h2><div class='table-container'><table><thead><tr><th>Nombre Completo</th><th>Documento</th><th>Fecha Nac.</th></tr></thead><tbody>");
        foreach (var p in reserva.Passengers)
        {
            html.AppendLine($"<tr><td style='font-weight:600'>{EscapeHtml(p.FullName)}</td><td>{EscapeHtml($"{p.DocumentType} {p.DocumentNumber}".Trim())}</td><td>{FormatDate(p.BirthDate)}</td></tr>");
        }
        html.AppendLine("</tbody></table></div>");
    }

    private static void AppendHotels(Reserva reserva, StringBuilder html)
    {
        if (!reserva.HotelBookings.Any()) return;

        html.AppendLine("<h2>Alojamiento</h2><div class='table-container'><table><thead><tr><th>Hotel / Destino</th><th>Fechas</th><th>Noches</th><th>Habitación / Régimen</th><th>Estado</th></tr></thead><tbody>");
        foreach (var h in reserva.HotelBookings)
        {
            var isConfirmed = h.Status == "Confirmed" || h.Status == "Confirmado";
            html.AppendLine($"<tr><td style='font-weight:600'>{EscapeHtml(h.HotelName)}<br/><span style='font-size:11px;color:#64748b;font-weight:400'>{EscapeHtml(h.City)}</span></td><td>{h.CheckIn:dd/MM/yyyy} - {h.CheckOut:dd/MM/yyyy}</td><td>{h.Nights}</td><td>{EscapeHtml($"{h.RoomType} ({h.MealPlan})")}</td>");
            html.AppendLine($"<td><span class='status-pill {(isConfirmed ? "status-confirmed" : "status-pending")}'>{EscapeHtml(h.Status ?? "-")}</span></td></tr>");
        }
        html.AppendLine("</tbody></table></div>");
    }

    private static void AppendFlights(Reserva reserva, StringBuilder html)
    {
        if (!reserva.FlightSegments.Any()) return;

        html.AppendLine("<h2>Vuelos</h2><div class='table-container'><table><thead><tr><th>Vuelo</th><th>Origen</th><th>Destino</th><th>Salida</th><th>Clase</th><th>PNR</th></tr></thead><tbody>");
        foreach (var f in reserva.FlightSegments)
        {
            html.AppendLine($"<tr><td style='font-weight:600'>{EscapeHtml($"{f.AirlineCode} {f.FlightNumber}")}</td><td>{EscapeHtml(f.OriginCity ?? f.Origin)}</td><td>{EscapeHtml(f.DestinationCity ?? f.Destination)}</td><td>{f.DepartureTime:dd/MM/yyyy HH:mm}</td><td>{EscapeHtml(f.CabinClass)}</td><td style='font-family:monospace;font-weight:700'>{EscapeHtml(f.PNR ?? "---")}</td></tr>");
        }
        html.AppendLine("</tbody></table></div>");
    }

    private static void AppendTransfers(Reserva reserva, StringBuilder html)
    {
        if (!reserva.TransferBookings.Any()) return;

        html.AppendLine("<h2>Traslados</h2><div class='table-container'><table><thead><tr><th>Tipo de Servicio</th><th>Recogida</th><th>Destino</th><th>Fecha y Hora</th><th>Confirmación</th></tr></thead><tbody>");
        foreach (var t in reserva.TransferBookings)
        {
            html.AppendLine($"<tr><td style='font-weight:600'>{EscapeHtml(t.VehicleType)}</td><td>{EscapeHtml(t.PickupLocation)}</td><td>{EscapeHtml(t.DropoffLocation)}</td><td>{t.PickupDateTime:dd/MM/yyyy HH:mm}</td><td style='font-weight:600'>{EscapeHtml(t.ConfirmationNumber ?? t.Status ?? "-")}</td></tr>");
        }
        html.AppendLine("</tbody></table></div>");
    }

    private static void AppendPackages(Reserva reserva, StringBuilder html)
    {
        if (!reserva.PackageBookings.Any()) return;

        html.AppendLine("<h2>Paquetes Turísticos</h2><div class='table-container'><table><thead><tr><th>Paquete</th><th>Destino</th><th>Fechas</th><th>Noches</th><th>Estado</th></tr></thead><tbody>");
        foreach (var p in reserva.PackageBookings)
        {
            var isConfirmed = p.Status == "Confirmed" || p.Status == "Confirmado";
            html.AppendLine($"<tr><td style='font-weight:600'>{EscapeHtml(p.PackageName)}</td><td>{EscapeHtml(p.Destination)}</td><td>{p.StartDate:dd/MM/yyyy} - {p.EndDate:dd/MM/yyyy}</td><td>{p.Nights}</td>");
            html.AppendLine($"<td><span class='status-pill {(isConfirmed ? "status-confirmed" : "status-pending")}'>{EscapeHtml(p.Status ?? "-")}</span></td></tr>");
        }
        html.AppendLine("</tbody></table></div>");
    }

    private static string FormatDate(DateTime? date)
    {
        return date?.ToString("dd/MM/yyyy") ?? "---";
    }

    private static string BuildVoucherFileName(Reserva reserva)
    {
        var reservationNumber = string.IsNullOrWhiteSpace(reserva.NumeroReserva)
            ? reserva.PublicId.ToString("N")[..8]
            : reserva.NumeroReserva;
        var safeNumber = new string(reservationNumber
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .ToArray())
            .Trim('-');

        return $"voucher-{(string.IsNullOrWhiteSpace(safeNumber) ? reserva.PublicId.ToString("N")[..8] : safeNumber)}.pdf";
    }

    private static string EscapeHtml(string? value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }
}
