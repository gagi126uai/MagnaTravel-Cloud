using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
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
public class BookingCancellationService
    : IBookingCancellationService,
      IInvoiceAnnulmentBcBridge,
      IPartialCreditNoteApprovalBridge
{
    private readonly AppDbContext _db;
    private readonly IInvoiceService _invoiceService;
    private readonly IApprovalRequestService _approvalService;
    private readonly IAuditService _auditService;
    private readonly ILogger<BookingCancellationService> _logger;
    private readonly IOperationalFinanceSettingsService _settings;
    // FC1.3.3 (ADR-009 §2.6): clasificador fiscal puro. Lo inyectamos como
    // interface para poder mockearlo en tests unit del service sin levantar
    // toda la cadena (Invoice + Items + Supplier reales).
    private readonly IFiscalLiquidationCalculator _calculator;
    // FC1.3.3 (ADR-009 §2.3.4.bis N-002): abstraccion chica que cuenta admins
    // activos. Existe como interface dedicada para evitar mockear UserManager
    // entero en tests (su ctor pide 8+ dependencias).
    private readonly IAdminUserCountService _adminUserCount;

    public BookingCancellationService(
        AppDbContext db,
        IInvoiceService invoiceService,
        IApprovalRequestService approvalService,
        IAuditService auditService,
        ILogger<BookingCancellationService> logger,
        IOperationalFinanceSettingsService settings,
        IFiscalLiquidationCalculator calculator,
        IAdminUserCountService adminUserCount)
    {
        _db = db;
        _invoiceService = invoiceService;
        _approvalService = approvalService;
        _auditService = auditService;
        _logger = logger;
        _settings = settings;
        _calculator = calculator;
        _adminUserCount = adminUserCount;
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

        // FC1.2.7b counter: una metrica operativa por draft creado. La diferencia
        // con el audit log es de roles: el audit es traza FISCAL (quien / cuando /
        // que cambio); el counter es SENIAL para metricas/alerting (ej. cuantos
        // drafts/dia, cuantos por usuario, picos anomalos). El prefijo "metric:"
        // permite que un parser de logs (Grafana Loki / Promtail) extraiga los
        // valores como series temporales sin tener que tocar el audit log fiscal.
        _logger.LogInformation(
            "metric:cancellation_drafted | BcPublicId={BcPublicId} ReservaPublicId={ReservaPublicId} UserId={UserId}",
            bc.PublicId, reserva.PublicId, userId);

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
        // FC1.3 Fase 2 (B-001 fix, 2026-05-26): incluimos Invoice.Tributes
        // (ThenInclude) porque el calculator chequea .Any() sobre esa coleccion
        // para disparar G-F2-C (tributos provinciales => revision manual). El
        // proyecto NO tiene lazy proxies activos (ver Program.cs §AddDbContext),
        // entonces sin Include la coleccion queda con el default vacio del
        // constructor de Invoice y el flag NUNCA dispara aunque la BD tenga
        // tributos. Bug fantasma: build verde + tests pasaban porque los unit
        // tests inyectan Invoices ya construidas con .Tributes seteado a mano.
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.OriginatingInvoice)
                .ThenInclude(i => i.Tributes)
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
        //
        // N-001 (ADR-009 §2.3.5, round 3): el snapshot DEBE quedar populado ANTES
        // de cualquier transicion a Status >= 8 (ManualReviewPending, etc.). El
        // CHECK heredado `chk_BookingCancellations_fiscalsnapshot_consistent` (FC1.2)
        // exige que cualquier Status != Drafted/Aborted tenga Source != 0,
        // ExchangeRate > 0 y Currency != NULL. Esto se cubre seteando el snapshot
        // ACA y dejando la transicion de Status para mas abajo (en step 8 FC1.2 o
        // en SubmitForReviewAsync FC1.3).
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

        // ===================================================================
        // FC1.3.3 (ADR-009 §2.3.5 + §2.9 + §2.11, 2026-05-21): rama NC parcial.
        //
        // Si el flag EnablePartialCreditNotes esta OFF, todo este bloque se
        // saltea y caemos al path FC1.2 vigente (step 8). Esto preserva la
        // compatibilidad backward sin tocar el flow existente.
        //
        // Si esta ON:
        //  - Validamos que la reserva sea 100% Hotel (INV-FC1.3-007), salvo
        //    override admin con ApprovalRequest tipo InvariantOverride=7
        //    (justificacion >= 50 chars, distinta del comentario futuro del BC
        //    por RH-016).
        //  - Cargamos el OriginatingInvoice completo (items + supplier) e
        //    invocamos el calculator.
        //  - Si el calculator devuelve TotalPlusNewInvoice (casos 4/7): GR-001
        //    rechaza con InvalidOperationException ANTES de cualquier persistencia
        //    FC1.3. La fila del BC se queda en Drafted (rollback EF porque nunca
        //    llamamos a SaveChanges).
        //  - Si el calculator devuelve reason None: persistimos summary y caemos
        //    al path FC1.2 (step 8) — la NC se emite como total real.
        //  - Si el calculator devuelve reason != None: llamamos a
        //    SubmitForReviewAsync que crea el ApprovalRequest, transiciona el
        //    BC a ManualReviewPending y retorna directo. NO caemos al step 8.
        // ===================================================================
        var settings = await _settings.GetEntityAsync(ct);
        if (settings.EnablePartialCreditNotes)
        {
            // (a) INV-FC1.3-007: solo Hotel. Patron real lineas 256-285 de override.
            // Cargamos Servicios (no estaba en el Include inicial) para validar.
            await _db.Entry(bc.Reserva).Collection(r => r.Servicios).LoadAsync(ct);
            var nonHotelServices = bc.Reserva.Servicios
                .Where(s => !string.Equals(s.ProductType, ServiceTypes.Hotel, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (nonHotelServices.Count > 0)
            {
                // El override usa el MISMO approvalRequest del IsAdminOverride
                // si su Reason >= 50 chars (mayor exigencia que los 20 del override
                // de FC1.2). Si no, rechaza.
                var validOverrideForHotelInvariant =
                    approvalRequest != null
                    && approvalRequest.RequestType == ApprovalRequestType.InvariantOverride
                    && approvalRequest.Status == ApprovalStatus.Approved
                    && approvalRequest.EntityType == "BookingCancellation"
                    && approvalRequest.EntityId == bc.Id
                    && approvalRequest.RequestedByUserId == userId
                    && approvalRequest.ExpiresAt > DateTime.UtcNow
                    && !string.IsNullOrWhiteSpace(approvalRequest.Reason)
                    && approvalRequest.Reason.Trim().Length >= 50;

                if (!validOverrideForHotelInvariant)
                {
                    throw new BusinessInvariantViolationException(
                        $"FC1.3 Fase 1 solo soporta reservas 100% Hotel. " +
                        $"Servicios no-Hotel detectados: {string.Join(", ", nonHotelServices.Select(s => s.ProductType ?? "(null)"))}. " +
                        "Use flujo legacy (apagar EnablePartialCreditNotes para esta operacion) o " +
                        "solicitar override via InvariantOverride approval con justificacion >= 50 chars.",
                        invariantCode: "INV-FC1.3-007");
                }
                // Si llega aca el override cubre el caso, seguimos adelante.
            }

            // (b) Cargar items + supplier necesarios para el calculator.
            var invoiceItems = await _db.Set<InvoiceItem>()
                .Where(i => i.InvoiceId == bc.OriginatingInvoiceId)
                .ToListAsync(ct);
            var supplier = await _db.Suppliers
                .FirstOrDefaultAsync(s => s.Id == bc.SupplierId, ct)
                ?? throw new InvalidOperationException(
                    $"No se encontro el Supplier {bc.SupplierId} del BC {bc.PublicId}.");

            // (c) Armar input. CancellationAmount = ImporteTotal por defecto:
            // Fase 1 solo soporta cancelacion total (cancelacion parcial sub-monto
            // queda para Fase 2). OperatorPenaltyAmount = 0 por ahora — el endpoint
            // todavia no recibe el monto. En Fase 1.3.5 se agrega al request.
            var calculatorInput = new FiscalLiquidationInput(
                OriginatingInvoice: bc.OriginatingInvoice,
                Items: invoiceItems,
                Supplier: supplier,
                InvoicingModeAtEvent: bc.FiscalSnapshot.InvoicingModeAtEvent,
                OriginalInvoiceAmount: bc.OriginatingInvoice.ImporteTotal,
                CancellationAmount: bc.OriginatingInvoice.ImporteTotal,
                OperatorPenaltyAmount: 0m,
                RetentionNatureChangedByUser: false,
                OriginalInvoiceUnclearByUser: false,
                Currency: bc.FiscalSnapshot.CurrencyAtEvent ?? "ARS");

            // (d) Correr clasificador (puro, sin IO).
            var liquidation = _calculator.Calculate(calculatorInput, settings);

            // (e) GR-001: rechazo ANTES de persistir nada FC1.3. La fila del BC
            // queda intacta en Drafted (sin SaveChanges no hay efecto). Tests
            // verifican que `bc.CreditNoteKind` sigue null post-throw.
            if (liquidation.Kind == CreditNoteKind.TotalPlusNewInvoice)
            {
                throw new InvalidOperationException(
                    "Caso fiscal requiere FC1.3 Fase 2 - use flujo legacy. " +
                    $"Calculator devolvio CreditNoteKind=TotalPlusNewInvoice " +
                    $"(case {liquidation.Case}, motivos {liquidation.ReviewRequiredReason}). " +
                    "Apague EnablePartialCreditNotes para esta operacion o espere a Fase 2.");
            }

            // (f) Persistir summary minimo (GR-004): NO guardamos los montos del
            // calculator en BD. Solo el resultado + timestamps + quien lo corrio.
            // El detalle entero se serializa al Metadata del approval en
            // SubmitForReviewAsync (si aplica).
            bc.CreditNoteKind = liquidation.Kind;
            bc.ReviewRequiredReason = liquidation.ReviewRequiredReason;
            bc.LiquidationComputedAt = DateTime.UtcNow;
            bc.LiquidationComputedByUserId = userId;
            bc.LiquidationComputedByUserName = userName;

            // (g) Si hay motivos para review manual -> abrir approval + transicionar
            //     a ManualReviewPending + retornar. No caemos al step 8 de FC1.2.
            if (liquidation.ReviewRequiredReason != ReviewRequiredReason.None)
            {
                return await SubmitForReviewAsync(bc, liquidation, userId, userName, ct);
            }

            // (h) Reason == None y Kind == PartialOnOriginal: la liquidacion es
            //     auto-aprobable. Caemos al path FC1.2 (step 8) que emite NC total
            //     real (Fase 1). El summary FC1.3 queda persistido para que Fase 2
            //     pueda detectar BCs auto-clasificados y migrarlos cuando AfipService
            //     emita NC parcial real.
            _logger.LogInformation(
                "FC1.3 auto-aprobable: BC {BcPublicId} clasificado Kind={Kind} sin motivos manual review. " +
                "Continua flujo FC1.2 (NC total real Fase 1).",
                bc.PublicId, liquidation.Kind);
        }

        // 8) Transicionar BC + Reserva (HC2 plan v3: bypass UpdateStatusAsync —
        //    el state machine general no contempla la transicion lateral a
        //    PendingOperatorRefund, lo hacemos directo y dejamos el comentario
        //    para que en una review el lector entienda por que).
        bc.Status = BookingCancellationStatus.AwaitingFiscalConfirmation;
        bc.ConfirmedWithClientAt = DateTime.UtcNow;
        bc.ConfirmedByUserId = userId;
        bc.ConfirmedByUserName = userName;
        bc.OperatorRefundDueBy = DateTime.UtcNow.AddDays(settings.OperatorRefundTimeoutDays);

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

        // FC1.2.7b counter: marcamos confirm + flag with_override para que el
        // dashboard pueda distinguir "cuantas cancelaciones fueron normales vs
        // cuantas pasaron por escape hatch de admin". Si with_override sube,
        // hay un problema sistematico (probablemente reglas de negocio mal
        // calibradas o callbacks AFIP fallando).
        _logger.LogInformation(
            "metric:cancellation_confirmed | BcPublicId={BcPublicId} WithOverride={WithOverride} UserId={UserId}",
            bc.PublicId, approvalRequest != null, userId);

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

        // FC1.2.7b counter: cuantos drafts se abortan en vez de confirmarse.
        // Una tasa alta indica que vendedores estan creando drafts "por las dudas"
        // — vale la pena reentrenar el flujo o ajustar la UI para reducir aborts.
        _logger.LogInformation(
            "metric:cancellation_aborted | BcPublicId={BcPublicId} UserId={UserId}",
            bc.PublicId, userId);

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

        // OPS4 (plan v3 §11) + FC1.2.7b: counter para alerting. Si supera
        // N/semana en produccion, indica que el callback automatico esta fallando
        // sistematicamente. Usamos el mismo formato "metric:nombre | k=v k=v" que
        // los demas counters del modulo para que el parser de logs los junte.
        _logger.LogInformation(
            "metric:cancellation_force_arca_executed | BcPublicId={BcPublicId} AdminUserId={AdminUserId}",
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

    public async Task OnAllCreditConsumedAsync(int bookingCancellationId, CancellationToken ct)
    {
        // FC1.2.3 v3 §6.4 (2026-05-18): cierre del BC cuando TODOS los entries
        // del BC quedan con RemainingBalance=0. Lo invoca ClientCreditService
        // despues de un withdraw que dejo el entry consumido.
        //
        // Patron HC1: este callback corre DENTRO de la tx envolvente del
        // ClientCreditService.WithdrawAsync. Modificamos el bc en memoria y
        // dejamos que el caller commitee TODO junto (un solo SaveChanges).
        //
        // Patron MR-02 (idempotencia bajo concurrencia): si dos withdraws
        // paralelos terminan el ultimo entry casi al mismo tiempo, los dos
        // callers pueden invocar este callback. La transicion debe ser
        // idempotente — solo cerrar si esta en ClientCreditApplied; si ya
        // esta Closed, no-op silencioso.
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .FirstOrDefaultAsync(b => b.Id == bookingCancellationId, ct);

        if (bc is null)
        {
            _logger.LogWarning(
                "OnAllCreditConsumedAsync: BC {BcId} no existe. No-op.",
                bookingCancellationId);
            return;
        }

        // Reverificacion bajo concurrencia (MR-02 plan v3):
        //
        // El caller dijo "ya consumi el ultimo entry" basandose en su estado
        // in-memory. Pero si OTRO withdraw paralelo abrio otra tx, podria
        // haber agregado un nuevo entry o restaurado el balance via Reassociate.
        // Antes de cerrar el BC, contamos directamente en BD con SQL crudo
        // cuantos entries quedan con saldo > 0 EXCLUYENDO los cambios in-memory
        // que el caller ya hizo (todavia no commiteados).
        //
        // Por que SQL crudo y no LINQ: el CountAsync de EF ve el estado
        // persistido de BD, no el ChangeTracker. Es exactamente lo que queremos
        // aca — verificar que en BD no quede nada con saldo > 0 de OTRA tx.
        // Sumamos al final la cuenta in-memory de "lo que NUESTRO tx esta a
        // punto de dejar a saldo > 0" (caso edge: un nuevo entry agregado en
        // este scope).
        var remainingInDb = await _db.Database.SqlQueryRaw<int>(
            // EF Core 8: SqlQueryRaw<int> usa { } para parametros (no @p0).
            // El TableName/Column names entre comillas dobles para Postgres.
            "SELECT COUNT(*)::int AS \"Value\" FROM \"ClientCreditEntries\" " +
            "WHERE \"BookingCancellationId\" = {0} AND \"RemainingBalance\" > 0",
            bookingCancellationId).FirstOrDefaultAsync(ct);

        // Tambien contamos lo in-memory: entries Added/Modified en este scope
        // con RemainingBalance > 0 que aun no se commitearon. Si hay alguno,
        // no cerramos.
        //
        // Trainee/junior: EF Core trackea entidades modificadas via
        // ChangeTracker. Le pedimos las entries que esta gestionando y filtramos
        // por las que apuntan a este BC y todavia tienen saldo. Esto cubre el
        // caso "el caller in-memory tiene un entry con saldo > 0 que la query
        // SQL no ve porque no se persistio".
        var remainingInMemory = _db.ChangeTracker
            .Entries<ClientCreditEntry>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified)
            .Select(e => e.Entity)
            .Count(entry => entry.BookingCancellationId == bookingCancellationId
                         && entry.RemainingBalance > 0m);

        if (remainingInDb + remainingInMemory > 0)
        {
            _logger.LogDebug(
                "OnAllCreditConsumedAsync: BC {BcPublicId} todavia tiene saldos pendientes " +
                "(db={RemainingInDb}, mem={RemainingInMemory}). No se cierra.",
                bc.PublicId, remainingInDb, remainingInMemory);
            return;
        }

        // Transicion idempotente:
        //  - Si esta en ClientCreditApplied -> Closed + Reserva Cancelled.
        //  - Si ya esta Closed -> no-op (otro withdraw paralelo cerro antes).
        //  - Si esta en otro estado (AwaitingOperatorRefund / etc.) -> log
        //    warning. No tiene sentido cerrar desde un estado que no llego a
        //    aplicar credito.
        if (bc.Status == BookingCancellationStatus.Closed)
        {
            _logger.LogDebug(
                "OnAllCreditConsumedAsync: BC {BcPublicId} ya esta Closed. No-op.",
                bc.PublicId);
            return;
        }

        if (bc.Status != BookingCancellationStatus.ClientCreditApplied)
        {
            _logger.LogWarning(
                "OnAllCreditConsumedAsync: BC {BcPublicId} esta en {Status}, no en ClientCreditApplied. " +
                "No se cierra (algo raro: el flujo deberia haber pasado por allocation antes de retiros).",
                bc.PublicId, bc.Status);
            return;
        }

        bc.Status = BookingCancellationStatus.Closed;
        bc.ClosedAt = DateTime.UtcNow;
        bc.Reserva.Status = EstadoReserva.Cancelled;

        // Audit dedicado del cierre del BC para que el contador pueda buscar
        // "cuando se cerro la cancelacion #X" sin tener que mirar el ultimo
        // withdraw individual.
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationClosed,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                reservaPublicId = bc.Reserva.PublicId,
                closedAt = bc.ClosedAt,
                receivedRefundAmount = bc.ReceivedRefundAmount,
            }),
            // Usamos el usuario que confirmo el BC originalmente como actor.
            // El user que dispara el ultimo withdraw figura en el audit
            // ClientCreditWithdrawn — aca queremos trazar "quien era duenio
            // del BC cuando se cerro" para reportes operativos.
            userId: bc.ConfirmedByUserId ?? bc.DraftedByUserId,
            userName: bc.ConfirmedByUserName ?? bc.DraftedByUserName,
            ct: ct);

        _logger.LogInformation(
            "BC {BcPublicId} closed via OnAllCreditConsumedAsync (Reserva -> Cancelled).",
            bc.PublicId);

        // FC1.2.7b counter: cierre del BC = ciclo completo (Drafted → Closed).
        // El delta entre cancellation_drafted y cancellation_closed indica cuantas
        // cancelaciones quedan "abiertas" en el funnel (drafted pero no cerradas).
        _logger.LogInformation(
            "metric:cancellation_closed | BcPublicId={BcPublicId} ReceivedRefundAmount={ReceivedRefundAmount}",
            bc.PublicId, bc.ReceivedRefundAmount);
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
    // FC1.3.3 (ADR-009 §2.7 G3, 2026-05-21): edicion admin de la liquidacion
    // =========================================================================

    /// <inheritdoc />
    public async Task<BookingCancellationDto> EditLiquidationAsync(
        Guid publicId,
        EditLiquidationRequest req,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        if (req is null) throw new ArgumentNullException(nameof(req));

        await EnsureFeatureFlagOnAsync(ct);

        // 1) Cargar BC + approval + reserva + factura origen. Necesitamos todo
        //    para correr el calculator de nuevo y validar el flow.
        // FC1.3 Fase 2 (B-001 fix, 2026-05-26): incluimos Invoice.Tributes
        // (ThenInclude) por la misma razon que ConfirmAsync. EditLiquidation
        // re-corre el calculator y G-F2-C necesita la coleccion cargada para
        // disparar bien (sin lazy proxies, Tributes queda vacia por default).
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva).ThenInclude(r => r.Servicios)
            .Include(b => b.OriginatingInvoice)
                .ThenInclude(i => i.Tributes)
            .Include(b => b.PartialCreditNoteApprovalRequest)
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // 2) Estado: solo se edita desde ManualReviewPending. Si esta en otro
        //    estado, rechazamos: el flujo G3 es self-loop, no es para destrabar
        //    BCs aprobados/rechazados.
        if (bc.Status != BookingCancellationStatus.ManualReviewPending)
        {
            throw new BusinessInvariantViolationException(
                $"EditLiquidation solo se permite desde ManualReviewPending (actual: {bc.Status}).",
                invariantCode: "INV-093");
        }

        if (bc.PartialCreditNoteApprovalRequest is null)
        {
            // Defensive: el CHECK chk_BookingCancellations_manualreview_approvalref
            // ya garantiza esto. Si llegamos aca, hubo corrupcion o bypass.
            throw new BusinessInvariantViolationException(
                $"BC {bc.PublicId} en ManualReviewPending sin PartialCreditNoteApprovalRequestId. " +
                "Estado inconsistente.",
                invariantCode: "INV-FC1.3-002");
        }

        // 3) 4-eyes (INV-FC1.3-004) con bypass GR-005. Si el admin que edita es el
        //    mismo que solicito (DraftedByUserId), aplicar la regla.
        var settings = await _settings.GetEntityAsync(ct);
        var isSelfEdit = string.Equals(bc.DraftedByUserId, userId, StringComparison.Ordinal);
        var bypassApplied = false;

        if (isSelfEdit)
        {
            bypassApplied = await TryApplyGr005BypassAsync(req.Comment, settings, ct);
            if (!bypassApplied)
            {
                throw new BusinessInvariantViolationException(
                    "INV-FC1.3-004 violado: el admin que edita no puede ser el mismo " +
                    "que solicito la cancelacion (4-eyes). Bypass GR-005 no aplica " +
                    "(setting Allow4EyesBypassWhenSingleAdmin=false, o hay mas de 1 admin " +
                    "activo, o comentario < 100 chars).",
                    invariantCode: "INV-FC1.3-004");
            }
        }
        // Si !isSelfEdit, 4-eyes esta cumplido naturalmente. No hace falta bypass.

        // 4) Cargar inputs para recorrer calculator. Items + supplier ya estan en BD.
        var invoiceItems = await _db.Set<InvoiceItem>()
            .Where(i => i.InvoiceId == bc.OriginatingInvoiceId)
            .ToListAsync(ct);
        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == bc.SupplierId, ct)
            ?? throw new InvalidOperationException($"Supplier {bc.SupplierId} no encontrado.");

        // 5) Aplicar overrides del admin sobre el input.
        var penaltyOverride = req.OperatorPenaltyAmountOverride ?? 0m;
        var calculatorInput = new FiscalLiquidationInput(
            OriginatingInvoice: bc.OriginatingInvoice,
            Items: invoiceItems,
            Supplier: supplier,
            InvoicingModeAtEvent: bc.FiscalSnapshot.InvoicingModeAtEvent,
            OriginalInvoiceAmount: bc.OriginatingInvoice.ImporteTotal,
            CancellationAmount: bc.OriginatingInvoice.ImporteTotal,
            OperatorPenaltyAmount: penaltyOverride,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false,
            Currency: bc.FiscalSnapshot.CurrencyAtEvent ?? "ARS");

        var newLiquidation = _calculator.Calculate(calculatorInput, settings);

        // 6) Re-validacion GR-001: la nueva clasificacion puede haber pasado a
        //    TotalPlusNewInvoice (cambio de inputs). Misma politica que Confirm.
        if (newLiquidation.Kind == CreditNoteKind.TotalPlusNewInvoice)
        {
            throw new InvalidOperationException(
                "Re-clasificacion despues del edit dio CreditNoteKind=TotalPlusNewInvoice. " +
                "Fase 1 no soporta este caso. Pedir Reject del admin y abortar el BC.");
        }

        // 7) Capturar snapshot anterior para construir el diff (RH-012).
        var oldKind = bc.CreditNoteKind;
        var oldReason = bc.ReviewRequiredReason;

        // 8) Actualizar summary en el BC.
        bc.CreditNoteKind = req.CreditNoteKindOverride ?? newLiquidation.Kind;
        bc.ReviewRequiredReason = newLiquidation.ReviewRequiredReason;

        // 9) Apend al Metadata.edits[] del approval. RH-006 cubierto: si otro
        //    admin edito entre la lectura y el save, EF tira
        //    DbUpdateConcurrencyException via xmin del ApprovalRequest.
        var approval = bc.PartialCreditNoteApprovalRequest;
        var metadataObj = DeserializeMetadataOrEmpty(approval.Metadata);
        var newEdit = new
        {
            at = DateTime.UtcNow,
            by = userId,
            byName = userName,
            comment = req.Comment,
            selfApprovedDueToSingleAdmin = bypassApplied,
            previousKind = oldKind?.ToString(),
            newKind = bc.CreditNoteKind?.ToString(),
            previousReason = oldReason.ToString(),
            newReason = bc.ReviewRequiredReason.ToString(),
            newFiscalAmountToCredit = newLiquidation.FiscalAmountToCredit,
            newOperatorPenaltyAmount = newLiquidation.OperatorPenaltyAmount,
            newNonRefundableItemsAmount = newLiquidation.NonRefundableItemsAmount,
        };
        metadataObj["edits"] = (metadataObj.TryGetValue("edits", out var existing) && existing is List<object> list)
            ? new List<object>(list) { newEdit }
            : new List<object> { newEdit };
        approval.Metadata = JsonSerializer.Serialize(metadataObj);

        // 10) Audit con diff RH-012. Shape canonico {"Field":{"Old":"...","New":"..."}}.
        var changes = new Dictionary<string, object>
        {
            ["CreditNoteKind"] = new { Old = oldKind?.ToString(), New = bc.CreditNoteKind?.ToString() },
            ["ReviewRequiredReason"] = new { Old = oldReason.ToString(), New = bc.ReviewRequiredReason.ToString() },
            ["FiscalAmountToCredit"] = new { Old = (decimal?)null, New = newLiquidation.FiscalAmountToCredit },
            ["OperatorPenaltyAmount"] = new { Old = (decimal?)null, New = newLiquidation.OperatorPenaltyAmount },
            ["NonRefundableItemsAmount"] = new { Old = (decimal?)null, New = newLiquidation.NonRefundableItemsAmount },
        };

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationLiquidationEdited,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                approvalRequestPublicId = approval.PublicId,
                comment = req.Comment,
                selfApprovedDueToSingleAdmin = bypassApplied,
                Changes = changes,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        // 11) Commit. Si hay race entre dos admins editando el mismo approval,
        //     EF tira DbUpdateConcurrencyException por xmin y el caller decide
        //     reintentar (RH-006). NO catcheamos aca: el caller (controller)
        //     mapea 409 al cliente.
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "FC1.3 EditLiquidation: BC {BcPublicId} editado por {UserId} (selfBypass={Bypass}).",
            bc.PublicId, userId, bypassApplied);

        return (await MapToDtoAsync(bc.Id, ct))!;
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

        // FC1.2.7b counter: callback AFIP exitoso. Si crece muy despacio comparado
        // con cancellation_confirmed, hay backlog en Hangfire o AFIP esta lento.
        _logger.LogInformation(
            "metric:cancellation_arca_succeeded | BcPublicId={BcPublicId} OriginatingInvoiceId={OriginatingInvoiceId} CreditNoteInvoiceId={CreditNoteInvoiceId}",
            bc.PublicId, originatingInvoiceId, creditNoteInvoiceId);
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

        // FC1.2.7b counter: callback AFIP rechazado. Si supera N/dia, hay un
        // problema sistematico con AFIP (CUITs invalidos, fechas mal, etc.).
        // El admin puede recurrir a ForceArcaConfirmation manualmente para
        // destrabar (genera otro counter "cancellation_force_arca_executed").
        _logger.LogInformation(
            "metric:cancellation_arca_failed | BcPublicId={BcPublicId} OriginatingInvoiceId={OriginatingInvoiceId} ErrorTruncated={ErrorPreview}",
            bc.PublicId, originatingInvoiceId,
            // Truncamos a 80 chars para que el log no se ensucie con XML completo.
            bc.ArcaErrorMessage.Length > 80 ? bc.ArcaErrorMessage[..80] : bc.ArcaErrorMessage);
    }

    // =========================================================================
    // FC1.3.3 (ADR-009 §2.8.3, 2026-05-21): IPartialCreditNoteApprovalBridge.
    //
    // Estos dos callbacks los dispara `ApprovalRequestService.ApproveAsync` /
    // `RejectAsync` DESPUES de haber commiteado el cambio de Status en el
    // ApprovalRequest. Por lo tanto:
    //  - Si el bridge tira o crashea, el approval queda en su estado final
    //    (Approved/Rejected) pero el BC queda en ManualReviewPending. Esa
    //    divergencia la sanea el job de reconciliacion bridge (FC1.3.6b) +
    //    endpoint admin de force-callback (ADR §2.12). No usamos tx distribuida
    //    intencionalmente (N-007 round 3).
    //  - Por eso ambos metodos son idempotentes: si el BC ya esta en el estado
    //    destino, log warning + return SIN tirar (no romper el flow de approval).
    // =========================================================================

    /// <summary>
    /// FC1.3.3: callback que dispara <c>ApprovalRequestService.ApproveAsync</c>
    /// cuando aprueba un <c>PartialCreditNoteApproval=11</c>. Transiciona el BC
    /// de <c>ManualReviewPending</c> a <c>ManualReviewApproved</c> y, si el
    /// kind es <c>PartialOnOriginal</c>, avanza inmediatamente a
    /// <c>AwaitingFiscalConfirmation</c> (path FC1.2 — Fase 1 emite NC total).
    /// </summary>
    public async Task OnApprovedAsync(
        int approvalRequestId,
        string resolverUserId,
        string? resolverUserName,
        string? resolverNotes,
        CancellationToken ct)
    {
        // 1) Localizar BC por la FK.
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.PartialCreditNoteApprovalRequest)
            .FirstOrDefaultAsync(b => b.PartialCreditNoteApprovalRequestId == approvalRequestId, ct);

        if (bc is null)
        {
            // No hay BC vinculado (ApprovalRequest huerfana). Log + return: el
            // job de reconciliacion no puede reabrir esto (no hay BC). Si llegamos
            // aca, lo mas probable es que el BC fue abortado/eliminado.
            _logger.LogWarning(
                "OnApprovedAsync FC1.3: no se encontro BC con PartialCreditNoteApprovalRequestId={ApprovalRequestId}. " +
                "Approval queda Approved sin efecto. No-op.",
                approvalRequestId);
            return;
        }

        // 2) Idempotencia: si ya esta en estado destino, log + return.
        if (bc.Status == BookingCancellationStatus.ManualReviewApproved
            || bc.Status == BookingCancellationStatus.AwaitingFiscalConfirmation
            || bc.Status == BookingCancellationStatus.AwaitingOperatorRefund
            || bc.Status == BookingCancellationStatus.ClientCreditApplied
            || bc.Status == BookingCancellationStatus.Closed)
        {
            _logger.LogWarning(
                "OnApprovedAsync FC1.3 no-op: BC {BcPublicId} ya esta en {Status}. " +
                "El bridge probablemente fue invocado dos veces (job reconciliacion + bridge real).",
                bc.PublicId, bc.Status);
            return;
        }

        // 3) Si no esta en ManualReviewPending, algo raro: no transicionamos.
        if (bc.Status != BookingCancellationStatus.ManualReviewPending)
        {
            _logger.LogWarning(
                "OnApprovedAsync FC1.3: BC {BcPublicId} esta en {Status}, no en ManualReviewPending. " +
                "No-op (no es seguro forzar la transicion sin entender por que llego aca).",
                bc.PublicId, bc.Status);
            return;
        }

        // 4) Validar 4-eyes con bypass GR-005 SOBRE EL RESOLVER. El admin que
        //    aprueba puede ser distinto del que edito; lo que importa para
        //    INV-FC1.3-004 es que el approver != vendedor original.
        var settings = await _settings.GetEntityAsync(ct);
        var isSelfApproval = string.Equals(bc.DraftedByUserId, resolverUserId, StringComparison.Ordinal);
        var bypassApplied = false;

        if (isSelfApproval)
        {
            bypassApplied = await TryApplyGr005BypassAsync(resolverNotes, settings, ct);
            if (!bypassApplied)
            {
                // No tiramos: el approval ya esta aprobado en BD. Loguear como
                // ERROR (no warning) y dejar el BC en ManualReviewPending. El
                // admin del sistema debe intervenir manualmente (revertir el
                // approval o forzar el callback con InvariantOverride scoped).
                _logger.LogError(
                    "OnApprovedAsync FC1.3 RECHAZADO: BC {BcPublicId} aprobado por el mismo vendedor " +
                    "({UserId}), bypass GR-005 no aplica. BC se queda en ManualReviewPending. " +
                    "Intervencion manual requerida (revertir approval o force-callback con InvariantOverride).",
                    bc.PublicId, resolverUserId);
                return;
            }
        }

        // 5) Validar longitud minima del resolverNotes. Si el monto supera el
        //    accounting threshold, exigir 100 chars (G5). Si no, 20 basta.
        //    Esto es defensive: ApprovalRequestService probablemente ya valido
        //    longitud minima, pero la regla "100 si accounting" es de FC1.3.
        var commentMinLength = bc.ReviewRequiredReason.HasFlag(ReviewRequiredReason.AmountAboveAccountingThreshold) ? 100 : 20;
        if (string.IsNullOrWhiteSpace(resolverNotes) || resolverNotes.Trim().Length < commentMinLength)
        {
            _logger.LogError(
                "OnApprovedAsync FC1.3 RECHAZADO: BC {BcPublicId} comment del resolver muy corto " +
                "({Actual} chars, requeridos {Required}). BC se queda en ManualReviewPending.",
                bc.PublicId, resolverNotes?.Length ?? 0, commentMinLength);
            return;
        }

        // 6) Transicion fiscal a ManualReviewApproved.
        bc.Status = BookingCancellationStatus.ManualReviewApproved;
        bc.ManualReviewerUserId = resolverUserId;
        bc.ManualReviewerUserName = resolverUserName;
        bc.ManualReviewedAt = DateTime.UtcNow;
        bc.ManualReviewComment = resolverNotes;

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationManualReviewApproved,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                approvalRequestId,
                resolverUserId,
                resolverNotes,
                creditNoteKind = bc.CreditNoteKind?.ToString(),
                reviewRequiredReason = bc.ReviewRequiredReason.ToString(),
                selfApprovedDueToSingleAdmin = bypassApplied,
                accountingReviewRequired = bc.ReviewRequiredReason.HasFlag(ReviewRequiredReason.AmountAboveAccountingThreshold),
            }),
            userId: resolverUserId,
            userName: resolverUserName,
            ct: ct);

        // 7) Fase 1: emision automatica AVANZANDO A AwaitingFiscalConfirmation con
        //    el path FC1.2 (NC total real al ARCA). En Fase 2 esto se reemplaza
        //    por InvoiceService.EnqueuePartialCreditNoteAsync que emitira NC
        //    parcial real.
        if (bc.CreditNoteKind == CreditNoteKind.PartialOnOriginal)
        {
            _logger.LogWarning(
                "FC1.3 Fase 1: BC {BcPublicId} aprobado con CreditNoteKind=PartialOnOriginal pero " +
                "AfipService emite NC TOTAL real (no parcial). Fase 2 implementa parcial real. " +
                "Razon FC1.3: {Reason}. Monto facturado: {Total}.",
                bc.PublicId, bc.ReviewRequiredReason, bc.OriginatingInvoice?.ImporteTotal);

            // Recuperamos settings de nuevo para OperatorRefundTimeoutDays
            // (no es hot path; lo importante es que la transicion sea atomica).
            bc.Status = BookingCancellationStatus.AwaitingFiscalConfirmation;
            bc.ConfirmedWithClientAt ??= DateTime.UtcNow;
            bc.ConfirmedByUserId ??= resolverUserId;
            bc.ConfirmedByUserName ??= resolverUserName;
            bc.OperatorRefundDueBy ??= DateTime.UtcNow.AddDays(settings.OperatorRefundTimeoutDays);
            bc.Reserva.Status = EstadoReserva.PendingOperatorRefund;

            await _db.SaveChangesAsync(ct);

            // Encolar la NC en AFIP. En Fase 1 emite total (mismo path que FC1.2).
            // Pasamos requesterIsAdmin=true porque el approval FC1.3 ya cubrio la
            // autorizacion (no necesitamos otro approval InvoiceAnnulment).
            await _invoiceService.EnqueueAnnulmentAsync(
                id: bc.OriginatingInvoiceId,
                userId: resolverUserId,
                userName: resolverUserName,
                reason: $"FC1.3 manual review approved: {resolverNotes?.Trim()}",
                requesterIsAdmin: true,
                ct: ct,
                approvalRequestId: approvalRequestId);

            // Marcar el approval como Consumed para que no se reuse.
            await _approvalService.MarkConsumedAsync(approvalRequestId, ct);
        }
        else
        {
            // Defensive: si llega un kind raro (Unset, futuro TotalPlusNewInvoice
            // si Fase 2 lo permite), persistir lo que hicimos hasta aca y dejar
            // la decision al admin que llamara al endpoint de Fase 2.
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "FC1.3 OnApprovedAsync: BC {BcPublicId} -> ManualReviewApproved (selfBypass={Bypass}).",
            bc.PublicId, bypassApplied);
    }

    /// <summary>
    /// FC1.3.3: callback que dispara <c>ApprovalRequestService.RejectAsync</c>
    /// cuando rechaza un <c>PartialCreditNoteApproval=11</c>. Transiciona el BC
    /// a <c>ManualReviewRejected</c> e inmediatamente despues auto-resetea a
    /// <c>Drafted</c> dentro de la misma tx, limpiando los campos FC1.3.
    /// </summary>
    public async Task OnRejectedAsync(
        int approvalRequestId,
        string resolverUserId,
        string? resolverUserName,
        string? resolverNotes,
        CancellationToken ct)
    {
        // 1) Localizar BC por la FK.
        var bc = await _db.BookingCancellations
            .FirstOrDefaultAsync(b => b.PartialCreditNoteApprovalRequestId == approvalRequestId, ct);

        if (bc is null)
        {
            _logger.LogWarning(
                "OnRejectedAsync FC1.3: no se encontro BC con PartialCreditNoteApprovalRequestId={ApprovalRequestId}. " +
                "No-op.",
                approvalRequestId);
            return;
        }

        // 2) Idempotencia: si ya esta en Drafted/Aborted/Rejected, no hacer nada.
        if (bc.Status == BookingCancellationStatus.Drafted
            || bc.Status == BookingCancellationStatus.Aborted
            || bc.Status == BookingCancellationStatus.ManualReviewRejected)
        {
            _logger.LogWarning(
                "OnRejectedAsync FC1.3 no-op: BC {BcPublicId} ya esta en {Status}.",
                bc.PublicId, bc.Status);
            return;
        }

        if (bc.Status != BookingCancellationStatus.ManualReviewPending)
        {
            _logger.LogWarning(
                "OnRejectedAsync FC1.3: BC {BcPublicId} esta en {Status}, no en ManualReviewPending. No-op.",
                bc.PublicId, bc.Status);
            return;
        }

        // 3) Validar longitud minima del resolverNotes (20 chars).
        if (string.IsNullOrWhiteSpace(resolverNotes) || resolverNotes.Trim().Length < 20)
        {
            _logger.LogError(
                "OnRejectedAsync FC1.3 RECHAZADO: BC {BcPublicId} resolverNotes muy cortos " +
                "({Actual} chars, requeridos 20). BC se queda en ManualReviewPending.",
                bc.PublicId, resolverNotes?.Length ?? 0);
            return;
        }

        // 4) Audit del rechazo ANTES del reset — guarda el snapshot pre-reset
        //    para auditoria (si despues miras el BC en Drafted, no sabrias que
        //    paso por FC1.3 si no fuera por este audit).
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationManualReviewRejected,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                approvalRequestId,
                resolverUserId,
                resolverNotes,
                preResetSnapshot = new
                {
                    creditNoteKind = bc.CreditNoteKind?.ToString(),
                    reviewRequiredReason = bc.ReviewRequiredReason.ToString(),
                    liquidationComputedAt = bc.LiquidationComputedAt,
                    liquidationComputedByUserId = bc.LiquidationComputedByUserId,
                },
            }),
            userId: resolverUserId,
            userName: resolverUserName,
            ct: ct);

        // 5) Auto-reset: limpiar todos los campos FC1.3 + volver a Drafted.
        bc.Status = BookingCancellationStatus.Drafted;
        bc.CreditNoteKind = null;
        bc.ReviewRequiredReason = ReviewRequiredReason.None;
        bc.LiquidationComputedAt = null;
        bc.LiquidationComputedByUserId = null;
        bc.LiquidationComputedByUserName = null;
        bc.PartialCreditNoteApprovalRequestId = null;
        // ManualReviewer* fields NO se limpian: el rechazo en si es un evento
        // que vale la pena trazar inline en el BC (ademas del audit log).
        bc.ManualReviewerUserId = resolverUserId;
        bc.ManualReviewerUserName = resolverUserName;
        bc.ManualReviewedAt = DateTime.UtcNow;
        bc.ManualReviewComment = resolverNotes;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "FC1.3 OnRejectedAsync: BC {BcPublicId} rechazado y auto-reseteado a Drafted por {ResolverUserId}.",
            bc.PublicId, resolverUserId);
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

    // =========================================================================
    // FC1.3.3 (ADR-009 §2.7 + §2.3.4.bis, 2026-05-21): helpers privados FC1.3
    // =========================================================================

    /// <summary>
    /// FC1.3.3 (ADR-009 §2.8.3 + §2.7): abre el <c>ApprovalRequest</c> tipo
    /// <c>PartialCreditNoteApproval</c>, transiciona el BC a
    /// <c>ManualReviewPending</c>, serializa la liquidacion al Metadata JSON
    /// (schemaVersion=1) y emite el audit log. SIN llamadas a AFIP — eso solo
    /// pasa al aprobar.
    /// </summary>
    private async Task<BookingCancellationDto> SubmitForReviewAsync(
        BookingCancellation bc,
        FiscalLiquidationDto liquidation,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // 1) Armar metadata JSON con schemaVersion=1 (ADR-009 §2.7). Si en el
        //    futuro cambia el schema, se versiona y el reader detecta.
        var metadata = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["computedAt"] = DateTime.UtcNow,
            ["computedByUserId"] = userId,
            ["computedByUserName"] = userName,
            ["computedCase"] = liquidation.Case.ToString(),
            ["originalInvoiceAmount"] = liquidation.OriginalInvoiceAmount,
            ["cancellationAmount"] = liquidation.CancellationAmount,
            ["operatorPenaltyAmount"] = liquidation.OperatorPenaltyAmount,
            ["nonRefundableItemsAmount"] = liquidation.NonRefundableItemsAmount,
            ["fiscalAmountToCredit"] = liquidation.FiscalAmountToCredit,
            ["amountToRefundCustomer"] = liquidation.AmountToRefundCustomer,
            ["finalNetInvoiced"] = liquidation.FinalNetInvoiced,
            ["creditNoteKind"] = liquidation.Kind.ToString(),
            ["reviewRequiredReason"] = liquidation.ReviewRequiredReason.ToString(),
            ["currency"] = liquidation.Currency,
            ["classificationExplanation"] = liquidation.ClassificationExplanation,
            ["accountingReviewRequired"] = liquidation.ReviewRequiredReason.HasFlag(ReviewRequiredReason.AmountAboveAccountingThreshold),
            ["selfApprovedDueToSingleAdmin"] = false,
            ["edits"] = new List<object>(),
        };
        var metadataJson = JsonSerializer.Serialize(metadata);

        // 2) Crear el ApprovalRequest via el service (ApprovalRequestService lo
        //    persiste con sus defaults — expiration, cooldown, etc.).
        var approvalDto = await _approvalService.CreateAsync(
            new CreateApprovalRequestPayload(
                RequestType: ApprovalRequestType.PartialCreditNoteApproval.ToString(),
                EntityType: "BookingCancellation",
                EntityId: bc.Id,
                Reason: $"NC parcial Hotel - case {liquidation.Case}, motivos {liquidation.ReviewRequiredReason}",
                Metadata: metadataJson),
            requestedByUserId: userId,
            requestedByUserName: userName,
            ct: ct);

        // 3) Vincular FK del approval al BC. ApprovalRequestService.CreateAsync
        //    devuelve el dto sin el Id legacy, asi que lo buscamos por PublicId.
        var approvalEntity = await _db.ApprovalRequests
            .FirstOrDefaultAsync(a => a.PublicId == approvalDto.PublicId, ct)
            ?? throw new InvalidOperationException(
                $"ApprovalRequest {approvalDto.PublicId} no encontrado despues de crearlo.");

        bc.PartialCreditNoteApprovalRequestId = approvalEntity.Id;

        // 4) Transicion atomica Drafted -> ManualReviewPending. El estado
        //    intermedio RequiresManualReview (8) existe solo como marker
        //    semantico del enum y NO se persiste (ADR §2.8.1).
        bc.Status = BookingCancellationStatus.ManualReviewPending;
        bc.ConfirmedWithClientAt = DateTime.UtcNow;
        bc.ConfirmedByUserId = userId;
        bc.ConfirmedByUserName = userName;

        // 5) Audit del submit. Incluimos el detail completo de la liquidacion
        //    para que el reviewer pueda buscar por monto/caso sin abrir el
        //    approval. El JSON queda duplicado entre AuditLog y ApprovalRequest
        //    a proposito — son dos audits con TTL distintos.
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationSubmittedForReview,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                ReservaPublicId = bc.Reserva.PublicId,
                approvalRequestPublicId = approvalEntity.PublicId,
                creditNoteKind = liquidation.Kind.ToString(),
                reviewRequiredReason = liquidation.ReviewRequiredReason.ToString(),
                computedCase = liquidation.Case.ToString(),
                fiscalAmountToCredit = liquidation.FiscalAmountToCredit,
                amountToRefundCustomer = liquidation.AmountToRefundCustomer,
                accountingReviewRequired = liquidation.ReviewRequiredReason.HasFlag(ReviewRequiredReason.AmountAboveAccountingThreshold),
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        // 6) Save final: ApprovalRequest, BC summary, transicion de status,
        //    todo en un solo commit. Si EF tira (concurrency en BC, validacion
        //    constraint, etc.), nada se persiste y el caller recibe la excepcion.
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "FC1.3 SubmitForReview: BC {BcPublicId} -> ManualReviewPending, ApprovalRequest {ApprovalPublicId} creado. " +
            "Razon: {Reason}.",
            bc.PublicId, approvalEntity.PublicId, liquidation.ReviewRequiredReason);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException($"BC {bc.PublicId} no encontrado despues de submit for review.");
    }

    /// <summary>
    /// FC1.3.3 (ADR-009 §2.3.4.bis N-002 + GR-005): evalua si aplica el bypass
    /// de 4-ojos para single admin. Devuelve <c>true</c> si:
    ///  - <c>Allow4EyesBypassWhenSingleAdmin</c> setting esta en true.
    ///  - Hay EXACTAMENTE 1 admin activo (rol "Admin" + IsActive=true).
    ///  - El comentario es >= 100 chars (refuerzo G5).
    /// Devuelve <c>false</c> si alguno falla. El caller decide si tirar
    /// excepcion (en EditLiquidationAsync) o solo loguear (en OnApprovedAsync).
    /// </summary>
    private async Task<bool> TryApplyGr005BypassAsync(
        string? comment,
        OperationalFinanceSettings settings,
        CancellationToken ct)
    {
        if (!settings.Allow4EyesBypassWhenSingleAdmin)
            return false;

        if (string.IsNullOrWhiteSpace(comment) || comment.Trim().Length < 100)
            return false;

        var activeAdminCount = await _adminUserCount.CountActiveAdminsAsync(ct);
        return activeAdminCount == 1;
    }

    /// <summary>
    /// FC1.3.3: deserializa el <c>Metadata</c> JSON del approval a un Dictionary
    /// mutable. Si esta vacio o malformed, devuelve dict vacio (no tira) — el
    /// caller seguira escribiendo y guardando un JSON valido.
    /// </summary>
    private Dictionary<string, object?> DeserializeMetadataOrEmpty(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return new Dictionary<string, object?>();

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson);
            return parsed ?? new Dictionary<string, object?>();
        }
        catch (JsonException ex)
        {
            // Si el JSON estaba corrupto (no deberia pasar — siempre lo escribimos
            // nosotros), log warning y empezamos limpio. El audit log si guarda
            // el diff aparte.
            _logger.LogWarning(ex, "ApprovalRequest.Metadata JSON corrupto. Empezamos con dict vacio.");
            return new Dictionary<string, object?>();
        }
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
