using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.2.1 v3 §6.1 (2026-05-17): orquestador del flujo de cancelacion de
/// reservas. Implementa <see cref="IBookingCancellationService"/> y la interface
/// chica <see cref="IInvoiceAnnulmentBcBridge"/> simultaneamente (rompe el ciclo
/// DI BR-V2-04: <c>InvoiceService</c> inyecta solo la bridge, no el contrato
/// completo).
///
/// <para>
/// <b>Patron de transacciones</b>: cada metodo abre una unidad de trabajo que
/// commitea con un solo <c>SaveChangesAsync</c> al final (HC1 plan v3). Esto
/// garantiza que TODOS los efectos del paso (estado BC + estado Reserva +
/// approval consumed + audit log) sean atomicos. Si algo falla en el medio,
/// EF rollbackea automaticamente porque nada se commiteo.
/// </para>
///
/// <para>
/// <b>Llamada a InvoiceService dentro del flujo</b>: <c>ConfirmAsync</c> ejecuta
/// <c>EnqueueAnnulmentAsync</c> que internamente hace su propio
/// <c>SaveChangesAsync</c>. No corremos en una transaccion comun. Esto es
/// intencional: la annulacion fiscal queda persistida (Pending) aunque alguna
/// rama posterior falle. El BC podria quedar en estado inconsistente (Drafted
/// con NC en vuelo) → la remediacion es manual (audit visible + soporte).
/// El riesgo es chico porque el SaveChanges interno del BC viene <b>antes</b>
/// de EnqueueAnnulmentAsync, asi que el BC ya esta en AwaitingFiscalConfirmation
/// cuando el job arranca.
/// </para>
/// </summary>
public class BookingCancellationService : IBookingCancellationService, IInvoiceAnnulmentBcBridge
{
    private readonly AppDbContext _db;
    private readonly IInvoiceService _invoiceService;
    private readonly IApprovalRequestService _approvalService;
    private readonly IAuditService _auditService;
    private readonly ILogger<BookingCancellationService> _logger;
    private readonly IOperationalFinanceSettingsService _settings;

    public BookingCancellationService(
        AppDbContext db,
        IInvoiceService invoiceService,
        IApprovalRequestService approvalService,
        IAuditService auditService,
        ILogger<BookingCancellationService> logger,
        IOperationalFinanceSettingsService settings)
    {
        _db = db;
        _invoiceService = invoiceService;
        _approvalService = approvalService;
        _auditService = auditService;
        _logger = logger;
        _settings = settings;
    }

    // =========================================================================
    // Comandos publicos (IBookingCancellationService)
    // =========================================================================

    public async Task<BookingCancellationDto> DraftAsync(
        DraftCancellationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        // 1) Resolver la Reserva por PublicId. Includes: Payer (Customer) para
        //    inferir CustomerId, servicios para inferir SupplierId.
        var reserva = await _db.Reservas
            .Include(r => r.Payer)
            .FirstOrDefaultAsync(r => r.PublicId == request.ReservaPublicId, ct)
            ?? throw new KeyNotFoundException($"Reserva {request.ReservaPublicId} no encontrada.");

        // 2) Localizar la Invoice activa de la reserva. Usamos la mas reciente
        //    no anulada. Si <c>OnePerReservaInvoicePolicy</c> esta on y hay
        //    multiples activas: rechazar con INV-100 (review BR4 — el patron
        //    de FC1 deja una Invoice por reserva en estado normal).
        var settings = await _settings.GetEntityAsync(ct);

        var activeInvoices = await _db.Invoices
            .Where(i => i.ReservaId == reserva.Id
                     && i.AnnulmentStatus != AnnulmentStatus.Succeeded)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        if (activeInvoices.Count == 0)
            throw new InvalidOperationException(
                $"La reserva {reserva.NumeroReserva} no tiene factura activa para anular.");

        if (settings.OnePerReservaInvoicePolicy && activeInvoices.Count > 1)
            throw new BusinessInvariantViolationException(
                "La reserva tiene multiples facturas activas. Solo se soporta " +
                "cancelacion con una factura por reserva (OnePerReservaInvoicePolicy=true).",
                invariantCode: "INV-100");

        var originatingInvoice = activeInvoices[0];

        // 3) INV-081: una sola cancelacion activa por reserva. El UNIQUE de la
        //    BD ya bloquea, pero validamos antes para devolver un mensaje claro
        //    (sin esto el caller se come una DbUpdateException criptica).
        var existingBc = await _db.BookingCancellations
            .Where(b => b.ReservaId == reserva.Id)
            .FirstOrDefaultAsync(ct);
        if (existingBc != null)
            throw new BusinessInvariantViolationException(
                $"La reserva {reserva.NumeroReserva} ya tiene una cancelacion ({existingBc.Status}).",
                invariantCode: "INV-081");

        // 4) MIG2 (plan v3): si la Invoice original ya esta anulada (Succeeded),
        //    no tiene sentido cancelar.
        if (originatingInvoice.AnnulmentStatus == AnnulmentStatus.Succeeded)
            throw new BusinessInvariantViolationException(
                "La factura original ya fue anulada (NC aprobada). No se puede cancelar la reserva sobre una factura muerta.",
                invariantCode: "INV-100");

        // 5) Inferir Customer y Supplier:
        //    - Customer: el Payer de la reserva.
        //    - Supplier: la Invoice puede tener uno, pero la entidad Invoice no
        //      lo expone directamente. Usamos el primer Supplier vinculado a
        //      ServiciosReservados (si existe). Fallback: requerimos un Supplier
        //      explicito en el request (futuro: cuando agreguemos
        //      DraftCancellationRequest.SupplierPublicId).
        if (reserva.PayerId is null)
            throw new InvalidOperationException(
                $"La reserva {reserva.NumeroReserva} no tiene Payer asignado. No se puede crear cancelacion.");

        // DbSet se llama "Servicios" en AppDbContext aunque la clase sea
        // ServicioReserva (decision historica del repo).
        var supplierId = await _db.Servicios
            .Where(s => s.ReservaId == reserva.Id && s.SupplierId != null)
            .Select(s => (int?)s.SupplierId!)
            .FirstOrDefaultAsync(ct);
        if (supplierId is null)
            throw new InvalidOperationException(
                $"La reserva {reserva.NumeroReserva} no tiene servicios con Supplier asignado. " +
                "Se requiere al menos un servicio con operador para registrar la cancelacion.");

        // 6) Calcular AmountPaidAtCancellation: suma de pagos activos
        //    (no soft-deleted y con Status != "Cancelled") de la reserva.
        //    Es informativo; el monto real del refund se determina al momento
        //    de Confirm + allocations.
        var amountPaid = await _db.Payments
            .Where(p => p.ReservaId == reserva.Id
                     && !p.IsDeleted
                     && p.Status != "Cancelled")
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = reserva.PayerId.Value,
            SupplierId = supplierId.Value,
            OriginatingInvoiceId = originatingInvoice.Id,
            Status = BookingCancellationStatus.Drafted,
            Reason = request.Reason.Trim(),
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = userId,
            DraftedByUserName = userName,
            AmountPaidAtCancellation = amountPaid,
            EstimatedRefundAmount = amountPaid,
            ReceivedRefundAmount = 0m,
            // Snapshot vacio explicito: en Drafted el CHECK SQL permite valores
            // por defecto. ConfirmAsync lo completa antes de pasar a T0.
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.Unset,
                FetchedAt = default,
            },
            IsLegacyPreCancellationModel = false,
        };

        _db.BookingCancellations.Add(bc);
        await _db.SaveChangesAsync(ct);

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationDrafted,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                ReservaPublicId = reserva.PublicId,
                bc.Reason,
                bc.AmountPaidAtCancellation,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException("BC no encontrada despues de crearla. Estado inconsistente.");
    }

    public async Task<BookingCancellationDto> ConfirmAsync(
        Guid publicId,
        ConfirmCancellationRequest request,
        string userId,
        string? userName,
        // requesterIsAdmin: flag informativo del rol del caller (lo setea el
        // controller con User.IsInRole("Admin")). NO se usa para saltear el
        // workflow de approval del InvoiceAnnulment — ese bypass depende del
        // override del BC (approvalRequest != null), no de este flag.
        // Lo mantenemos en la firma para forward-compatibility con futuros
        // checks de policy y para mantener simetria con IPaymentService /
        // IInvoiceService que tambien lo aceptan.
        bool requesterIsAdmin,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        // 1) Cargar BC con todos los includes necesarios.
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.OriginatingInvoice)
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // 2) Solo se confirma desde Drafted.
        if (bc.Status != BookingCancellationStatus.Drafted)
            throw new BusinessInvariantViolationException(
                $"Solo se puede confirmar una cancelacion en estado Drafted (actual: {bc.Status}).",
                invariantCode: "INV-093");

        // 3) MIG2: la Invoice original ya esta anulada → bloquear.
        if (bc.OriginatingInvoice.AnnulmentStatus == AnnulmentStatus.Succeeded)
            throw new BusinessInvariantViolationException(
                "La factura original ya fue anulada (NC aprobada). No se puede confirmar la cancelacion.",
                invariantCode: "INV-100");

        // 4) Override admin: si IsAdminOverride=true, buscar approval.
        ApprovalRequest? approvalRequest = null;
        if (request.IsAdminOverride)
        {
            if (string.IsNullOrWhiteSpace(request.OverrideReason) || request.OverrideReason.Trim().Length < 20)
                throw new BusinessInvariantViolationException(
                    "OverrideReason requerido (min 20 chars) cuando IsAdminOverride=true.");

            if (request.ApprovalRequestPublicId is null)
                throw new ApprovalRequiredException(
                    ApprovalRequestType.InvariantOverride,
                    "BookingCancellation",
                    bc.Id);

            approvalRequest = await _db.ApprovalRequests
                .FirstOrDefaultAsync(a => a.PublicId == request.ApprovalRequestPublicId, ct)
                ?? throw new ApprovalRequiredException(
                    ApprovalRequestType.InvariantOverride, "BookingCancellation", bc.Id);

            // Validacion de coherencia approval ↔ BC. Si el admin trae un approval
            // que apunta a otra entidad, lo rechazamos.
            var validForBc = approvalRequest.RequestType == ApprovalRequestType.InvariantOverride
                          && approvalRequest.EntityType == "BookingCancellation"
                          && approvalRequest.EntityId == bc.Id
                          && approvalRequest.Status == ApprovalStatus.Approved
                          && approvalRequest.RequestedByUserId == userId
                          && approvalRequest.ExpiresAt > DateTime.UtcNow;
            if (!validForBc)
                throw new ApprovalRequiredException(
                    ApprovalRequestType.InvariantOverride, "BookingCancellation", bc.Id);
        }

        // 5) Normalizar condiciones fiscales y validar coherencia.
        //    Si alguna queda Unknown → INV-118: snapshot inconsistente.
        var agencyCanonical = TaxConditionNormalizer.Normalize(request.SnapshotData.AgencyTaxConditionAtEvent);
        var supplierCanonical = TaxConditionNormalizer.Normalize(request.SnapshotData.SupplierTaxConditionAtEvent);
        var customerCanonical = TaxConditionNormalizer.Normalize(request.SnapshotData.CustomerTaxConditionAtEvent);
        if (agencyCanonical == TaxConditionCanonical.Unknown ||
            supplierCanonical == TaxConditionCanonical.Unknown ||
            customerCanonical == TaxConditionCanonical.Unknown)
        {
            throw new BusinessInvariantViolationException(
                "FiscalSnapshot incoherente: alguna de las condiciones fiscales " +
                "(Agency/Supplier/Customer) no se pudo normalizar a un valor canonico. " +
                "Revisa los strings enviados.",
                invariantCode: "INV-118");
        }

        // 6) Validar Source / ManualJustification:
        //    - Si Source=Manual, ManualJustification es obligatorio (INV-120).
        //    - Si Source=Unset, rechazar (no se puede confirmar sin TC explicito).
        if (request.SnapshotData.Source == ExchangeRateSource.Unset)
            throw new BusinessInvariantViolationException(
                "FiscalSnapshot.Source no puede ser Unset al confirmar la cancelacion.",
                invariantCode: "INV-118");
        if (request.SnapshotData.Source == ExchangeRateSource.Manual &&
            string.IsNullOrWhiteSpace(request.SnapshotData.ManualJustification))
            throw new BusinessInvariantViolationException(
                "ManualJustification es requerido cuando Source=Manual (INV-120).",
                invariantCode: "INV-120");

        // 7) Completar FiscalSnapshot.
        bc.FiscalSnapshot = new FiscalSnapshot
        {
            CurrencyAtEvent = request.SnapshotData.CurrencyAtEvent.ToUpperInvariant(),
            ExchangeRateAtOriginalInvoice = request.SnapshotData.ExchangeRateAtOriginalInvoice,
            Source = request.SnapshotData.Source,
            ManualJustification = request.SnapshotData.ManualJustification,
            FetchedAt = DateTime.UtcNow,
            AgencyTaxConditionAtEvent = TaxConditionNormalizer.ToStorageString(agencyCanonical),
            SupplierTaxConditionAtEvent = TaxConditionNormalizer.ToStorageString(supplierCanonical),
            CustomerTaxConditionAtEvent = TaxConditionNormalizer.ToStorageString(customerCanonical),
        };

        // 8) Transicionar BC + Reserva (HC2 plan v3: bypass UpdateStatusAsync —
        //    el state machine general no contempla la transicion lateral a
        //    PendingOperatorRefund, lo hacemos directo y dejamos el comentario
        //    para que en una review el lector entienda por que).
        bc.Status = BookingCancellationStatus.AwaitingFiscalConfirmation;
        bc.ConfirmedWithClientAt = DateTime.UtcNow;
        bc.ConfirmedByUserId = userId;
        bc.ConfirmedByUserName = userName;
        bc.OperatorRefundDueBy = DateTime.UtcNow.AddDays(
            (await _settings.GetEntityAsync(ct)).OperatorRefundTimeoutDays);

        // HC2 plan v3 §6.1 step 5: bypass UpdateStatusAsync porque
        // AllowedRevertTransitions no contempla esta salida. La transicion
        // queda visible en el audit log + la query de Reservas filtra por
        // status.
        bc.Reserva.Status = EstadoReserva.PendingOperatorRefund;

        // 9) Guardar BC + Reserva ANTES de encolar la annulacion (asi el job
        //    encuentra el BC en AwaitingFiscalConfirmation cuando arranca).
        await _db.SaveChangesAsync(ct);

        // 10) BR-V2-03 cross-reference: encolar annulacion en AFIP.
        //     Si hubo override admin, pasamos el approvalRequestId para que el
        //     InvoiceService persista la cross-reference fiscal.
        //
        //     Bypass del approval del InvoiceAnnulment (requesterIsAdmin del
        //     InvoiceService, NO confundir con el parametro homonimo de
        //     ConfirmAsync): se hace SOLO cuando el override del BC ya cubre la
        //     NC fiscal (approvalRequest != null). Cuando NO hay override, la NC
        //     tiene que pasar por su approval workflow normal — si seteamos
        //     true sin override, un caller no-admin podria emitir NCs sin
        //     control fiscal (OPS-FISCAL-001 plan v3 §13).
        //
        //     IMPORTANTE: si en el futuro el BC se invoca desde un controller
        //     que pasa requesterIsAdmin=true porque el usuario es Admin (sin
        //     necesidad de override formal), revisar si tiene sentido propagarlo
        //     aca. Hoy no, porque la unica forma de "saltear AFIP approval" es
        //     traer un approval scoped al BC.
        var crossRefReason = approvalRequest != null
            ? $"BC override {approvalRequest.PublicId}: {request.OverrideReason!.Trim()}"
            : $"BC cancellation: {bc.Reason}";

        await _invoiceService.EnqueueAnnulmentAsync(
            id: bc.OriginatingInvoiceId,
            userId: userId,
            userName: userName,
            reason: crossRefReason,
            requesterIsAdmin: approvalRequest != null,
            ct: ct,
            approvalRequestId: approvalRequest?.Id);

        // 11) Marcar el InvariantOverride como Consumed si hubo override.
        //     Importante hacerlo DESPUES de EnqueueAnnulmentAsync — si el
        //     encolado tira, no consumimos el approval.
        if (approvalRequest != null)
        {
            await _approvalService.MarkConsumedAsync(approvalRequest.Id, ct);
        }

        // 12) Audit. Incluimos approvalRequestPublicId en metadata para que el
        //     reviewer pueda cruzar audit logs con Invoice.AnnulmentApprovalRequestId.
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationConfirmed,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                ReservaPublicId = bc.Reserva.PublicId,
                approvalRequestPublicId = approvalRequest?.PublicId,
                isAdminOverride = request.IsAdminOverride,
                overrideReason = request.OverrideReason,
                fiscalSnapshot = new
                {
                    bc.FiscalSnapshot.CurrencyAtEvent,
                    bc.FiscalSnapshot.ExchangeRateAtOriginalInvoice,
                    bc.FiscalSnapshot.Source,
                    bc.FiscalSnapshot.AgencyTaxConditionAtEvent,
                    bc.FiscalSnapshot.SupplierTaxConditionAtEvent,
                    bc.FiscalSnapshot.CustomerTaxConditionAtEvent,
                },
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException("BC no encontrada despues de confirmar. Estado inconsistente.");
    }

    public async Task<BookingCancellationDto> AbortAsync(
        Guid publicId,
        string reason,
        string userId,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        var bc = await _db.BookingCancellations
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // Idempotente: si ya esta Aborted, retornamos sin tocar.
        if (bc.Status == BookingCancellationStatus.Aborted)
        {
            _logger.LogInformation(
                "AbortAsync no-op: BC {BcPublicId} ya esta Aborted.",
                bc.PublicId);
            return (await MapToDtoAsync(bc.Id, ct))!;
        }

        // Solo se aborta desde Drafted (las otras transiciones tienen side-effects fiscales).
        if (bc.Status != BookingCancellationStatus.Drafted)
            throw new BusinessInvariantViolationException(
                $"Solo se puede abortar una cancelacion en Drafted (actual: {bc.Status}).",
                invariantCode: "INV-093");

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("El motivo de abort es requerido.", nameof(reason));

        bc.Status = BookingCancellationStatus.Aborted;
        bc.ClosedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationAborted,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new { bc.PublicId, reason }),
            userId: userId,
            userName: null,
            ct: ct);

        return (await MapToDtoAsync(bc.Id, ct))!;
    }

    public async Task<BookingCancellationDto> ForceArcaConfirmationAsync(
        Guid publicId,
        ForceArcaConfirmationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        // 1) Cargar BC.
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // 2) Idempotencia: si ya esta AwaitingOperatorRefund o adelante, no-op.
        if (bc.Status == BookingCancellationStatus.AwaitingOperatorRefund ||
            bc.Status == BookingCancellationStatus.ClientCreditApplied ||
            bc.Status == BookingCancellationStatus.Closed)
        {
            _logger.LogWarning(
                "ForceArcaConfirmationAsync no-op: BC {BcPublicId} ya esta en {Status}. " +
                "Admin {UserId} intento forzar pero el flujo automatico ya transiciono.",
                bc.PublicId, bc.Status, userId);

            await _auditService.LogBusinessEventAsync(
                action: AuditActions.BookingCancellationArcaConfirmedManuallyNoOp,
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    bc.PublicId,
                    currentStatus = bc.Status.ToString(),
                    request.CreditNoteInvoicePublicId,
                    request.ApprovalRequestPublicId,
                    request.Reason,
                    attemptedByUserId = userId,
                }),
                userId: userId,
                userName: userName,
                ct: ct);

            return (await MapToDtoAsync(bc.Id, ct))!;
        }

        // 3) Solo se fuerza desde AwaitingFiscalConfirmation (es el unico estado
        //    donde tiene sentido este escape hatch).
        if (bc.Status != BookingCancellationStatus.AwaitingFiscalConfirmation)
            throw new BusinessInvariantViolationException(
                $"ForceArcaConfirmation solo se permite desde AwaitingFiscalConfirmation (actual: {bc.Status}).",
                invariantCode: "INV-093");

        // 4) Validar la NC referenciada.
        var creditNote = await _db.Invoices
            .FirstOrDefaultAsync(i => i.PublicId == request.CreditNoteInvoicePublicId, ct)
            ?? throw new InvalidOperationException(
                $"La Invoice {request.CreditNoteInvoicePublicId} no existe.");

        // NC tipos: 3 (NC A), 8 (NC B), 13 (NC C).
        var ncTipos = new[] { 3, 8, 13 };
        var isValidNc = creditNote.OriginalInvoiceId == bc.OriginatingInvoiceId
                     && ncTipos.Contains(creditNote.TipoComprobante)
                     && creditNote.Resultado == "A"
                     && !string.IsNullOrWhiteSpace(creditNote.CAE);
        if (!isValidNc)
            throw new InvalidOperationException(
                "La Invoice referenciada no es una NC valida de la factura original del BC " +
                "(verificar OriginalInvoiceId, TipoComprobante en {3,8,13}, Resultado=A, CAE presente).");

        // 5) Validar approval InvariantOverride scoped al BC.
        var approval = await _db.ApprovalRequests
            .FirstOrDefaultAsync(a => a.PublicId == request.ApprovalRequestPublicId, ct)
            ?? throw new ApprovalRequiredException(
                ApprovalRequestType.InvariantOverride, "BookingCancellation", bc.Id);

        var validForBc = approval.RequestType == ApprovalRequestType.InvariantOverride
                      && approval.EntityType == "BookingCancellation"
                      && approval.EntityId == bc.Id
                      && approval.Status == ApprovalStatus.Approved
                      && approval.RequestedByUserId == userId
                      && approval.ExpiresAt > DateTime.UtcNow;
        if (!validForBc)
            throw new ApprovalRequiredException(
                ApprovalRequestType.InvariantOverride, "BookingCancellation", bc.Id);

        // 6) Transicion fiscal manual.
        bc.Status = BookingCancellationStatus.AwaitingOperatorRefund;
        bc.CreditNoteInvoiceId = creditNote.Id;
        bc.ArcaConfirmedManuallyAt = DateTime.UtcNow;
        bc.ArcaConfirmedManuallyByUserId = userId;
        bc.Reserva.Status = EstadoReserva.PendingOperatorRefund;

        // 7) Consumir approval.
        await _approvalService.MarkConsumedAsync(approval.Id, ct);

        // 8) Audit dedicado para discriminar manual vs automatico.
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationArcaConfirmedManually,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                creditNoteInvoiceId = creditNote.Id,
                creditNoteInvoicePublicId = creditNote.PublicId,
                approvalRequestId = approval.Id,
                approvalRequestPublicId = approval.PublicId,
                request.Reason,
                manuallyConfirmedByUserId = userId,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        // OPS4 (plan v3 §11): counter para alerting. Si supera N/semana en
        // produccion, indica que el callback automatico esta fallando sistematicamente.
        _logger.LogInformation(
            "metric:cancellation_force_arca_executed {BcPublicId} {AdminUserId}",
            bc.PublicId, userId);

        return (await MapToDtoAsync(bc.Id, ct))!;
    }

    // =========================================================================
    // Hooks internos (stubs FC1.2.1 — implementacion completa en FC1.2.2/3)
    // =========================================================================

    public async Task OnAllocationRecordedAsync(int bookingCancellationId, decimal netAmount, CancellationToken ct)
    {
        // FC1.2.2 (2026-05-18): el caller (OperatorRefundService.AllocateAsync)
        // YA hizo Add() del allocation pero NO commiteo todavia (HC1: services
        // internos no SaveChanges). Nosotros marcamos el estado del BC en
        // memoria y dejamos que el caller commitee TODO junto.
        //
        // IMPORTANTE: ReceivedRefundAmount tambien lo aumenta el OperatorRefundService
        // antes de llamar a este callback (es responsabilidad del aggregate del BC,
        // no nuestra) — nosotros solo nos ocupamos del Status.
        var bc = await _db.BookingCancellations
            .FirstOrDefaultAsync(b => b.Id == bookingCancellationId, ct);

        if (bc is null)
        {
            // No tiramos: el caller esta dentro de su tx y necesitamos que se le
            // propague la falla del Add() — un log diagnostico es suficiente.
            _logger.LogWarning(
                "OnAllocationRecordedAsync: BC {BcId} no existe. No-op.",
                bookingCancellationId);
            return;
        }

        // Reglas del estado:
        //  - AwaitingOperatorRefund (post-CAE) -> ClientCreditApplied (primera allocation).
        //  - ClientCreditApplied (ya habia allocations) -> sigue igual.
        //  - Otros estados (Drafted, Aborted, Closed, ArcaRejected) -> el caller
        //    no deberia llegar aca, pero loggeamos y no transicionamos.
        if (bc.Status == BookingCancellationStatus.AwaitingOperatorRefund)
        {
            bc.Status = BookingCancellationStatus.ClientCreditApplied;
            _logger.LogInformation(
                "BC {BcPublicId} transitioned to ClientCreditApplied via OnAllocationRecordedAsync. NetAmount={NetAmount}",
                bc.PublicId, netAmount);
        }
        else if (bc.Status != BookingCancellationStatus.ClientCreditApplied)
        {
            _logger.LogWarning(
                "OnAllocationRecordedAsync: BC {BcPublicId} esta en {Status}, no se transiciona. NetAmount={NetAmount}",
                bc.PublicId, bc.Status, netAmount);
        }
    }

    public async Task OnAllocationVoidedAsync(int bookingCancellationId, decimal netAmount, CancellationToken ct)
    {
        // FC1.2.2 (2026-05-18): el caller (OperatorRefundService.VoidAllocation)
        // ya marco IsVoided=true + decremento refund.AllocatedAmount + ajusto
        // bc.ReceivedRefundAmount, pero todavia no commiteo (HC1). Aca solo
        // ajustamos el Status del BC si quedo sin allocations activas.
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .FirstOrDefaultAsync(b => b.Id == bookingCancellationId, ct);

        if (bc is null)
        {
            _logger.LogWarning(
                "OnAllocationVoidedAsync: BC {BcId} no existe. No-op.",
                bookingCancellationId);
            return;
        }

        // Cuento allocations activas restantes (excluyendo la que se acaba de void).
        //
        // ATENCION trainee/junior — bug fix 2026-05-18:
        // El caller (OperatorRefundService.VoidAllocationAsync / ReassociateAsync)
        // hace `allocation.IsVoided = true` EN MEMORIA y nos invoca SIN haber
        // hecho SaveChangesAsync todavia. Eso es el patron HC1 del plan v3: un
        // unico SaveChanges al final de la transaccion del service.
        //
        // El problema: EF8 + Postgres NO ve los cambios in-memory cuando hace
        // CountAsync, porque CountAsync se traduce a un SELECT COUNT(*) que va
        // al motor SQL. El motor lee la fila tal como esta persistida (IsVoided
        // sigue false en BD), asi que la cuenta da >= 1 y NUNCA entramos al
        // if (remainingActiveAllocations == 0) que revierte el BC. Resultado:
        // el BC queda colgado en ClientCreditApplied aunque la ultima allocation
        // ya fue voideada en memoria.
        //
        // Fix: filtramos manualmente los Ids que estan marcados como IsVoided=true
        // en el ChangeTracker (estado Modified). Es el equivalente "ver lo no
        // persistido todavia" que CountAsync no hace.
        var localVoidedIds = _db.ChangeTracker
            .Entries<OperatorRefundAllocation>()
            .Where(e => e.State == EntityState.Modified && e.Entity.IsVoided)
            .Select(e => e.Entity.Id)
            .ToHashSet();

        var remainingActiveAllocations = await _db.OperatorRefundAllocations
            .Where(a => a.BookingCancellationId == bookingCancellationId
                     && !a.IsVoided
                     && !localVoidedIds.Contains(a.Id))
            .CountAsync(ct);

        if (remainingActiveAllocations == 0 &&
            bc.Status == BookingCancellationStatus.ClientCreditApplied)
        {
            // Volvemos al estado pre-allocation. La Reserva sigue en
            // PendingOperatorRefund (no cambia: el flujo fiscal sigue activo).
            bc.Status = BookingCancellationStatus.AwaitingOperatorRefund;
            _logger.LogInformation(
                "BC {BcPublicId} revertido a AwaitingOperatorRefund por void de la ultima allocation. NetAmountLiberado={NetAmount}",
                bc.PublicId, netAmount);
        }
        else
        {
            _logger.LogDebug(
                "OnAllocationVoidedAsync: BC {BcPublicId} tiene {Count} allocations activas, Status sigue en {Status}.",
                bc.PublicId, remainingActiveAllocations, bc.Status);
        }
    }

    public Task OnAllCreditConsumedAsync(int bookingCancellationId, CancellationToken ct)
    {
        // FC1.2.3 placeholder. Cierra el BC + Reserva cuando todos los entries
        // tienen RemainingBalance=0.
        _logger.LogDebug(
            "OnAllCreditConsumedAsync called but not yet implemented (FC1.2.3). BcId={BcId}",
            bookingCancellationId);
        return Task.CompletedTask;
    }

    public async Task<BookingCancellationDto?> GetByPublicIdAsync(Guid publicId, CancellationToken ct)
    {
        var bc = await _db.BookingCancellations
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct);
        if (bc is null) return null;
        return await MapToDtoAsync(bc.Id, ct);
    }

    // =========================================================================
    // Bridge callbacks (IInvoiceAnnulmentBcBridge)
    // =========================================================================

    public async Task OnArcaSucceededAsync(int originatingInvoiceId, int creditNoteInvoiceId, CancellationToken ct)
    {
        // MR-04 plan v3: buscar SOLO BCs en AwaitingFiscalConfirmation. Si el
        // BC ya transiciono (Force manual antes que el callback) no hacemos nada.
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .FirstOrDefaultAsync(b =>
                b.OriginatingInvoiceId == originatingInvoiceId &&
                b.Status == BookingCancellationStatus.AwaitingFiscalConfirmation, ct);

        if (bc is null)
        {
            _logger.LogWarning(
                "OnArcaSucceededAsync: no se encontro BC AwaitingFiscalConfirmation para Invoice {InvoiceId}. " +
                "Probablemente ya transiciono via ForceArcaConfirmation o no existe. No-op.",
                originatingInvoiceId);
            return;
        }

        bc.Status = BookingCancellationStatus.AwaitingOperatorRefund;
        bc.CreditNoteInvoiceId = creditNoteInvoiceId;
        bc.Reserva.Status = EstadoReserva.PendingOperatorRefund;

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationArcaSucceeded,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                originatingInvoiceId,
                creditNoteInvoiceId,
            }),
            userId: bc.ConfirmedByUserId ?? bc.DraftedByUserId,
            userName: bc.ConfirmedByUserName ?? bc.DraftedByUserName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "BC {BcPublicId} transitioned to AwaitingOperatorRefund via OnArcaSucceededAsync (auto).",
            bc.PublicId);
    }

    public async Task OnArcaFailedAsync(int originatingInvoiceId, string? afipErrorMessage, CancellationToken ct)
    {
        var bc = await _db.BookingCancellations
            .FirstOrDefaultAsync(b =>
                b.OriginatingInvoiceId == originatingInvoiceId &&
                b.Status == BookingCancellationStatus.AwaitingFiscalConfirmation, ct);

        if (bc is null)
        {
            _logger.LogWarning(
                "OnArcaFailedAsync: no se encontro BC AwaitingFiscalConfirmation para Invoice {InvoiceId}. No-op.",
                originatingInvoiceId);
            return;
        }

        bc.Status = BookingCancellationStatus.ArcaRejected;
        // Truncamos para no romper el MaxLength=1000. AFIP a veces devuelve
        // mensajes con XML completo; preferimos cortar a perder el commit.
        var errorMessage = afipErrorMessage ?? "AFIP rechazo la NC sin mensaje.";
        bc.ArcaErrorMessage = errorMessage.Length > 1000
            ? errorMessage[..1000]
            : errorMessage;

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationArcaRejected,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                originatingInvoiceId,
                afipErrorMessage = bc.ArcaErrorMessage,
            }),
            userId: bc.ConfirmedByUserId ?? bc.DraftedByUserId,
            userName: bc.ConfirmedByUserName ?? bc.DraftedByUserName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogError(
            "BC {BcPublicId} marked as ArcaRejected. AFIP error: {Error}",
            bc.PublicId, bc.ArcaErrorMessage);
    }

    // =========================================================================
    // Helpers privados
    // =========================================================================

    /// <summary>
    /// Centraliza la validacion del feature flag. Si el modulo no esta habilitado,
    /// rechazamos con <c>InvalidOperationException</c> que el controller traduce
    /// a HTTP 403 / 422. No revelamos detalles del estado interno al cliente.
    /// </summary>
    private async Task EnsureFeatureFlagOnAsync(CancellationToken ct)
    {
        var settings = await _settings.GetEntityAsync(ct);
        if (!settings.EnableNewCancellationFlow)
            throw new InvalidOperationException(
                "El modulo de cancelacion/refund no esta habilitado en este ambiente " +
                "(EnableNewCancellationFlow=false).");
    }

    /// <summary>
    /// Mapeo entidad → DTO. Lo hacemos manual (sin AutoMapper) porque queremos
    /// controlar exactamente que PublicIds exponemos y como se aplana el
    /// owned <c>FiscalSnapshot</c>.
    /// </summary>
    private async Task<BookingCancellationDto?> MapToDtoAsync(int bcId, CancellationToken ct)
    {
        var bc = await _db.BookingCancellations
            .AsNoTracking()
            .Include(b => b.Reserva)
            .Include(b => b.Customer)
            .Include(b => b.Supplier)
            .Include(b => b.OriginatingInvoice)
            .Include(b => b.CreditNoteInvoice)
            .FirstOrDefaultAsync(b => b.Id == bcId, ct);
        if (bc is null) return null;

        FiscalSnapshotSummaryDto? snapshotDto = null;
        if (bc.FiscalSnapshot != null && bc.Status != BookingCancellationStatus.Drafted)
        {
            snapshotDto = new FiscalSnapshotSummaryDto
            {
                CurrencyAtEvent = bc.FiscalSnapshot.CurrencyAtEvent,
                ExchangeRateAtOriginalInvoice = bc.FiscalSnapshot.ExchangeRateAtOriginalInvoice,
                Source = bc.FiscalSnapshot.Source.ToString(),
                FetchedAt = bc.FiscalSnapshot.FetchedAt,
                CustomerTaxConditionAtEvent = bc.FiscalSnapshot.CustomerTaxConditionAtEvent,
                SupplierTaxConditionAtEvent = bc.FiscalSnapshot.SupplierTaxConditionAtEvent,
                AgencyTaxConditionAtEvent = bc.FiscalSnapshot.AgencyTaxConditionAtEvent,
                ManualJustification = bc.FiscalSnapshot.ManualJustification,
            };
        }

        return new BookingCancellationDto
        {
            PublicId = bc.PublicId,
            Status = bc.Status.ToString(),
            ReservaPublicId = bc.Reserva.PublicId,
            CustomerPublicId = bc.Customer.PublicId,
            SupplierPublicId = bc.Supplier.PublicId,
            OriginatingInvoicePublicId = bc.OriginatingInvoice.PublicId,
            CreditNoteInvoicePublicId = bc.CreditNoteInvoice?.PublicId,
            Reason = bc.Reason,
            DraftedAt = bc.DraftedAt,
            ConfirmedWithClientAt = bc.ConfirmedWithClientAt,
            OperatorRefundDueBy = bc.OperatorRefundDueBy,
            ClosedAt = bc.ClosedAt,
            DraftedByUserId = bc.DraftedByUserId,
            DraftedByUserName = bc.DraftedByUserName,
            ConfirmedByUserId = bc.ConfirmedByUserId,
            ConfirmedByUserName = bc.ConfirmedByUserName,
            AmountPaidAtCancellation = bc.AmountPaidAtCancellation,
            EstimatedRefundAmount = bc.EstimatedRefundAmount,
            ReceivedRefundAmount = bc.ReceivedRefundAmount,
            FiscalSnapshot = snapshotDto,
            ArcaConfirmedManuallyAt = bc.ArcaConfirmedManuallyAt,
            ArcaConfirmedManuallyByUserId = bc.ArcaConfirmedManuallyByUserId,
            ArcaErrorMessage = bc.ArcaErrorMessage,
        };
    }
}
