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

    // FC1.3 Fase 3 (ADR-010 R1): evaluador compartido de la regla GR-005 (bypass
    // 4-ojos single-admin). Antes esta logica vivia inline en TryApplyGr005BypassAsync;
    // se extrajo a un servicio compartido para que la bandeja de reconciliacion (Fase 3)
    // use exactamente la misma evaluacion. Opcional en el ctor (default null) para no
    // romper los tests unit/integration existentes que construyen el service a mano: si
    // no se inyecta, se arma uno con el IAdminUserCountService ya presente (mismo
    // comportamiento, ya que el evaluator solo depende de ese servicio).
    private readonly IFourEyesBypassEvaluator _fourEyesBypassEvaluator;

    public BookingCancellationService(
        AppDbContext db,
        IInvoiceService invoiceService,
        IApprovalRequestService approvalService,
        IAuditService auditService,
        ILogger<BookingCancellationService> logger,
        IOperationalFinanceSettingsService settings,
        IFiscalLiquidationCalculator calculator,
        IAdminUserCountService adminUserCount,
        IFourEyesBypassEvaluator? fourEyesBypassEvaluator = null)
    {
        _db = db;
        _invoiceService = invoiceService;
        _approvalService = approvalService;
        _auditService = auditService;
        _logger = logger;
        _settings = settings;
        _calculator = calculator;
        _adminUserCount = adminUserCount;
        _fourEyesBypassEvaluator = fourEyesBypassEvaluator
            ?? new FourEyesBypassEvaluator(adminUserCount);
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
        CancellationToken ct,
        // ADR-013: el caller puede clasificar la penalidad como ingreso propio de la
        // agencia (lo que dispara una ND fiscal). Lo resuelve el controller contra el
        // permiso cancellations.classify_agency_penalty. Va DESPUES del CancellationToken
        // con default false para no romper callers posicionales legacy y ser conservador.
        bool userCanClassifyAgencyPenalty = false)
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
            // ADR-013: Supplier para poder sugerir el default de la clasificacion de la
            // penalidad a partir de Supplier.PenaltyOwnership ("depende del operador").
            .Include(b => b.Supplier)
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

        // 7-bis) ADR-013 (2026-06-01): capturar la clasificacion de la penalidad.
        //
        // Aca es donde el usuario, al confirmar la cancelacion con el operador,
        // informa si la penalidad es ingreso propio de la agencia (-> ND) o del
        // operador (pass-through -> NO ND), el estado (Confirmed/Estimated), la
        // finalidad y el monto confirmado. Si no informa nada, todo queda en los
        // defaults conservadores (pass-through / Estimated) y el comportamiento es
        // byte-identico a hoy (NC total, sin ND).
        //
        // Lo hacemos ANTES de la transicion (step 8) y de encolar la NC, asi cuando
        // mas tarde corra OnArcaSucceededAsync (post-CAE de la NC), el BC ya tiene la
        // clasificacion seteada y el gating de la ND la lee.
        //
        // B1 (review 2026-06-01): la captura SOLO corre si el flag de la ND esta ON.
        // Cargamos settings ACA (y lo reusamos para la rama NC parcial mas abajo) para
        // decidirlo. Con EnableCancellationDebitNote=false el metodo NO toca ningun campo
        // de clasificacion ni lanza excepcion -> ConfirmAsync queda byte-identico al
        // comportamiento previo a ADR-013 (commit d29ac8a), donde ConceptKind nunca se
        // escribia y quedaba en su default OperatorPenaltyPassThrough.
        var settings = await _settings.GetEntityAsync(ct);
        // ADR-014 (M1): el path sincrono mapea su request al record comun de clasificacion.
        // La logica de captura es identica al diferido; solo cambia la fuente de los campos.
        var classification = new PenaltyClassificationInput(
            PenaltyConceptKind: request.PenaltyConceptKind,
            PenaltyStatus: request.PenaltyStatus,
            DebitNotePurpose: request.DebitNotePurpose,
            ConfirmedPenaltyAmount: request.ConfirmedPenaltyAmount);
        CaptureDebitNoteClassification(
            bc, classification, userId, userName, userCanClassifyAgencyPenalty,
            debitNoteFeatureEnabled: settings.EnableCancellationDebitNote);

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
        // settings ya fue cargado arriba (step 7-bis) para gatear la captura de la ND.
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

            // (f) Persistir summary (GR-004) + detalle completo (FC1.3 Fase 2, RH-002).
            //
            // Capturamos el timestamp UNA sola vez en una variable local y lo usamos
            // tanto para la columna summary LiquidationComputedAt como para el VO
            // FiscalLiquidation.ComputedAt. Es CRITICO que sean el MISMO valor: el
            // CHECK chk_BookingCancellations_fiscalliquidation_consistency exige
            // igualdad EXACTA entre ambos. Si usaramos dos DateTime.UtcNow distintos
            // (uno por linea), Postgres rebotaria el INSERT/UPDATE.
            var computedAt = DateTime.UtcNow;

            bc.CreditNoteKind = liquidation.Kind;
            bc.ReviewRequiredReason = liquidation.ReviewRequiredReason;
            bc.LiquidationComputedAt = computedAt;
            bc.LiquidationComputedByUserId = userId;
            bc.LiquidationComputedByUserName = userName;

            // FC1.3 Fase 2 (RH-002): doble-write. Persistimos el detalle COMPLETO de
            // la liquidacion en las 10 columnas dedicadas, ademas del summary de
            // arriba. Esto cubre los DOS sub-paths que siguen:
            //  - auto-aprobable (reason None): cae al step 8 y se guarda en el
            //    SaveChanges del paso 9.
            //  - manual review (reason != None): va a SubmitForReviewAsync, que
            //    serializa el MISMO detalle al Metadata JSON y hace su propio
            //    SaveChanges.
            // En ambos casos el VO ya quedo seteado en la entidad trackeada.
            //
            // B-FISC-1 (decision Gaston, opcion A): EXCEPCION para modo CommissionOnly.
            // En CommissionOnly (operador intermediario) el calculator hace early-exit
            // y devuelve FiscalAmountToCredit=0 + NonRefundableItemsAmount=0 +
            // OperatorPenaltyAmount=penalty con OriginalInvoiceAmount>0. Esa terna NO
            // cumple el CHECK de suma (0+0+penalty != original), asi que persistir el VO
            // haria rebotar a Postgres (SqlState 23514) una operacion LEGITIMA que solo
            // va a revision manual. Ademas semanticamente en intermediario NO hay un
            // "total a descomponer" en componentes fiscales — la NC depende solo de la
            // comision, formula que Fase 2 todavia no modela (espera respuesta F2 del
            // contador). Por eso dejamos el VO en NULL: las columnas FiscalLiquidation_*
            // quedan NULL y el CHECK no aplica (clausula "...IS NULL OR..."). El detalle
            // igual viaja al JSON Metadata via SubmitForReviewAsync, para que el humano
            // que revisa manualmente vea los numeros del input.
            if (!IsCommissionOnlyLiquidation(liquidation))
            {
                bc.FiscalLiquidation = BuildFiscalLiquidationVo(liquidation, computedAt, userId, userName);
            }

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

    /// <inheritdoc />
    public async Task<IReadOnlyList<CancellationDebitNotePendingDto>> GetCancellationsWithMissingDebitNoteAsync(
        CancellationToken ct)
    {
        // (1) Traer los BC con NC ya emitida (CreditNoteInvoiceId seteado) cuya ND quedo
        //     pendiente o fallida. Trackeados (no AsNoTracking) porque podemos reconciliar
        //     el estado de los Pending y persistir la transicion.
        var pendingStates = new[] { DebitNoteStatus.Pending, DebitNoteStatus.Failed };
        var candidates = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.DebitNoteInvoice)
            .Where(b => b.CreditNoteInvoiceId != null &&
                        pendingStates.Contains(b.DebitNoteStatus))
            .ToListAsync(ct);

        // (2) Reconciliar los Pending: la ND la emite el job async (ProcessInvoiceJob), que
        //     setea Invoice.Resultado ("A"=Aprobado / "R"=Rechazado / "PENDING"=en vuelo).
        //     Leemos ese resultado y, si ya cerro, transicionamos DebitNoteStatus. No hay
        //     callback dedicado: esta lectura ES la reconciliacion (mismo espiritu que el
        //     "barrendero" de FC1.3).
        var changed = false;
        foreach (var bc in candidates)
        {
            if (bc.DebitNoteStatus != DebitNoteStatus.Pending) continue;
            var nd = bc.DebitNoteInvoice;
            if (nd is null) continue;

            if (string.Equals(nd.Resultado, "A", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(nd.CAE))
            {
                bc.DebitNoteStatus = DebitNoteStatus.Issued;
                bc.DebitNoteArcaErrorMessage = null;
                changed = true;
            }
            else if (string.Equals(nd.Resultado, "R", StringComparison.OrdinalIgnoreCase))
            {
                bc.DebitNoteStatus = DebitNoteStatus.Failed;
                var obs = nd.Observaciones ?? "ARCA rechazo la ND sin mensaje.";
                bc.DebitNoteArcaErrorMessage = obs.Length > 1000 ? obs[..1000] : obs;
                changed = true;
            }
            // Resultado == "PENDING" o null: sigue en vuelo, lo dejamos en Pending.
        }

        // (2-bis) ADR-014 (§3.8 pieza 3, M-R2-1): segunda rama de la bandeja para la ND
        //     HUERFANA o NUNCA CREADA. El query (1) proyecta sobre BCs que YA tienen
        //     DebitNoteInvoiceId (la ND ya vinculada) -> nunca ve un BC con
        //     DebitNoteInvoiceId == null. El flujo diferido introduce dos casos nuevos que
        //     ese query no captura:
        //       (a) ND huerfana: el motor creo la ND (T1 commiteo) pero el link al BC (T2)
        //           nunca corrio (crash entre crear y vincular). El BC quedo con
        //           PenaltyStatus=Confirmed + DebitNoteInvoiceId=null, pero EXISTE una ND
        //           para la factura original. -> re-vincular, NO re-emitir.
        //       (b) ND nunca creada: PenaltyStatus=Confirmed pero el motor rebanoto a
        //           ManualReview / no llego a crear nada. No hay ND para esa factura. -> la
        //           dejamos visible en la bandeja para re-disparo manual (el endpoint
        //           confirm-penalty ya rebota por PenaltyStatus=Confirmed).
        //     La marca PenaltyStatus=Confirmed garantiza que re-vincular NUNCA re-emite.
        var orphanCandidates = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Where(b => b.CreditNoteInvoiceId != null &&
                        b.DebitNoteInvoiceId == null &&
                        b.PenaltyStatus == PenaltyStatus.Confirmed)
            .ToListAsync(ct);

        var orphanRows = new List<CancellationDebitNotePendingDto>();
        foreach (var bc in orphanCandidates)
        {
            // Buscar una ND existente para la MISMA factura original del BC y la misma
            // reserva. Tipos de ND: 2 (A), 7 (B), 12 (C), 52 (M). Validar OriginalInvoiceId
            // == bc.OriginatingInvoiceId es lo que evita re-vincular una ND de otro evento.
            var orphanDebitNote = await _db.Invoices
                .Where(i => debitNoteTipos.Contains(i.TipoComprobante) &&
                            i.OriginalInvoiceId == bc.OriginatingInvoiceId &&
                            i.ReservaId == bc.ReservaId)
                .OrderByDescending(i => i.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (orphanDebitNote is not null)
            {
                // Caso (a): re-vincular la ND huerfana al BC. NO se emite otra. El estado de
                // la ND se deriva de su Resultado (igual que la reconciliacion de arriba).
                bc.DebitNoteInvoiceId = orphanDebitNote.Id;
                bc.DebitNoteStatus = ResolveDebitNoteStatusFromInvoice(orphanDebitNote);
                if (bc.DebitNoteStatus == DebitNoteStatus.Failed)
                {
                    var obs = orphanDebitNote.Observaciones ?? "ARCA rechazo la ND sin mensaje.";
                    bc.DebitNoteArcaErrorMessage = obs.Length > 1000 ? obs[..1000] : obs;
                }
                changed = true;

                _logger.LogWarning(
                    "ADR-014: BC {BcPublicId} tenia una ND huerfana (Invoice {InvoiceId}) sin vincular. " +
                    "Re-vinculada (NO re-emitida). Nuevo DebitNoteStatus={Status}.",
                    bc.PublicId, orphanDebitNote.Id, bc.DebitNoteStatus);

                // Si quedo Pending/Failed, la incluimos en la bandeja (sigue incompleta).
                if (bc.DebitNoteStatus is DebitNoteStatus.Pending or DebitNoteStatus.Failed)
                    orphanRows.Add(MapPendingDebitNoteRow(bc));
            }
            else
            {
                // Caso (b): no hay ND para esa factura. PenaltyStatus=Confirmed sin ND ->
                // visible en la bandeja para re-disparo manual. La marcamos como Failed para
                // que aparezca con un motivo claro (no quedo emitida).
                orphanRows.Add(MapPendingDebitNoteRow(
                    bc,
                    overrideStatus: CancellationDebitNotePendingDto.ConfirmedWithoutDebitNotePseudoStatus));
            }
        }

        if (changed)
            await _db.SaveChangesAsync(ct);

        // (2-ter) ADR-014 (M-B2, caso DOMINANTE del negocio): penalidad de cargo PROPIO de
        //     la agencia que quedo ESTIMADA (PenaltyStatus=Estimated), con la NC total ya
        //     emitida y sin ND. Estos BCs no entran en NINGUNA de las ramas anteriores:
        //       - el query (1) filtra por DebitNoteStatus in {Pending, Failed}, pero un BC
        //         estimado tiene DebitNoteStatus=NotApplicable (todavia no aplica ND);
        //       - la rama huerfana (2-bis) filtra por PenaltyStatus=Confirmed.
        //     Sin esta rama, el agente que cancelo con un cargo propio cuyo monto el operador
        //     aun no confirmo NUNCA volveria a verlo en la bandeja -> la ND de ese cargo jamas
        //     se emitiria. El frontend abre el ConfirmPenaltyModal desde estas filas.
        //
        //     SOLO listamos los concepto agency-owned (AgencyManagementFee / AgencyCancellationFee):
        //     un pass-through (operador retiene la penalidad) NUNCA lleva ND, asi que no tiene
        //     sentido pedir su confirmacion. NO usamos el helper ConceptIsAgencyOwnedDebitNote
        //     en el Where porque EF Core no lo traduce a SQL; inlineamos los dos valores del
        //     enum (mantener en sync con ese helper si cambia la definicion de agency-owned).
        //
        //     Es una rama de SOLO LECTURA: no reconcilia ni muta estado (no hay ND que mirar
        //     todavia). Por eso AsNoTracking y va DESPUES del SaveChanges de las otras ramas.
        var estimatedAgencyOwnedRows = new List<CancellationDebitNotePendingDto>();
        var estimatedCandidates = await _db.BookingCancellations
            .AsNoTracking()
            .Include(b => b.Reserva)
            .Where(b => b.CreditNoteInvoiceId != null &&
                        b.DebitNoteInvoiceId == null &&
                        b.PenaltyStatus == PenaltyStatus.Estimated &&
                        (b.ConceptKind == CancellationConceptKind.AgencyManagementFee ||
                         b.ConceptKind == CancellationConceptKind.AgencyCancellationFee))
            .ToListAsync(ct);

        foreach (var bc in estimatedCandidates)
        {
            estimatedAgencyOwnedRows.Add(MapPendingDebitNoteRow(
                bc,
                overrideStatus: CancellationDebitNotePendingDto.EstimatedPendingConfirmationPseudoStatus));
        }

        // (3) Proyectar SOLO los que siguen incompletos despues de reconciliar (los que
        //     pasaron a Issued ya no son problema y salen de la bandeja). Sumamos los
        //     huerfanos detectados por la segunda rama (ADR-014) y los estimados de cargo
        //     propio que esperan confirmacion del monto (M-B2).
        var rows = candidates
            .Where(b => b.DebitNoteStatus is DebitNoteStatus.Pending or DebitNoteStatus.Failed)
            .Select(b => MapPendingDebitNoteRow(b))
            .ToList();
        rows.AddRange(orphanRows);
        rows.AddRange(estimatedAgencyOwnedRows);
        return rows;
    }

    /// <summary>ADR-013/014: tipos de comprobante de Nota de Debito (A=2, B=7, C=12, M=52).</summary>
    private static readonly int[] debitNoteTipos = { 2, 7, 12, 52 };

    /// <summary>
    /// ADR-014: deriva el <see cref="DebitNoteStatus"/> observable a partir del Resultado de
    /// la Invoice ND (A=Issued con CAE / R=Failed / en vuelo=Pending). Misma logica que la
    /// reconciliacion principal, extraida para reusarla en la rama de ND huerfana.
    /// </summary>
    private static DebitNoteStatus ResolveDebitNoteStatusFromInvoice(Invoice debitNote)
    {
        if (string.Equals(debitNote.Resultado, "A", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(debitNote.CAE))
            return DebitNoteStatus.Issued;
        if (string.Equals(debitNote.Resultado, "R", StringComparison.OrdinalIgnoreCase))
            return DebitNoteStatus.Failed;
        return DebitNoteStatus.Pending;
    }

    /// <summary>ADR-013/014: proyecta un BC a la fila de la bandeja "NC sin su ND".</summary>
    private static CancellationDebitNotePendingDto MapPendingDebitNoteRow(
        BookingCancellation b, string? overrideStatus = null)
        => new CancellationDebitNotePendingDto
        {
            BookingCancellationPublicId = b.PublicId,
            ReservaNumero = b.Reserva?.NumeroReserva ?? string.Empty,
            DebitNoteStatus = overrideStatus ?? b.DebitNoteStatus.ToString(),
            PenaltyAmount = b.PenaltyAmountAtEvent,
            PenaltyCurrency = b.PenaltyCurrencyAtEvent,
            DebitNoteCbteTipo = b.DebitNoteCbteTipoAtEvent,
            ArcaErrorMessage = b.DebitNoteArcaErrorMessage,
            ConfirmedAt = b.ConfirmedWithClientAt,
        };

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

        // 8.bis) FC1.3 Fase 2 (RH-002): doble-write en el edit. Actualizamos las 10
        // columnas FiscalLiquidation_* con los nuevos montos del calculator, igual
        // que se reescriben las claves top-level del Metadata mas abajo. Ambas
        // representaciones tienen que reflejar el cambio (test
        // EditLiquidation_PostFase2_UpdatesBothRepresentations).
        //
        // IMPORTANTE — ComputedAt NO cambia: el edit re-corre el calculator pero NO
        // re-setea bc.LiquidationComputedAt (el "cuando se calculo originalmente" se
        // preserva; el "cuando se edito" queda en Metadata.edits[].at). Por eso el VO
        // mantiene el ComputedAt original. El CHECK de consistencia sigue cumpliendose
        // porque VO.ComputedAt == bc.LiquidationComputedAt (ninguno de los dos cambia).
        //
        // Fallback defensivo: si por backfill incompleto el VO viniera null, lo creamos
        // usando el LiquidationComputedAt ya persistido (no un UtcNow nuevo, que romperia
        // el CHECK). En BCs normales nunca es null aca: la migracion M1 los backfillea.
        //
        // B-FISC-1 (decision Gaston opcion A): si el edit re-clasifico a CommissionOnly
        // NO persistimos el VO (lo dejamos null, igual que en ConfirmAsync) — la terna
        // 0+0+penalty violaria el CHECK de suma. El JSON top-level si se actualiza igual
        // (lo necesita el humano que revisa). En el flujo normal del edit el modo no
        // cambia, pero el penalty override podria mover el caso, asi que aplicamos la
        // misma guarda por las dudas y para mantener coherencia con Confirm.
        if (IsCommissionOnlyLiquidation(newLiquidation))
        {
            bc.FiscalLiquidation = null;
        }
        else
        {
            var computedAtForEdit = bc.LiquidationComputedAt ?? DateTime.UtcNow;
            bc.FiscalLiquidation = BuildFiscalLiquidationVo(
                newLiquidation, computedAtForEdit, userId, userName);
        }

        // 9) Actualizar Metadata del approval. RH-006 cubierto: si otro admin edito
        //    entre la lectura y el save, EF tira DbUpdateConcurrencyException via xmin
        //    del ApprovalRequest.
        var approval = bc.PartialCreditNoteApprovalRequest;
        var metadataObj = DeserializeMetadataOrEmpty(approval.Metadata);

        // 9.a) B1 fix (RH-002): reescribir las claves TOP-LEVEL del Metadata con los
        // montos NUEVOS del calculator. Antes solo se appendeaba a edits[] y las claves
        // top-level (fiscalAmountToCredit, operatorPenaltyAmount, etc.) quedaban con el
        // valor PRE-edit. Eso hacia divergir el JSON top-level de las columnas
        // FiscalLiquidation_*, violando el doble-write y corrompiendo el rollback de la
        // migracion (que lee el JSON top-level como fuente de verdad). El historico de
        // cambios queda en edits[] (paso 9.b); el top-level refleja SIEMPRE el estado
        // actual de la liquidacion. computedCase tambien se actualiza porque un edit
        // puede mover el caso (ej. penalty override que dispara Case3).
        metadataObj["computedCase"] = newLiquidation.Case.ToString();
        metadataObj["originalInvoiceAmount"] = newLiquidation.OriginalInvoiceAmount;
        metadataObj["cancellationAmount"] = newLiquidation.CancellationAmount;
        metadataObj["operatorPenaltyAmount"] = newLiquidation.OperatorPenaltyAmount;
        metadataObj["nonRefundableItemsAmount"] = newLiquidation.NonRefundableItemsAmount;
        metadataObj["fiscalAmountToCredit"] = newLiquidation.FiscalAmountToCredit;
        metadataObj["amountToRefundCustomer"] = newLiquidation.AmountToRefundCustomer;
        metadataObj["finalNetInvoiced"] = newLiquidation.FinalNetInvoiced;
        metadataObj["creditNoteKind"] = bc.CreditNoteKind?.ToString();
        metadataObj["reviewRequiredReason"] = bc.ReviewRequiredReason.ToString();
        metadataObj["currency"] = newLiquidation.Currency;
        metadataObj["classificationExplanation"] = newLiquidation.ClassificationExplanation;

        // 9.b) Append al historico edits[] (no se pisa, se acumula). Mantiene el
        //    rastro de quien edito que y cuando, independiente del top-level actual.
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
        // RH-012: acumulamos el historico de ediciones en edits[] sin pisarlo.
        //
        // OJO con el round-trip de System.Text.Json: DeserializeMetadataOrEmpty
        // deserializa a Dictionary<string, object?>. Cuando un valor es un array
        // JSON, System.Text.Json NO lo materializa como List<object>, lo deja como
        // JsonElement (ValueKind == Array). Por eso `existing is List<object>` da
        // SIEMPRE false al releer un metadata que ya fue serializado y guardado en
        // la edicion anterior. El bug que evitamos: en el 2do edit consecutivo,
        // edits[] se reescribia con un solo elemento y se perdia el rastro previo
        // (auditoria fiscal RH-012). Reconstruimos la lista enumerando el JsonElement.
        var edits = new List<object>();
        if (metadataObj.TryGetValue("edits", out var existing))
        {
            if (existing is JsonElement editsElement && editsElement.ValueKind == JsonValueKind.Array)
            {
                // Caso normal: el metadata viene de un round-trip (ya fue guardado
                // y releido). Cada item es un JsonElement, que es serializable de
                // vuelta sin problema (re-serializa al JSON original).
                foreach (var item in editsElement.EnumerateArray())
                    edits.Add(item);
            }
            else if (existing is List<object> previousEdits)
            {
                // Caso borde: el dict todavia tiene la List<object> en memoria sin
                // haber pasado por un round-trip (p.ej. inicializada por
                // SubmitForReviewAsync dentro de la misma operacion).
                edits.AddRange(previousEdits);
            }
        }
        edits.Add(newEdit);
        metadataObj["edits"] = edits;
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
        //
        // ADR-013: incluimos OriginatingInvoice + Supplier porque el gating de la ND
        // (despues de transicionar) los necesita (tipo de comprobante de la factura
        // original, "quien se queda la penalidad" del operador). Con el flag OFF estos
        // Includes solo cargan datos que ya se usan en otros lados; no cambian el
        // comportamiento.
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.OriginatingInvoice)
                // ADR-013 (fix falso negativo de Tributos): el gating de la ND rutea a
                // revision manual las facturas con tributos provinciales (IIBB). SIN este
                // ThenInclude, EF deja Invoice.Tributes con su default del constructor (lista
                // VACIA, no null), asi que el gating leeria "0 tributos" aunque la BD tenga
                // IIBB -> emitiria una ND sobre una factura que debia ir a manual. Con el
                // Include, el gating ve los tributos reales. (El proyecto NO usa lazy loading,
                // ver Program.cs AddDbContext, por eso el Include es obligatorio.)
                .ThenInclude(i => i.Tributes)
            .Include(b => b.Supplier)
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

        // ADR-013 (2026-06-01): ORDEN NO NEGOCIABLE — la ND se dispara SOLO ahora, despues
        // de que la NC total obtuvo CAE (este callback es exactamente ese momento). Si la
        // NC hubiera fallado, este metodo nunca corre (corre OnArcaFailedAsync), asi que la
        // ND no se dispara sobre una NC fallida. Con el flag OFF, TryEmit... retorna sin
        // tocar nada (byte-identico a hoy). Va envuelto en try/catch porque una falla al
        // emitir la ND NO debe revertir la cancelacion: la NC ya quedo correcta.
        try
        {
            await TryEmitCancellationDebitNoteAsync(bc, ct);
        }
        catch (Exception ex)
        {
            // No re-lanzamos: la cancelacion ya esta correcta con la NC. La ND quedo en
            // Failed (o sin tocar) y la bandeja "NC sin su ND" la levanta para reintento/
            // manual. Re-lanzar pondria al job de Hangfire en retry-loop sobre una NC que
            // ya esta commiteada (peor).
            _logger.LogError(ex,
                "ADR-013: fallo al intentar emitir la ND para BC {BcPublicId} tras NC exitosa. " +
                "La cancelacion queda correcta (NC emitida); la ND queda pendiente de revision.",
                bc.PublicId);
        }
    }

    // =========================================================================
    // ADR-014 (2026-06-02): confirmacion DIFERIDA de la penalidad + disparo de la ND.
    // =========================================================================

    /// <summary>
    /// ADR-014 (§3.2): estados del BC en los que la NC total YA obtuvo CAE (post-NC). La
    /// confirmacion diferida de la penalidad SOLO procede en estos: la ND nunca sale antes
    /// que la NC (regla dura heredada de ADR-013). <c>CreditNoteInvoiceId != null</c> se
    /// valida ademas explicitamente (precondicion 4): es la senial dura de "NC con CAE".
    /// </summary>
    private static readonly BookingCancellationStatus[] PostCreditNoteStatuses =
    {
        BookingCancellationStatus.AwaitingOperatorRefund,
        BookingCancellationStatus.ClientCreditApplied,
        BookingCancellationStatus.Closed,
        BookingCancellationStatus.AbandonedByOperator,
    };

    /// <inheritdoc />
    public async Task<BookingCancellationDto> ConfirmPenaltyAsync(
        Guid publicId,
        ConfirmPenaltyRequest request,
        string userId,
        string? userName,
        bool requesterIsAdmin,
        CancellationToken ct,
        bool userCanClassifyAgencyPenalty = false)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        // requesterIsAdmin se mantiene en la firma por paridad con ConfirmAsync, pero este
        // flujo NO lo lee: el gate de permiso ya viene resuelto en userCanClassifyAgencyPenalty
        // (el controller hace Admin-OR-permiso) y el 4-eyes se decide por documento+monto, no
        // por rol. No lo borramos para no divergir la firma de los dos confirmadores.
        _ = requesterIsAdmin;

        var settings = await _settings.GetEntityAsync(ct);

        // === Precondicion 1: flag maestro. Con OFF el endpoint es INERTE (rechaza, no muta
        // nada) -> byte-identidad con el comportamiento previo a ADR-014. ===
        if (!settings.EnableCancellationDebitNote)
            throw new InvalidOperationException(
                "La emision de Nota de Debito por penalidad esta deshabilitada " +
                "(EnableCancellationDebitNote OFF).");

        // === Precondicion 2: el BC existe (404 si no). Cargamos los mismos Includes que
        // el gating necesita: factura original + sus Tributos (IIBB) + Supplier + Reserva.
        // Mismo set que OnArcaSucceededAsync para que TryEmit no se quede corto. ===
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.OriginatingInvoice)
                .ThenInclude(i => i.Tributes)
            .Include(b => b.Supplier)
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // === Precondicion 3: permiso elevado, ya resuelto server-side por el controller.
        // A diferencia del path sincrono (que degrada a pass-through si falta el permiso),
        // aca lo EXIGIMOS: el flujo diferido existe para disparar la ND, no tiene sentido
        // sin el permiso que la habilita. ===
        if (!userCanClassifyAgencyPenalty)
            throw new BusinessInvariantViolationException(
                "Confirmar la penalidad propia de la agencia emite una Nota de Debito fiscal " +
                "y requiere el permiso 'cancellations.classify_agency_penalty'.",
                invariantCode: "INV-ADR014-PERM");

        // === Precondicion 4: estado post-NC con CAE. Nunca emitir la ND antes que la NC. ===
        if (!PostCreditNoteStatuses.Contains(bc.Status) || bc.CreditNoteInvoiceId is null)
            throw new BusinessInvariantViolationException(
                $"La confirmacion diferida de la penalidad requiere que la NC total ya tenga " +
                $"CAE (estado post-NC + CreditNoteInvoiceId seteado). Estado actual: {bc.Status}, " +
                $"CreditNoteInvoiceId: {(bc.CreditNoteInvoiceId is null ? "null" : "seteado")}.",
                invariantCode: "INV-ADR014-001");

        // === Precondicion 5: concepto agency-owned. Resolvemos el concepto efectivo (el
        // explicito del request, o el default por operador) y rechazamos si es pass-through:
        // una penalidad del operador NO emite ND (seria declarar ingreso ajeno). ===
        var effectiveConcept = request.ConceptKind
            ?? DefaultConceptFromSupplier(bc.Supplier?.PenaltyOwnership);
        if (!ConceptIsAgencyOwnedDebitNote(effectiveConcept))
            throw new BusinessInvariantViolationException(
                "Esta penalidad no es ingreso propio de la agencia (pass-through del operador): " +
                "no corresponde emitir Nota de Debito. Este endpoint es solo para penalidades propias.",
                invariantCode: "INV-ADR014-002");

        // === Precondicion 6: pre-check de idempotencia (B1, §3.4 pieza 1). Rebota con 409
        // idempotente si la ND ya esta en juego o la penalidad ya fue confirmada por una
        // corrida anterior. La condicion PenaltyStatus==Confirmed es la que cierra la ventana
        // de doble emision tras un crash entre crear-la-ND y vincularla: la marca Confirmed
        // se persiste ANTES de crear la ND (pieza 2 abajo). ===
        var debitNoteAlreadyInPlay =
            bc.PenaltyStatus == PenaltyStatus.Confirmed ||
            bc.DebitNoteInvoiceId.HasValue ||
            bc.DebitNoteStatus == DebitNoteStatus.Pending ||
            bc.DebitNoteStatus == DebitNoteStatus.Issued;
        if (debitNoteAlreadyInPlay)
            throw new BusinessInvariantViolationException(
                "La penalidad ya fue confirmada o la Nota de Debito ya esta en juego para esta " +
                $"cancelacion (PenaltyStatus={bc.PenaltyStatus}, DebitNoteStatus={bc.DebitNoteStatus}, " +
                $"DebitNoteInvoiceId={(bc.DebitNoteInvoiceId.HasValue ? "seteado" : "null")}). " +
                "No se vuelve a emitir.",
                invariantCode: "INV-ADR014-003");

        // === Precondicion 7: fecha de confirmacion del operador valida (400). No futura;
        // no anterior a la fecha de la cancelacion (ConfirmedWithClientAt). ===
        var operatorDate = request.OperatorConfirmationDate;
        if (operatorDate.Date > DateTime.UtcNow.Date)
            throw new ArgumentException(
                "OperatorConfirmationDate no puede ser una fecha futura.", nameof(request));
        if (bc.ConfirmedWithClientAt.HasValue &&
            operatorDate.Date < bc.ConfirmedWithClientAt.Value.Date)
            throw new ArgumentException(
                "OperatorConfirmationDate no puede ser anterior a la fecha de la cancelacion.",
                nameof(request));

        // === 4-eyes (M2, §3.6). Obligatorio si NO hay soporte documental O si el monto
        // supera el umbral configurable, aunque el caller tenga el permiso base. Mismo patron
        // que Confirm: si falta el approval valido, tiramos ApprovalRequiredException (el
        // controller la traduce a 409 requiresApproval). ===
        var requiresFourEyes =
            string.IsNullOrWhiteSpace(request.SupportingDocumentReference) ||
            request.ConfirmedPenaltyAmount > settings.CancellationDebitNoteFourEyesThreshold;
        if (requiresFourEyes)
            await EnsureFourEyesApprovalAsync(bc, request, userId, ct);

        // === Aplicar la clasificacion (B1, §3.4 pieza 2, paso a). Reusa el MISMO metodo del
        // path sincrono via el record comun: setea ConceptKind, PenaltyStatus=Confirmed,
        // DebitNotePurpose, PenaltyAmountAtEvent + la auditoria del clasificador/confirmador,
        // y enforza las guardas (permiso elevado + anti-reclasificacion). ===
        var classification = new PenaltyClassificationInput(
            PenaltyConceptKind: effectiveConcept,
            PenaltyStatus: PenaltyStatus.Confirmed,
            DebitNotePurpose: request.DebitNotePurpose
                ?? Domain.Entities.DebitNotePurpose.PenaltyOrCancellationCharge,
            ConfirmedPenaltyAmount: request.ConfirmedPenaltyAmount);
        CaptureDebitNoteClassification(
            bc, classification, userId, userName,
            userCanClassifyAgencyPenalty: true, // ya validado en la precondicion 3
            debitNoteFeatureEnabled: true);

        // Paso b: las dos fechas nuevas del diferido (eje fiscal del plazo + soporte).
        bc.OperatorPenaltyConfirmedDate = operatorDate;
        bc.SupportingDocumentReference = request.SupportingDocumentReference;

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationArcaSucceeded,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                action = "deferred-penalty-confirmed",
                conceptKind = bc.ConceptKind.ToString(),
                confirmedAmount = request.ConfirmedPenaltyAmount,
                operatorConfirmationDate = operatorDate,
                hasSupportingDocument = !string.IsNullOrWhiteSpace(request.SupportingDocumentReference),
                fourEyesApplied = requiresFourEyes,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        // === Paso c (B1, §3.4 pieza 2): COMMIT PROPIO de la marca de no-retorno. Dejamos
        // PenaltyStatus=Confirmed durable ANTES de crear la ND. Si este commit choca por xmin
        // (otro proceso toco el BC), todavia NO se creo ninguna ND y el 409 es seguro de
        // reintentar. NO fusionar con el SaveChanges interno de TryEmit. ===
        await _db.SaveChangesAsync(ct);

        // === Paso d: disparar la ND reusando el motor existente de ADR-013. TryEmit hace su
        // propio gating (incluye el anti-doble-cobro RE-evaluado en runtime con query fresca
        // del Dia N, §3.8/R13), la emision async, el snapshot y su propio SaveChanges que
        // vincula DebitNoteInvoiceId. NO toca el balance ni el estado de la reserva (B2). ===
        // Alerta de plazo (§3.5): no bloqueante, solo observabilidad.
        WarnIfDebitNoteLate(bc, operatorDate, settings);
        await TryEmitCancellationDebitNoteAsync(bc, ct);

        _logger.LogInformation(
            "metric:cancellation_debit_note_deferred_confirmed | BcPublicId={BcPublicId} " +
            "Amount={Amount} DebitNoteStatus={DebitNoteStatus}",
            bc.PublicId, request.ConfirmedPenaltyAmount, bc.DebitNoteStatus);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException($"BC {publicId} no encontrada tras confirmar la penalidad.");
    }

    /// <summary>
    /// ADR-014 (§3.6, M2): valida el 4-eyes de la confirmacion diferida reusando el patron
    /// de approval de <c>Confirm</c>. Si el caller no trae un <c>InvariantOverride</c>
    /// aprobado, scoped a este BC, solicitado por el mismo usuario y no vencido, tira
    /// <see cref="ApprovalRequiredException"/> (el controller -> 409 requiresApproval).
    /// </summary>
    private async Task EnsureFourEyesApprovalAsync(
        BookingCancellation bc, ConfirmPenaltyRequest request, string userId, CancellationToken ct)
    {
        if (request.ApprovalRequestPublicId is null)
            throw new ApprovalRequiredException(
                ApprovalRequestType.InvariantOverride, "BookingCancellation", bc.Id);

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
    }

    /// <summary>
    /// ADR-014 (§3.5, M4): alerta de plazo NO bloqueante. Si pasaron mas dias que el plazo
    /// de gracia desde que el operador confirmo, loguea un warning + counter para que el
    /// back-office lo vea. Un segundo umbral (mas alto) eleva el aviso. NO bloquea la
    /// emision: la validez fiscal de una ND tardia la decide el contador, no el software.
    /// </summary>
    private void WarnIfDebitNoteLate(
        BookingCancellation bc, DateTime operatorConfirmationDate, OperationalFinanceSettings settings)
    {
        var daysSinceOperatorConfirmed = (DateTime.UtcNow.Date - operatorConfirmationDate.Date).TotalDays;

        if (daysSinceOperatorConfirmed > settings.CancellationDebitNoteHardWarnDays)
        {
            _logger.LogWarning(
                "ADR-014: BC {BcPublicId} confirma penalidad MUY tarde ({Days} dias desde la " +
                "confirmacion del operador, umbral duro {Threshold}). Se emite igual; revisar " +
                "validez fiscal con el contador. metric:cancellation_debit_note_very_late",
                bc.PublicId, daysSinceOperatorConfirmed, settings.CancellationDebitNoteHardWarnDays);
        }
        else if (daysSinceOperatorConfirmed > settings.CancellationDebitNoteGraceDays)
        {
            _logger.LogWarning(
                "ADR-014: BC {BcPublicId} confirma penalidad fuera del plazo de gracia ({Days} dias, " +
                "plazo {Threshold}). Se emite igual. metric:cancellation_debit_note_late",
                bc.PublicId, daysSinceOperatorConfirmed, settings.CancellationDebitNoteGraceDays);
        }
    }

    // =========================================================================
    // ADR-013 (2026-06-01): emision de la Nota de Debito por penalidad propia.
    // =========================================================================

    /// <summary>
    /// ADR-013 (2026-06-01): intenta emitir la Nota de Debito por la penalidad propia de
    /// la agencia, DESPUES de que la NC total ya obtuvo CAE. Es el corazon del MVP.
    ///
    /// <para><b>Conservador por diseño</b>: solo emite si TODO el gating (P3 del ADR) se
    /// cumple. Ante cualquier duda (pass-through, factura no-C, moneda no-ARS, penalidad
    /// estimada, penalidad &gt; factura, etc.) NO emite y rutea a revision manual marcando
    /// <see cref="DebitNoteStatus.ManualReview"/>. Con el flag OFF retorna de inmediato sin
    /// tocar nada -> comportamiento byte-identico a hoy.</para>
    ///
    /// <para><b>Idempotencia</b>: si el BC ya tiene <c>DebitNoteInvoiceId</c>, no crea otra
    /// ND (guard duro). El pipeline de emision ademas tiene su propio anti-doble-POST.</para>
    /// </summary>
    private async Task TryEmitCancellationDebitNoteAsync(BookingCancellation bc, CancellationToken ct)
    {
        var settings = await _settings.GetEntityAsync(ct);

        // (0) Flag maestro OFF -> comportamiento byte-identico a hoy (NC total, sin ND).
        //     Esta es la primera y mas importante guarda: TODA la logica nueva vive aca
        //     adentro. Mientras el flag siga apagado, nada de esto corre.
        if (!settings.EnableCancellationDebitNote)
            return;

        // (1) Idempotencia dura: si ya hay una ND vinculada, no creamos otra.
        if (bc.DebitNoteInvoiceId.HasValue)
        {
            _logger.LogInformation(
                "ADR-013: BC {BcPublicId} ya tiene ND vinculada (Id={DebitNoteInvoiceId}). No se crea otra.",
                bc.PublicId, bc.DebitNoteInvoiceId);
            return;
        }

        var originatingInvoice = bc.OriginatingInvoice;
        if (originatingInvoice is null)
        {
            // Defensivo: el caller (OnArcaSucceededAsync) hace el Include. Si falta, no
            // arriesgamos emitir con datos incompletos.
            _logger.LogWarning(
                "ADR-013: BC {BcPublicId} sin OriginatingInvoice cargada. Se rutea a revision manual.",
                bc.PublicId);
            await RouteDebitNoteToManualReviewAsync(bc, "OriginatingInvoice no cargada.", ct);
            return;
        }

        // (1-bis) FAIL-SAFE de Tributos (defensa en profundidad del fix del Include).
        //     El gating chequea originatingInvoice.Tributes para mandar a manual las
        //     facturas con IIBB. Esa coleccion se inicializa VACIA en el constructor de
        //     Invoice, asi que si por algun camino llego sin el ThenInclude, leeriamos
        //     "0 tributos" (falso negativo) y emitiriamos una ND sobre una factura con
        //     IIBB. Para no depender SOLO del Include, verificamos la existencia de
        //     tributos directamente contra la BD (no contra la coleccion en memoria). Si
        //     la BD dice que hay tributos pero la coleccion cargada no los tiene, forzamos
        //     manual. Es una query barata y conservadora (ante la duda, NO emitir).
        var dbTributesCount = await _db.Set<InvoiceTribute>()
            .CountAsync(t => t.InvoiceId == originatingInvoice.Id, ct);
        if (dbTributesCount > 0 && (originatingInvoice.Tributes?.Count ?? 0) == 0)
        {
            _logger.LogWarning(
                "ADR-013 fail-safe: BC {BcPublicId} factura {InvoiceId} tiene {Count} tributos en BD " +
                "pero la coleccion cargada esta vacia (Include faltante). Rutea a revision manual.",
                bc.PublicId, originatingInvoice.Id, dbTributesCount);
            await RouteDebitNoteToManualReviewAsync(
                bc, "Factura con tributos provinciales (fail-safe: coleccion no cargada).", ct);
            return;
        }

        // (2) Gating P3 (§3.4.1): ante la duda, NO emitir -> revision manual. Evaluamos
        //     cada condicion y juntamos los motivos para dejarlos en el log/auditoria.
        var manualReason = EvaluateDebitNoteGating(bc, originatingInvoice);
        if (manualReason is not null)
        {
            _logger.LogInformation(
                "ADR-013: BC {BcPublicId} NO califica para ND automatica ({Reason}). Rutea a revision manual.",
                bc.PublicId, manualReason);
            await RouteDebitNoteToManualReviewAsync(bc, manualReason, ct);
            return;
        }

        // (3) Disyuncion anti-doble-cobro (INV-ADR013-001, §3.3) desde el lado de la ND:
        //     si por algun camino quedo cargada una deduction CancellationPenalty para
        //     este BC, NO emitimos la ND (esa penalidad ya bajo el refund -> emitir la ND
        //     seria cobrarla dos veces). Va a revision manual. La guarda simetrica vive en
        //     OperatorRefundService (rechaza cargar la deduction si el concepto es ND propia).
        // (defensa simetrica: el concepto YA paso el gating como ND propia, pero validamos
        // que ademas no haya una deduction de penalidad cargada para este BC).
        var hasPenaltyDeduction = await _db.OperatorRefundAllocations
            .Where(a => a.BookingCancellationId == bc.Id && !a.IsVoided)
            .SelectMany(a => a.Deductions)
            .AnyAsync(d => d.Kind == DeductionKind.CancellationPenalty, ct);
        if (hasPenaltyDeduction)
        {
            _logger.LogWarning(
                "ADR-013 INV-ADR013-001: BC {BcPublicId} tiene una deduccion CancellationPenalty cargada " +
                "Y concepto de ND propia. No se emite la ND (seria doble cobro). Rutea a revision manual.",
                bc.PublicId);
            await RouteDebitNoteToManualReviewAsync(
                bc, "Penalidad cargada como deduccion del refund (anti-doble-cobro).", ct);
            return;
        }

        // (4) Construir el request de la ND y emitir por el pipeline existente.
        //     - IsDebitNote=true + OriginalInvoiceId=factura original -> el pipeline arma
        //       el <CbtesAsoc> y deriva CbteTipo=12 (ND C) con el fix M1 (§3.9).
        //     - Un solo item: el monto de la penalidad, AlicuotaIvaId=3 (0% / no gravado),
        //       que es lo que usa el sistema para comprobantes C (ImpIVA=0). El monto de la
        //       ND es INDEPENDIENTE del refund (no participa de ninguna suma del refund).
        var penaltyAmount = bc.PenaltyAmountAtEvent!.Value; // gating garantizo > 0
        var debitNoteRequest = new CreateInvoiceRequest
        {
            ReservaId = originatingInvoice.ReservaId!.Value.ToString(),
            Concepto = 3, // Productos y Servicios (mismo default que la NC total).
            OriginalInvoiceId = originatingInvoice.PublicId.ToString(),
            IsCreditNote = false,
            IsDebitNote = true,
            Items = new List<InvoiceItemDto>
            {
                new()
                {
                    Description = $"Penalidad por cancelacion s/Fc " +
                                  $"{originatingInvoice.PuntoDeVenta:00000}-{originatingInvoice.NumeroComprobante:00000000}.",
                    Quantity = 1,
                    UnitPrice = penaltyAmount,
                    Total = penaltyAmount,
                    AlicuotaIvaId = 3, // 0% / no gravado -> C sin IVA discriminado.
                },
            },
            Tributes = new List<InvoiceTributeDto>(),
            // MVP: solo ARS (el gating ya rechazo no-ARS). MonId/MonCotiz quedan en su
            // default (PES/1), igual que la NC total.
        };

        // Emitir via el pipeline existente (CreateAsync -> CreatePendingInvoice +
        // ProcessInvoiceJob async). Reusamos toda la infra de emision/idempotencia/CAE.
        var debitNoteDto = await _invoiceService.CreateAsync(
            debitNoteRequest, bc.ConfirmedByUserId, bc.ConfirmedByUserName, ct);

        // Resolver el Id (legacy int) de la ND recien creada para vincularla al BC.
        var debitNoteId = await _db.Invoices
            .Where(i => i.PublicId == debitNoteDto.PublicId)
            .Select(i => (int?)i.Id)
            .FirstOrDefaultAsync(ct);

        if (debitNoteId is null)
        {
            _logger.LogError(
                "ADR-013: no se pudo resolver el Id de la ND recien creada para BC {BcPublicId}. " +
                "La ND existe pero quedo sin vincular; rutea a revision manual.",
                bc.PublicId);
            await RouteDebitNoteToManualReviewAsync(bc, "ND creada pero no vinculada.", ct);
            return;
        }

        // (5) Vincular la ND + congelar el snapshot fiscal + marcar Pending. El resultado
        //     final (Issued/Failed) lo reconcilia la bandeja leyendo Invoice.Resultado
        //     (la ND se emite async por ProcessInvoiceJob).
        bc.DebitNoteInvoiceId = debitNoteId.Value;
        bc.DebitNoteStatus = DebitNoteStatus.Pending;
        FreezeDebitNoteSnapshot(bc, originatingInvoice, penaltyAmount);

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationArcaSucceeded,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                debitNoteAction = "debit-note-enqueued",
                debitNoteInvoiceId = debitNoteId.Value,
                penaltyAmount,
                conceptKind = bc.ConceptKind.ToString(),
                debitNoteCbteTipo = bc.DebitNoteCbteTipoAtEvent,
            }),
            userId: bc.ConfirmedByUserId ?? bc.DraftedByUserId,
            userName: bc.ConfirmedByUserName ?? bc.DraftedByUserName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "metric:cancellation_debit_note_enqueued | BcPublicId={BcPublicId} DebitNoteInvoiceId={DebitNoteId} Penalty={Penalty}",
            bc.PublicId, debitNoteId.Value, penaltyAmount);
    }

    /// <summary>
    /// ADR-013 §3.4.1 (P3 gating): decide si el caso califica para emitir la ND automatica.
    /// Devuelve <c>null</c> si TODO se cumple (puede emitir), o un string con el motivo por
    /// el cual va a revision manual. Conservador: ante la duda, devuelve motivo (NO emitir).
    ///
    /// <para>El criterio de "es C" mira el <c>TipoComprobante</c> de la factura ORIGINAL
    /// (11/12 = C), NO la condicion fiscal del emisor (M3, §3.4.1.b): la factura asociada es
    /// la fuente de verdad para la letra de la ND.</para>
    /// </summary>
    /// <summary>
    /// ADR-013 (§3.3 / §3.4.1): true si el concepto clasifica a "ingreso propio de la
    /// agencia" -> emite ND propia. Es la pieza central de la disyuncion anti-doble-cobro:
    /// la usan TANTO el gating de la ND (¿emite?) COMO OperatorRefundService (¿puede cargar
    /// la penalidad como deduction del refund?). Pura, testeable sin DB.
    /// </summary>
    internal static bool ConceptIsAgencyOwnedDebitNote(CancellationConceptKind concept) =>
        concept == CancellationConceptKind.AgencyManagementFee ||
        concept == CancellationConceptKind.AgencyCancellationFee;

    /// <summary>
    /// ADR-013 (2026-06-01): aplica al BC la clasificacion de la penalidad que viene en
    /// el request de Confirm. Es el wiring de captura: traduce lo que el usuario informo
    /// (concepto / estado / finalidad / monto) a los campos del <see cref="BookingCancellation"/>
    /// que el gating de la ND lee mas tarde.
    ///
    /// <para><b>Conservador</b>: si el request no informa concepto, sugiere el default a
    /// partir de <c>Supplier.PenaltyOwnership</c> del operador ("depende del operador",
    /// §3.7). Si el operador retiene la penalidad (pass-through, default), el concepto
    /// queda en pass-through -> NO ND, igual a hoy.</para>
    ///
    /// <para><b>Guardas de seguridad (security review):</b>
    /// <list type="number">
    /// <item><b>Permiso elevado</b>: clasificar como ingreso propio de la agencia
    /// (dispara ND fiscal real) exige <c>cancellations.classify_agency_penalty</c>. Si el
    /// caller no lo tiene, se rechaza (un vendedor comun no dispara una ND).</item>
    /// <item><b>Anti-reclasificacion</b>: no se puede cambiar el concepto cuando la ND ya
    /// esta en juego (<see cref="DebitNoteStatus.Pending"/>/<see cref="DebitNoteStatus.Issued"/>
    /// o ya hay <c>DebitNoteInvoiceId</c>). Cierra la ventana de doble cobro por edicion.</item>
    /// <item><b>Auditoria</b>: setea quien clasifico el concepto y quien confirmo la
    /// penalidad (la decision fiscalmente mas sensible, §3.11).</item>
    /// </list></para>
    /// </summary>
    // internal (no private) para que los tests unit puedan ejercer la captura + las
    // guardas (permiso / anti-reclasificacion / auditoria) sin levantar DB: el metodo
    // solo muta el BC en memoria y loguea, no toca _db. InternalsVisibleTo("TravelApi.Tests").
    internal void CaptureDebitNoteClassification(
        BookingCancellation bc,
        // ADR-014 (M1, refactor de SHAPE): el metodo consume un record neutro
        // (PenaltyClassificationInput) en vez de ConfirmCancellationRequest. Asi el path
        // sincrono (Dia 0) y el diferido (Dia N) reusan EXACTAMENTE la misma logica de
        // captura/guardas. La logica NO cambio: solo la forma de recibir los datos.
        PenaltyClassificationInput classification,
        string userId,
        string? userName,
        bool userCanClassifyAgencyPenalty,
        // B1 (review 2026-06-01): si el flag de la ND esta OFF, este metodo NO debe tocar
        // NINGUN campo ni lanzar excepcion -> ConfirmAsync queda byte-identico a hoy.
        bool debitNoteFeatureEnabled)
    {
        // (B1) Flag OFF -> short-circuit total. No mutamos ConceptKind/PenaltyStatus/
        //      PenaltyAmountAtEvent/DebitNotePurpose ni la auditoria, y NO lanzamos
        //      INV-ADR013-PERM/INV-ADR013-002. El BC se queda con sus defaults
        //      conservadores (pass-through / Estimated) exactamente como en d29ac8a,
        //      asi la disyuncion anti-doble-cobro en OperatorRefundService nunca se
        //      activa (ConceptKind queda pass-through, no agency-owned).
        if (!debitNoteFeatureEnabled)
            return;

        // (0) Resolver el concepto que el usuario quiere aplicar. Distinguimos dos casos
        //     porque afectan la guarda de permiso (B2-back):
        //       - conceptExplicit: el usuario lo informo en el request (decision deliberada).
        //       - requestedConcept: el efectivo, que cae al default por operador
        //         (PenaltyOwnership) si el usuario no informo nada ("depende del operador").
        var conceptExplicit = classification.PenaltyConceptKind;
        var requestedConcept = conceptExplicit
            ?? DefaultConceptFromSupplier(bc.Supplier?.PenaltyOwnership);

        // (B2-back, review 2026-06-01) Guarda de permiso acotada al concepto EXPLICITO.
        //     Clasificar como ingreso propio de la agencia dispara una ND fiscal real ->
        //     exige permiso elevado. Pero SOLO lo exigimos cuando el usuario lo pidio
        //     EXPLICITAMENTE (AgencyManagementFee/AgencyCancellationFee en el request). Si
        //     el concepto agency-owned proviene del DEFAULT derivado del supplier (operador
        //     marcado como Agency) y el usuario no tiene el permiso, NO abortamos el confirm:
        //     degradamos conservador a pass-through (NO ND, igual a hoy). Asi un vendedor sin
        //     permiso puede cancelar sobre un operador Agency sin que el confirm explote; la
        //     ND simplemente no se dispara (queda para quien tenga el permiso).
        if (conceptExplicit.HasValue && ConceptIsAgencyOwnedDebitNote(conceptExplicit.Value) &&
            !userCanClassifyAgencyPenalty)
        {
            throw new BusinessInvariantViolationException(
                "Clasificar la penalidad como ingreso propio de la agencia emite una Nota " +
                "de Debito fiscal y requiere el permiso 'cancellations.classify_agency_penalty'. " +
                "Tu usuario no lo tiene.",
                invariantCode: "INV-ADR013-PERM");
        }

        // Degradacion conservadora: el default por operador sugiere agency-owned pero el
        // usuario no tiene permiso para disparar la ND. No lanzamos: dejamos pass-through.
        if (!conceptExplicit.HasValue && ConceptIsAgencyOwnedDebitNote(requestedConcept) &&
            !userCanClassifyAgencyPenalty)
        {
            _logger.LogInformation(
                "ADR-013 capture: BC {BcPublicId} operador sugiere ND propia (default por " +
                "PenaltyOwnership) pero el usuario {UserId} no tiene permiso. Degrada a " +
                "pass-through (NO ND).", bc.PublicId, userId);
            requestedConcept = CancellationConceptKind.OperatorPenaltyPassThrough;
        }

        // (1) Guarda anti-reclasificacion (B/bloqueante). Si la ND ya esta en juego, el
        //     concepto NO se puede cambiar: hacerlo abriria una ventana de doble cobro
        //     (ej. emitir la ND y despues reclasificar a pass-through + cargar la penalidad
        //     como deduction del refund). Bloqueamos cualquier CAMBIO de concepto en ese
        //     estado. Si el concepto requerido es el MISMO que ya tiene, es un no-op y se
        //     permite (no hay reclasificacion real). Va DESPUES de resolver la degradacion
        //     para no rechazar un confirm que en realidad no cambia el concepto.
        EnsureConceptNotLockedByDebitNote(bc, requestedConcept);

        // (3) Auditoria del clasificador (B/bloqueante, §3.11): registramos quien clasifico
        //     el concepto SOLO si cambia respecto del valor actual (evita pisar el rastro de
        //     una clasificacion previa con un confirm que no toca el concepto).
        if (bc.ConceptKind != requestedConcept)
        {
            bc.ConceptKind = requestedConcept;
            bc.ConceptClassifiedByUserId = userId;
            bc.ConceptClassifiedByUserName = userName;
            bc.ConceptClassifiedAt = DateTime.UtcNow;
        }

        // (4) Finalidad de la ND. Si el usuario no la informo pero clasifico a ND propia,
        //     defaulteamos a PenaltyOrCancellationCharge (el unico caso que el MVP automatiza).
        if (classification.DebitNotePurpose.HasValue)
        {
            bc.DebitNotePurpose = classification.DebitNotePurpose.Value;
        }
        else if (ConceptIsAgencyOwnedDebitNote(requestedConcept) && bc.DebitNotePurpose is null)
        {
            bc.DebitNotePurpose = TravelApi.Domain.Entities.DebitNotePurpose.PenaltyOrCancellationCharge;
        }

        // (5) Estado de la penalidad + monto confirmado. Solo seteamos PenaltyConfirmedBy*
        //     cuando el usuario marca Confirmed (R5: la confirmacion es el acto auditable).
        if (classification.PenaltyStatus.HasValue)
        {
            bc.PenaltyStatus = classification.PenaltyStatus.Value;
        }

        if (classification.ConfirmedPenaltyAmount.HasValue)
        {
            bc.PenaltyAmountAtEvent = classification.ConfirmedPenaltyAmount.Value;
        }

        if (bc.PenaltyStatus == PenaltyStatus.Confirmed)
        {
            // Auditoria de la confirmacion (§3.8, R5): quien y cuando confirmo el monto.
            bc.PenaltyConfirmedByUserId = userId;
            bc.PenaltyConfirmedByUserName = userName;
            bc.PenaltyConfirmedAt = DateTime.UtcNow;
        }

        _logger.LogInformation(
            "ADR-013 capture: BC {BcPublicId} concept={Concept} status={Status} purpose={Purpose} " +
            "amount={Amount} by {UserId}.",
            bc.PublicId, bc.ConceptKind, bc.PenaltyStatus, bc.DebitNotePurpose,
            bc.PenaltyAmountAtEvent, userId);
    }

    /// <summary>
    /// ADR-013 §3.7: traduce el "quien se queda la penalidad" del operador al concepto
    /// por defecto. Operador retiene (default) -> pass-through (NO ND). Operador =
    /// agencia -> cargo de cancelacion propio (candidato a ND, pero el usuario decide el
    /// sub-tipo exacto si quiere). Conservador: ante la ausencia de dato, pass-through.
    /// </summary>
    internal static CancellationConceptKind DefaultConceptFromSupplier(PenaltyOwnership? ownership) =>
        ownership == PenaltyOwnership.Agency
            ? CancellationConceptKind.AgencyCancellationFee
            : CancellationConceptKind.OperatorPenaltyPassThrough;

    /// <summary>
    /// ADR-013 (anti-reclasificacion, B/bloqueante): rechaza CAMBIAR el concepto de la
    /// penalidad cuando la ND ya esta en juego. "En juego" = ya hay una ND vinculada
    /// (<c>DebitNoteInvoiceId</c>) o el estado de la ND es Pending/Issued. Permitir el
    /// cambio en ese momento abriria una ventana de doble cobro (emitir ND + despues
    /// reclasificar a pass-through y netear la penalidad del refund). Si el concepto
    /// requerido es identico al actual, NO es reclasificacion y se permite (no-op).
    /// </summary>
    internal static void EnsureConceptNotLockedByDebitNote(
        BookingCancellation bc, CancellationConceptKind requestedConcept)
    {
        if (requestedConcept == bc.ConceptKind)
            return; // mismo valor: no hay reclasificacion real.

        var debitNoteInPlay =
            bc.DebitNoteInvoiceId.HasValue ||
            bc.DebitNoteStatus == DebitNoteStatus.Pending ||
            bc.DebitNoteStatus == DebitNoteStatus.Issued;

        if (debitNoteInPlay)
        {
            throw new BusinessInvariantViolationException(
                "No se puede reclasificar el concepto de la penalidad: ya hay una Nota de " +
                $"Debito en juego (estado {bc.DebitNoteStatus}). Cambiar el concepto ahora " +
                "podria producir un doble cobro. Anula la ND antes de reclasificar.",
                invariantCode: "INV-ADR013-002");
        }
    }

    // internal (no private) para que los tests unit puedan validar el gating sin DB:
    // el proyecto tiene InternalsVisibleTo("TravelApi.Tests"). Es una funcion pura.
    internal static string? EvaluateDebitNoteGating(BookingCancellation bc, Invoice originatingInvoice)
    {
        // Concepto: solo ingreso propio de la agencia emite ND. Pass-through (default) y
        // seguros -> manual.
        if (!ConceptIsAgencyOwnedDebitNote(bc.ConceptKind))
            return $"Concepto {bc.ConceptKind} no es ingreso propio de la agencia (no emite ND).";

        // El operador NO debe ser pass-through (defensa redundante con el concepto, pero
        // explicita: el operador define el default y el concepto puede haberlo overrideado).
        if (bc.Supplier?.PenaltyOwnership == PenaltyOwnership.Operator)
            return "El operador retiene la penalidad (pass-through): la agencia no emite ND.";

        // Penalidad confirmada por el operador (R5): no se emite sobre estimada.
        if (bc.PenaltyStatus != PenaltyStatus.Confirmed)
            return "La penalidad no esta confirmada (Estimated): no se emite ND sobre un estimado.";

        // (B3, review 2026-06-01) Auditoria como INVARIANTE del gating, no como convencion.
        //   La ND es la decision fiscalmente mas sensible: exigimos rastro de QUIEN clasifico
        //   el concepto y QUIEN confirmo el monto. Hay caminos donde estos quedan NULL y la
        //   ND seria igual emitible (ej. un BC ya creado con ConceptKind=AgencyCancellationFee
        //   + un Confirm que lo deja igual: el `if (bc.ConceptKind != requested)` de la captura
        //   es falso y nunca setea ConceptClassifiedByUserId). Sin clasificador/confirmador
        //   conocido, NO emitimos: a revision manual. Asi la auditoria es obligatoria por
        //   construccion, no por confianza en el orden de las mutaciones.
        if (bc.ConceptClassifiedByUserId is null)
            return "Falta el rastro de quien clasifico el concepto de la penalidad (auditoria): revision manual.";
        if (bc.PenaltyConfirmedByUserId is null)
            return "Falta el rastro de quien confirmo la penalidad (auditoria): revision manual.";

        // Finalidad: el MVP solo automatiza PenaltyOrCancellationCharge.
        if (bc.DebitNotePurpose != TravelApi.Domain.Entities.DebitNotePurpose.PenaltyOrCancellationCharge)
            return $"DebitNotePurpose {bc.DebitNotePurpose?.ToString() ?? "(null)"} no se automatiza en el MVP.";

        // Factura original C (11/12). A=1/2, B=6/7, M=51/52 -> manual (M3).
        if (originatingInvoice.TipoComprobante is not (11 or 12))
            return $"Factura original tipo {originatingInvoice.TipoComprobante} no es C (11/12): revision manual.";

        // Moneda ARS (la factura original en pesos). MonId "PES" o vacio = pesos.
        var isArs = string.IsNullOrWhiteSpace(originatingInvoice.MonId) ||
                    string.Equals(originatingInvoice.MonId, "PES", StringComparison.OrdinalIgnoreCase);
        if (!isArs)
            return $"Factura original en moneda {originatingInvoice.MonId} (no ARS): revision manual.";

        // La factura con tributos provinciales (IIBB/percepciones) -> manual (R6).
        if (originatingInvoice.Tributes is { Count: > 0 })
            return "La factura original tiene tributos provinciales: revision manual.";

        // Monto de la penalidad: debe estar seteado, ser > 0 y NO superar la factura (M2).
        if (!bc.PenaltyAmountAtEvent.HasValue || bc.PenaltyAmountAtEvent.Value <= 0m)
            return "No hay monto de penalidad confirmado (> 0).";
        if (bc.PenaltyAmountAtEvent.Value > originatingInvoice.ImporteTotal)
            return $"La penalidad ({bc.PenaltyAmountAtEvent.Value}) supera el total de la factura " +
                   $"({originatingInvoice.ImporteTotal}): revision manual (M2).";

        return null; // Pasa todo el gating: puede emitir.
    }

    /// <summary>
    /// ADR-013 §3.8/§3.11: congela el snapshot fiscal de la ND al momento del evento. Sirve
    /// para auditoria: prueba con que reglas (monto, moneda, tipo de comprobante, condicion
    /// fiscal, quien confirmo/clasifico) se emitio. El tipo de la ND se deriva del
    /// <c>TipoComprobante</c> de la factura original (M3), no de la condicion fiscal.
    /// </summary>
    private static void FreezeDebitNoteSnapshot(
        BookingCancellation bc, Invoice originatingInvoice, decimal penaltyAmount)
    {
        bc.PenaltyAmountAtEvent = penaltyAmount;
        bc.PenaltyCurrencyAtEvent = string.IsNullOrWhiteSpace(originatingInvoice.MonId)
            ? "ARS"
            : (string.Equals(originatingInvoice.MonId, "PES", StringComparison.OrdinalIgnoreCase) ? "ARS" : originatingInvoice.MonId);
        bc.OriginalInvoiceCbteTipoAtEvent = originatingInvoice.TipoComprobante;
        // ND C = 12 (derivado del tipo de la factura original via el helper, M1/M3).
        bc.DebitNoteCbteTipoAtEvent =
            InvoiceComprobanteHelpers.GetDebitNoteTypeForAssociated(originatingInvoice.TipoComprobante);
        bc.EmitterTaxConditionAtEvent ??= bc.FiscalSnapshot?.AgencyTaxConditionAtEvent;
        bc.PenaltyOwnershipAtEvent ??= bc.Supplier?.PenaltyOwnership;
    }

    /// <summary>
    /// ADR-013 §3.10 (M4): marca la ND como pendiente de revision manual sin emitirla.
    /// Hace observable el caso (la bandeja lo levanta) y deja el motivo persistido.
    /// </summary>
    private async Task RouteDebitNoteToManualReviewAsync(
        BookingCancellation bc, string reason, CancellationToken ct)
    {
        bc.DebitNoteStatus = DebitNoteStatus.ManualReview;
        bc.DebitNoteArcaErrorMessage = reason.Length > 1000 ? reason[..1000] : reason;

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationArcaSucceeded,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                debitNoteAction = "debit-note-manual-review",
                reason,
                conceptKind = bc.ConceptKind.ToString(),
            }),
            userId: bc.ConfirmedByUserId ?? bc.DraftedByUserId,
            userName: bc.ConfirmedByUserName ?? bc.DraftedByUserName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "metric:cancellation_debit_note_manual_review | BcPublicId={BcPublicId} Reason={Reason}",
            bc.PublicId, reason);
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
        //    F2.3: agregamos Include(OriginatingInvoice) + Include(Customer) porque el path
        //    Fase 2 (NC parcial real) los necesita para armar las Lines del request al
        //    InvoiceService y para renderizar la descripcion template. Sin Include el
        //    template renderiza con valores default ("?") y la URL del InvoiceService
        //    explota al armar el XML.
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.OriginatingInvoice)
            .Include(b => b.Customer)
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

        // 7) Emision de la NC. Hay DOS caminos posibles segun el flag de Fase 2:
        //
        //  a) Flag Fase 2 ON (settings.EnablePartialCreditNoteRealEmission=true)
        //     + kind PartialOnOriginal: Fase 2 emite NC PARCIAL REAL contra ARCA
        //     usando InvoiceService.EnqueuePartialCreditNoteAsync con las lineas
        //     reducidas que arma F2.3.
        //
        //  b) Flag Fase 2 OFF (default) + kind PartialOnOriginal: fallback FC1.2.
        //     El AfipService emite NC TOTAL (no parcial) — comportamiento Fase 1
        //     intacto + log warning. Mantenemos este path byte-identico para que
        //     no haya regresion mientras el flag no este prendido en prod.
        //
        // F2.3 (plan tactico Fase 2 §FC1.3.F2.3, 2026-05-28): este bloque es la
        // unica diferencia funcional de F2.3 vs F2.2 — todo el resto del flow
        // (transicion BC, OperatorRefundDueBy, ApprovalConsumed) queda igual.
        if (bc.CreditNoteKind == CreditNoteKind.PartialOnOriginal)
        {
            if (settings.EnablePartialCreditNoteRealEmission)
            {
                // ===== PATH FASE 2: NC PARCIAL REAL CONTRA ARCA =====
                await EmitRealPartialCreditNoteAsync(
                    bc, settings, resolverUserId, resolverUserName, resolverNotes,
                    approvalRequestId, ct);
            }
            else
            {
                // ===== PATH FASE 1 (fallback FC1.2): NC TOTAL REAL =====
                // Mantenemos el log warning historico — sirve a operaciones para
                // detectar BCs que cayeron al fallback aunque Fase 2 ya este
                // mergeada (caso: olvido prender el flag, rollback de Fase 2).
                _logger.LogWarning(
                    "FC1.3 Fase 1: BC {BcPublicId} aprobado con CreditNoteKind=PartialOnOriginal pero " +
                    "AfipService emite NC TOTAL real (no parcial). Fase 2 implementa parcial real. " +
                    "Razon FC1.3: {Reason}. Monto facturado: {Total}.",
                    bc.PublicId, bc.ReviewRequiredReason, bc.OriginatingInvoice?.ImporteTotal);

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

        // B2 fix (FC1.3 Fase 2, RH-002): limpiar TAMBIEN el owned VO FiscalLiquidation.
        // Antes el reset limpiaba LiquidationComputedAt (columna summary) pero NO seteaba
        // bc.FiscalLiquidation = null, dejando las columnas FiscalLiquidation_* pobladas
        // (con FiscalLiquidation_ComputedAt no-null) mientras LiquidationComputedAt
        // quedaba null. El CHECK de consistencia NO atrapa esa combinacion (compara
        // null = timestamp => UNKNOWN => pasa), asi que quedaba un BC en Drafted con una
        // "liquidacion fantasma" visible en reportes. Al volver a Drafted la liquidacion
        // ya no aplica: la fuente de verdad para reprocesar es el Metadata JSON del
        // approval (que persiste para auditoria), no estas columnas.
        bc.FiscalLiquidation = null;
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
        // FC1.3 Fase 2 (RH-002): el computedAt del JSON usa el MISMO valor que la
        // columna summary bc.LiquidationComputedAt (seteado en ConfirmAsync paso f),
        // no un DateTime.UtcNow nuevo. Asi las dos representaciones del doble-write
        // (JSON + columnas FiscalLiquidation_*) quedan coherentes en el timestamp.
        // Fallback defensivo a UtcNow solo si por algun bug llegara null (no deberia:
        // ConfirmAsync siempre lo setea antes de invocar este metodo).
        var metadata = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["computedAt"] = bc.LiquidationComputedAt ?? DateTime.UtcNow,
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
        // FC1.3 Fase 3 (ADR-010 R1): la regla GR-005 ahora vive en el servicio
        // compartido IFourEyesBypassEvaluator (mismos chequeos, mismo orden, mismos
        // umbrales). Este metodo se mantiene como punto de entrada de los call-sites
        // existentes (EditLiquidation + OnApproved) para no cambiar su flujo, pero la
        // evaluacion es la misma que usa la bandeja de reconciliacion.
        return await _fourEyesBypassEvaluator.EvaluateAsync(comment, settings, ct);
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
    /// FC1.3 Fase 2 (RH-002): arma el owned VO <c>FiscalLiquidation</c> a partir del
    /// resultado del calculator. Centraliza el doble-write para que ConfirmAsync y
    /// EditLiquidationAsync construyan el VO de la misma forma (un solo lugar que
    /// mapea DTO -> VO).
    ///
    /// <para><b>computedAt es parametro, no DateTime.UtcNow interno</b>: el caller
    /// pasa el MISMO timestamp que ya escribio en <c>bc.LiquidationComputedAt</c>.
    /// El CHECK <c>chk_BookingCancellations_fiscalliquidation_consistency</c> exige
    /// igualdad exacta entre el VO y esa columna; generar un timestamp aca propio
    /// rebotaria el INSERT.</para>
    /// </summary>
    /// <summary>
    /// FC1.3 Fase 2 (B-FISC-1, decision Gaston opcion A): indica si la liquidacion
    /// corresponde al modo CommissionOnly (operador intermediario), en cuyo caso NO
    /// se persiste el owned VO <see cref="FiscalLiquidation"/> (queda null).
    ///
    /// <para><b>Por que el discriminador es el flag y no el Case</b>: el calculator
    /// hace early-exit en STEP 0 cuando el modo es CommissionOnly (GR-003) y devuelve
    /// SIEMPRE <c>ReviewRequiredReason.InvoicingModeCommissionOnly</c> como UNICO motivo
    /// (el early-exit corre ANTES de evaluar Factura A, items no reintegrables, etc.,
    /// por eso nunca se combina con otros flags). Ese flag es el marcador 1:1 del modo
    /// CommissionOnly en el DTO. Los Cases 5/6 acompanan pero el flag es lo canonico:
    /// se persiste como int en la columna ReviewRequiredReason y permite query directa.</para>
    ///
    /// <para><b>Por que importa</b>: en CommissionOnly el calculator devuelve
    /// FiscalAmountToCredit=0 + NonRefundable=0 + Penalty=penalty con Original>0. Esa
    /// terna viola el CHECK de suma. Dejar el VO null evita el rebote de Postgres y es
    /// fiscalmente correcto (en intermediario no hay total a descomponer).</para>
    /// </summary>
    private static bool IsCommissionOnlyLiquidation(FiscalLiquidationDto liquidation)
        => liquidation.ReviewRequiredReason.HasFlag(ReviewRequiredReason.InvoicingModeCommissionOnly);

    private static FiscalLiquidation BuildFiscalLiquidationVo(
        FiscalLiquidationDto liquidation,
        DateTime computedAt,
        string userId,
        string? userName)
    {
        return new FiscalLiquidation
        {
            OriginalInvoiceAmount = liquidation.OriginalInvoiceAmount,
            CancellationAmount = liquidation.CancellationAmount,
            OperatorPenaltyAmount = liquidation.OperatorPenaltyAmount,
            NonRefundableItemsAmount = liquidation.NonRefundableItemsAmount,
            FiscalAmountToCredit = liquidation.FiscalAmountToCredit,
            AmountToRefundCustomer = liquidation.AmountToRefundCustomer,
            FinalNetInvoiced = liquidation.FinalNetInvoiced,
            Currency = liquidation.Currency,
            ComputedAt = computedAt,
            ComputedByUserId = userId,
            ComputedByUserName = userName,
        };
    }

    // =========================================================================
    // FC1.3.F2.3 (plan tactico Fase 2 §FC1.3.F2.3, 2026-05-28): helpers para el
    // path Fase 2 (NC parcial REAL contra ARCA).
    // =========================================================================

    /// <summary>
    /// F2.3 path Fase 2: emite la NC parcial real llamando al InvoiceService nuevo.
    /// Se ejecuta solo cuando <c>settings.EnablePartialCreditNoteRealEmission=true</c>
    /// y el kind es <c>PartialOnOriginal</c>. Caso contrario el caller usa el fallback
    /// FC1.2 (NC total via EnqueueAnnulmentAsync).
    ///
    /// <para><b>Defense in depth</b>: antes de armar las lineas y llamar al
    /// InvoiceService, re-validamos INV-FC1.3-005 sobre el VO persistido. Si la suma
    /// quedo rota (concurrent edit malicioso entre el approval y este callback), abortamos
    /// emision + log critical. El CHECK SQL ya bloquea esto a nivel BD, pero esta
    /// validacion en C# da un mensaje de error mas claro.</para>
    /// </summary>
    private async Task EmitRealPartialCreditNoteAsync(
        BookingCancellation bc,
        OperationalFinanceSettings settings,
        string resolverUserId,
        string? resolverUserName,
        string? resolverNotes,
        int approvalRequestId,
        CancellationToken ct)
    {
        // 1) Validar precondiciones: FiscalLiquidation debe estar persistido (F2.1).
        //    Sin VO no podemos saber cuanto creditar. Esto NO deberia pasar en Fase 2
        //    (ConfirmAsync siempre lo setea para PartialOnOriginal), pero defendemos
        //    porque si llega null algo se rompio antes y mejor explotar aca que mandar
        //    una NC con monto 0 al ARCA.
        if (bc.FiscalLiquidation is null)
        {
            _logger.LogCritical(
                "F2.3 ABORT: BC {BcPublicId} sin FiscalLiquidation persistido. " +
                "No se puede emitir NC parcial real. Approval {ApprovalRequestId} queda Approved sin efecto.",
                bc.PublicId, approvalRequestId);
            throw new InvalidOperationException(
                $"BC {bc.PublicId} no tiene FiscalLiquidation persistida — Fase 2 requiere doble-write (F2.1).");
        }

        // 2) Defense in depth: re-validar INV-FC1.3-005 (suma cuadra) sobre el VO.
        //    El CHECK SQL chk_BookingCancellations_fiscalliquidation_sum hace lo mismo
        //    a nivel BD con tolerancia 0.01. Aca duplicamos la validacion para emitir
        //    un audit log explicito + mensaje claro si la suma divergio (ej. UPDATE
        //    raw que bypassea EF).
        var fl = bc.FiscalLiquidation;
        var sumComponents = fl.FiscalAmountToCredit + fl.NonRefundableItemsAmount + fl.OperatorPenaltyAmount;
        var sumDiff = Math.Abs(sumComponents - fl.OriginalInvoiceAmount);
        if (sumDiff > 0.01m)
        {
            _logger.LogCritical(
                "F2.3 ABORT: BC {BcPublicId} INV-FC1.3-005 violado en runtime. " +
                "FiscalAmountToCredit ({Fiscal}) + NonRefundableItemsAmount ({Nr}) + " +
                "OperatorPenaltyAmount ({Penalty}) = {Sum}, esperado OriginalInvoiceAmount={Original}. " +
                "Diff={Diff}. Probable concurrent edit malicioso.",
                bc.PublicId, fl.FiscalAmountToCredit, fl.NonRefundableItemsAmount,
                fl.OperatorPenaltyAmount, sumComponents, fl.OriginalInvoiceAmount, sumDiff);

            await _auditService.LogBusinessEventAsync(
                // Nombre acortado <=50 chars (columna AuditLogs.Action es varchar(50)).
                action: "PartialNcEmissionAborted_SumMismatch",
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    bc.PublicId,
                    approvalRequestId,
                    fl.FiscalAmountToCredit,
                    fl.NonRefundableItemsAmount,
                    fl.OperatorPenaltyAmount,
                    sumComponents,
                    fl.OriginalInvoiceAmount,
                    diff = sumDiff,
                }),
                userId: resolverUserId,
                userName: resolverUserName,
                ct: ct);

            throw new BusinessInvariantViolationException(
                $"BC {bc.PublicId}: la suma de la liquidacion fiscal no cuadra con el monto original. " +
                "No se emite NC parcial. Intervencion manual requerida.",
                invariantCode: "INV-FC1.3-005");
        }

        // 2.bis) FC1.3.F2.5 (multimoneda, 2026-05-28): GUARD DE MONEDA SOPORTADA.
        //
        // Historia: en F2.3 este guard rechazaba "todo lo que no sea ARS" porque el XML SOAP al
        // ARCA estaba hardcoded en MonId=PES/MonCotiz=1. Eso ya se resolvio en F2.5: el envelope
        // ahora interpola la moneda y cotizacion reales (ver AfipService.ProcessInvoiceJob +
        // InvoiceService.EmitPartialCreditNoteAsync). Por eso el guard cambio de forma.
        //
        // Que valida AHORA: que la moneda del snapshot este en el catalogo de monedas que el
        // sistema sabe mapear a un codigo ARCA (ARS->PES, USD->DOL). USD pasa y fluye a la
        // emision; EUR/BRL/etc. (que todavia no homologamos) abortan ACA, temprano.
        //
        // Por que rechazar temprano (y no dejar que falle adentro del job de emision): el job
        // del InvoiceService tambien valida la moneda (misma fuente de verdad, ArcaCurrencyMapper)
        // y la marca Failed si no la soporta. Pero si abortamos antes de transicionar el estado
        // del BC, el BC queda en ManualReviewApproved (tratamiento manual) en vez de viajar a
        // AwaitingFiscalConfirmation y morir en background con una NC Failed. Mejor UX operativa:
        // el operador ve el rechazo en el acto, no tiene que ir a buscar una NC fallida.
        //
        // FUENTE DE VERDAD UNICA: tanto este guard como InvoiceService usan ArcaCurrencyMapper.
        // Sumar una moneda nueva (ej. EUR) es una linea en el helper + homologacion ARCA; ningun
        // codigo de aca hay que tocar.
        // FIX m-1 (revision 2026-05-28): UNA sola variable de moneda para todo el metodo.
        // Antes el guard usaba (CurrencyAtEvent ?? "ARS") y el input mas abajo usaba
        // (CurrencyAtEvent ?? fl.Currency). Si CurrencyAtEvent era null, el guard validaba "ARS"
        // pero el input emitia con fl.Currency — podian divergir. Unificamos en currency para que
        // lo que validamos sea EXACTAMENTE lo que emitimos.
        var currency = bc.FiscalSnapshot?.CurrencyAtEvent ?? fl.Currency;
        if (!ArcaCurrencyMapper.IsSupported(currency))
        {
            _logger.LogCritical(
                "F2.5 ABORT - currency {Currency} no soportada por el mapeo ARCA. bcId={BcId}, invoiceId={InvoiceId}",
                currency, bc.Id, bc.OriginatingInvoiceId);

            await _auditService.LogBusinessEventAsync(
                // Nombre acortado <=50 chars (columna AuditLogs.Action es varchar(50)).
                // "PartialNcAborted_UnsupportedCurrency" = 36 chars.
                action: "PartialNcAborted_UnsupportedCurrency",
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    bcPublicId = bc.PublicId,
                    originalInvoiceId = bc.OriginatingInvoiceId,
                    currency,
                    reason = "Moneda no soportada por el mapeo ARCA (solo ARS y USD por ahora). " +
                             "Agregar la moneda al ArcaCurrencyMapper + homologar ARCA antes de operarla.",
                }),
                userId: resolverUserId,
                userName: resolverUserName,
                ct: ct);

            // FC1.3.F2.6 (counter): abortamos la emision por guard fiscal. El tag reason
            // distingue los 3 motivos de aborto multimoneda (F2.5) para poder alertar por
            // separado. Este caso: moneda fuera del catalogo ARCA soportado.
            _logger.LogInformation(
                "metric:Fc13.PartialCreditNote.AbortedFiscalGuard | bcPublicId={BcPublicId} originalInvoiceId={OriginalInvoiceId} reason=UnsupportedCurrency currency={Currency}",
                bc.PublicId, bc.OriginatingInvoiceId, currency);

            throw new BusinessInvariantViolationException(
                $"NC parcial real no soportada para moneda {currency}: no esta en el " +
                $"mapeo de monedas ARCA (solo ARS y USD por ahora). BookingCancellation {bc.PublicId} " +
                $"queda en ManualReviewApproved sin emision (tratamiento manual).");
        }

        // 2.ter) FC1.3.F2.5 (fix M-1, revision 2026-05-28): GUARD DE COTIZACION COHERENTE,
        //        a la par del guard de moneda soportada y ANTES de transicionar el BC.
        //
        // Si la moneda es extranjera (no ARS), el tipo de cambio del snapshot tiene que ser un
        // valor real (> 0 y != 1). Un TC en 0 (snapshot no poblado / dato por SQL crudo / backfill)
        // o en 1 (incoherente: un dolar no vale un peso) significaria emitir una NC en DOL valuada
        // como pesos. Frenamos ACA, temprano, asi el BC queda en ManualReviewApproved (tratamiento
        // manual) en vez de viajar a AwaitingFiscalConfirmation y morir en background con una NC
        // Failed. El InvoiceService tiene el mismo guard como ultima linea de defensa.
        bool isForeignCurrency = !string.Equals(currency, "ARS", StringComparison.OrdinalIgnoreCase);
        decimal snapshotExchangeRate = bc.FiscalSnapshot?.ExchangeRateAtOriginalInvoice ?? 0m;
        if (isForeignCurrency && (snapshotExchangeRate <= 0m || snapshotExchangeRate == 1m))
        {
            _logger.LogCritical(
                "F2.5 ABORT - moneda extranjera {Currency} con cotizacion incoherente {Rate} (<= 0 o = 1). " +
                "bcId={BcId}, invoiceId={InvoiceId}",
                currency, snapshotExchangeRate, bc.Id, bc.OriginatingInvoiceId);

            await _auditService.LogBusinessEventAsync(
                // Nombre acortado <=50 chars (columna AuditLogs.Action es varchar(50)).
                action: "PartialNcAborted_IncoherentRate",
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    bcPublicId = bc.PublicId,
                    originalInvoiceId = bc.OriginatingInvoiceId,
                    currency,
                    exchangeRate = snapshotExchangeRate,
                    reason = "Moneda extranjera con tipo de cambio incoherente (0 o 1). No se puede valuar " +
                             "un dolar como un peso. Revisar el snapshot fiscal de la factura origen.",
                }),
                userId: resolverUserId,
                userName: resolverUserName,
                ct: ct);

            // FC1.3.F2.6 (counter): aborto por guard fiscal, caso cotizacion incoherente
            // (moneda extranjera con TC <= 0 o = 1 -> valuaria un dolar como un peso).
            _logger.LogInformation(
                "metric:Fc13.PartialCreditNote.AbortedFiscalGuard | bcPublicId={BcPublicId} originalInvoiceId={OriginalInvoiceId} reason=IncoherentExchangeRate currency={Currency} exchangeRate={Rate}",
                bc.PublicId, bc.OriginatingInvoiceId, currency, snapshotExchangeRate);

            throw new BusinessInvariantViolationException(
                $"NC parcial real abortada para moneda {currency}: el tipo de cambio del snapshot es " +
                $"{snapshotExchangeRate} (incoherente para moneda extranjera). BookingCancellation {bc.PublicId} " +
                $"queda en ManualReviewApproved sin emision (tratamiento manual).");
        }

        // 2.quater) FC1.3.F2.5 (GAP-1, defense-in-depth, revision 2026-05-28):
        //           GUARD DE MONEDA NC == MONEDA DEL COMPROBANTE ORIGEN.
        //
        // Que compara: el codigo ARCA con el que VAMOS a emitir la NC (derivado del snapshot,
        // 'currency' -> ArcaCurrencyMapper) contra el codigo ARCA REAL con el que la factura madre
        // quedo registrada en ARCA (bc.OriginatingInvoice.MonId, "PES" o "DOL").
        //
        // POR QUE EXISTE (el caso legacy): una factura en dolares emitida ANTES de F2.5 — cuando
        // todo el sistema registraba en pesos — tiene OriginatingInvoice.MonId = "PES" aunque su
        // snapshot fiscal diga CurrencyAtEvent = "USD". Sin este guard, con el flag prendido, el
        // emisor armaria una NC en DOL asociada (via <CbtesAsoc>) a una factura que ARCA tiene
        // registrada en PES: la nota de credito NO coincide en moneda con su comprobante origen.
        // Eso es un desfasaje fiscal NC != origen que ninguna otra capa detecta hoy.
        //
        // POR QUE NO ROMPE EL CASO FELIZ: una factura USD emitida CORRECTAMENTE post-F2.5 tiene
        // OriginatingInvoice.MonId = "DOL" y el snapshot "USD" -> ArcaCurrencyMapper -> "DOL".
        // Coinciden -> el guard no dispara -> emite normal. Idem ARS (PES == PES). El guard SOLO
        // frena el caso incoherente (snapshot dice una moneda, la factura madre quedo en otra).
        //
        // POR QUE ABORTAR A MANUAL (y no auto-corregir): no podemos asumir cual de los dos datos es
        // el correcto. Una factura USD legacy en PES quizas haya que reemitirla, o el snapshot esta
        // mal poblado. Es una decision fiscal humana — dejamos el BC en ManualReviewApproved (su
        // estado actual, sin transicionar) para que un operador lo resuelva.
        //
        // 'currency' ya paso el guard de moneda soportada, asi que TryMap nunca devuelve null aca;
        // igual usamos el resultado de TryMap para comparar EXACTAMENTE el codigo que emitiriamos.
        var originatingInvoice = bc.OriginatingInvoice;
        if (originatingInvoice is null)
        {
            // Defensive: el path desde OnApprovedAsync incluye OriginatingInvoice (Include en la
            // query). Si llega null, algo cambio en el path de carga — explotamos antes de emitir
            // una NC sin poder validar la moneda del origen.
            throw new InvalidOperationException(
                $"BC {bc.PublicId}: OriginatingInvoice no esta cargado. No se puede validar la " +
                "moneda de la factura origen antes de emitir NC parcial.");
        }

        var ncArcaCurrencyCode = ArcaCurrencyMapper.TryMap(currency);
        var originInvoiceArcaCurrencyCode = originatingInvoice.MonId;
        if (!string.Equals(ncArcaCurrencyCode, originInvoiceArcaCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogCritical(
                "F2.5 ABORT - NC parcial en {NcMonId} pero factura origen {InvoicePublicId} registrada en " +
                "ARCA como {OriginMonId}; no se emite para evitar desfasaje NC != origen (probable factura " +
                "USD legacy pre-F2.5). bcId={BcId}, invoiceId={InvoiceId}.",
                ncArcaCurrencyCode, originatingInvoice.PublicId, originInvoiceArcaCurrencyCode,
                bc.Id, bc.OriginatingInvoiceId);

            await _auditService.LogBusinessEventAsync(
                // Nombre acortado <=50 chars (columna AuditLogs.Action es varchar(50)).
                // "PartialNcAborted_CurrencyMismatchVsOrigin" = 41 chars.
                action: "PartialNcAborted_CurrencyMismatchVsOrigin",
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    bcPublicId = bc.PublicId,
                    originalInvoiceId = bc.OriginatingInvoiceId,
                    originalInvoicePublicId = originatingInvoice.PublicId,
                    snapshotCurrency = currency,
                    ncArcaCurrencyCode,
                    originInvoiceArcaCurrencyCode,
                    reason = "La moneda ARCA de la NC parcial no coincide con la moneda ARCA registrada " +
                             "en la factura origen. Probable factura USD legacy pre-F2.5 (snapshot USD pero " +
                             "factura madre registrada en PES). No se emite para evitar desfasaje NC != origen.",
                }),
                userId: resolverUserId,
                userName: resolverUserName,
                ct: ct);

            // FC1.3.F2.6 (counter): aborto por guard fiscal, caso moneda NC != moneda de la
            // factura origen registrada en ARCA (probable factura USD legacy pre-F2.5).
            _logger.LogInformation(
                "metric:Fc13.PartialCreditNote.AbortedFiscalGuard | bcPublicId={BcPublicId} originalInvoiceId={OriginalInvoiceId} reason=CurrencyMismatchVsOrigin ncArcaCurrency={NcArcaCurrency} originArcaCurrency={OriginArcaCurrency}",
                bc.PublicId, bc.OriginatingInvoiceId, ncArcaCurrencyCode, originInvoiceArcaCurrencyCode);

            throw new BusinessInvariantViolationException(
                $"NC parcial real abortada: la moneda ARCA de la NC ({ncArcaCurrencyCode}) no coincide con " +
                $"la de la factura origen ({originInvoiceArcaCurrencyCode}). BookingCancellation {bc.PublicId} " +
                "queda en ManualReviewApproved sin emision (tratamiento manual). " +
                "Probable factura USD legacy pre-F2.5.");
        }

        // 3) Cargar items de la factura origen para construir Lines.
        //    NOTA: bc.OriginatingInvoice ya esta Included (path desde OnApprovedAsync),
        //    pero los Items hay que cargarlos por separado (no se include cascada).
        var invoiceItems = await _db.Set<InvoiceItem>()
            .Where(i => i.InvoiceId == bc.OriginatingInvoiceId)
            .ToListAsync(ct);

        if (invoiceItems.Count == 0)
        {
            // Factura sin items — caso degenerado. NO podemos armar lineas para la NC.
            // Mas allá del CHECK de BD, el InvoiceService igual rechazaria con XML invalido.
            // Mejor abortar aca con mensaje claro.
            throw new InvalidOperationException(
                $"BC {bc.PublicId}: factura origen {bc.OriginatingInvoiceId} no tiene items. " +
                "No se puede emitir NC parcial.");
        }

        // 4) Construir las Lines (corazon del cambio F2.3).
        var lines = BuildPartialCreditNoteLines(bc, invoiceItems, settings);

        // 5) Armar el input para el InvoiceService.
        // 'originatingInvoice' ya fue resuelto y validado arriba (guard GAP-1: moneda NC == origen).
        var originalInvoice = originatingInvoice;
        // 'currency' ya fue resuelta y validada arriba (guard de moneda soportada + guard de
        // cotizacion coherente). No la re-declaramos: lo que validamos es lo que emitimos (fix m-1).
        //
        // Tipo de cambio (fix M-1): para pesos vale 1. Para moneda extranjera ya garantizamos
        // arriba que snapshotExchangeRate es > 0 y != 1 (sino abortamos terminal), asi que aca lo
        // usamos directo SIN el viejo fallback "?? 1m" — ese fallback era justamente el bug que
        // valuaba un dolar como un peso cuando el snapshot venia en 0.
        var exchangeRate = string.Equals(currency, "ARS", StringComparison.OrdinalIgnoreCase)
            ? 1m
            : snapshotExchangeRate;

        var emissionInput = new PartialCreditNoteEmissionInput(
            OriginalNetAmount: originalInvoice.ImporteNeto,
            OriginalVatAmount: originalInvoice.ImporteIva,
            OriginalTotalAmount: originalInvoice.ImporteTotal,
            FiscalAmountToCredit: fl.FiscalAmountToCredit,
            Currency: currency,
            ExchangeRateAtOriginalInvoice: exchangeRate,
            Lines: lines);

        // 6) Transicionar BC + Reserva ANTES de encolar (asi el job encuentra el BC
        //    en el estado esperado cuando arranque).
        bc.Status = BookingCancellationStatus.AwaitingFiscalConfirmation;
        bc.ConfirmedWithClientAt ??= DateTime.UtcNow;
        bc.ConfirmedByUserId ??= resolverUserId;
        bc.ConfirmedByUserName ??= resolverUserName;
        bc.OperatorRefundDueBy ??= DateTime.UtcNow.AddDays(settings.OperatorRefundTimeoutDays);
        bc.Reserva.Status = EstadoReserva.PendingOperatorRefund;

        await _db.SaveChangesAsync(ct);

        // 7) Encolar la NC parcial real contra ARCA (job Hangfire F2.2).
        await _invoiceService.EnqueuePartialCreditNoteAsync(
            originalInvoiceId: bc.OriginatingInvoiceId,
            liquidation: emissionInput,
            userId: resolverUserId,
            userName: resolverUserName,
            reason: $"FC1.3 F2 partial NC: {resolverNotes?.Trim()}",
            approvalRequestId: approvalRequestId,
            ct: ct);

        // 8) Marcar el approval como Consumed para que no se reuse.
        await _approvalService.MarkConsumedAsync(approvalRequestId, ct);

        _logger.LogInformation(
            "FC1.3 F2.3: BC {BcPublicId} emitio NC parcial real (encolada). " +
            "FiscalAmountToCredit={Amount} {Currency}, lines={LineCount}.",
            bc.PublicId, fl.FiscalAmountToCredit, currency, lines.Count);
    }

    /// <summary>
    /// F2.3 — construye las lineas de la NC parcial a partir de la factura origen y
    /// la liquidacion fiscal persistida.
    ///
    /// <para><b>Casos</b> (cubren los 3 escenarios mas comunes del plan F2.3 punto 2):
    /// <list type="number">
    ///   <item><b>Hay items no reintegrables</b> (flag <c>HasNonRefundableItems</c>):
    ///   excluimos esos items y prorrateamos el resto por factor de escala
    ///   <c>FiscalAmountToCredit / SUM(refundable_items.Total)</c>. Cada item refundable
    ///   sale como linea propia con su <c>AlicuotaIvaId</c> original. Esto preserva la
    ///   alicuota por item (mas fiel fiscalmente que colapsar).</item>
    ///   <item><b>No hay items no reintegrables + factura multi-alicuotas</b>:
    ///   default (RH-001/OQ-2) reproducir TODAS las alicuotas con prorrateo proporcional
    ///   al total por alicuota — preserva fidelidad fiscal. Una linea por alicuota.</item>
    ///   <item><b>Factura mono-alicuota</b>: una unica linea con
    ///   <c>Total = FiscalAmountToCredit</c> + alicuota dominante de la factura origen
    ///   + <c>Description</c> renderizada desde <see cref="OperationalFinanceSettings.PartialNcDescriptionTemplate"/>.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>CRITICO (decision Gaston 2026-05-28)</b>: el <c>Total</c> de cada
    /// <see cref="PartialCreditNoteLineDto"/> es BRUTO (con IVA incluido), igual que el
    /// <c>InvoiceItem.Total</c> original. El calculator del InvoiceService extrae el IVA
    /// por dentro al armar el XML para ARCA. NO restar IVA aca.</para>
    /// </summary>
    // Visibilidad internal (no private) para que TravelApi.Tests pueda candar el invariante
    // de absorcion de residuo (Σ line.Total == FiscalAmountToCredit EXACTO) sin armar todo el
    // escenario de BookingCancellation. InternalsVisibleTo ya esta configurado en el csproj
    // (mismo patron que GetDominantAlicuotaId).
    internal static IReadOnlyList<PartialCreditNoteLineDto> BuildPartialCreditNoteLines(
        BookingCancellation bc,
        IReadOnlyList<InvoiceItem> invoiceItems,
        OperationalFinanceSettings settings)
    {
        var fl = bc.FiscalLiquidation!; // ya validado no-null en el caller
        var fiscalAmountToCredit = fl.FiscalAmountToCredit;

        // Caso 1: hay items no reintegrables. Excluirlos y prorratear los refundables.
        if (bc.ReviewRequiredReason.HasFlag(ReviewRequiredReason.HasNonRefundableItems))
        {
            var refundableItems = invoiceItems.Where(i => i.IsRefundable).ToList();
            if (refundableItems.Count == 0)
            {
                // Edge case: todos los items eran no reintegrables. Hipoteticamente el
                // calculator no deberia haber clasificado esto como PartialOnOriginal,
                // pero defendemos: emitimos una sola linea con la descripcion template.
                return new[]
                {
                    new PartialCreditNoteLineDto(
                        Description: RenderPartialNcDescription(bc, settings, fiscalAmountToCredit),
                        Quantity: 1m,
                        UnitPrice: fiscalAmountToCredit,
                        Total: fiscalAmountToCredit,
                        AlicuotaIvaId: GetDominantAlicuotaId(invoiceItems)),
                };
            }

            // Factor de escala: cuanto del Total bruto refundable se acredita.
            // Si refundableSum es 0 (raro: items refundable con Total=0), factor=0 y
            // todas las lineas salen en 0 — el caller defensive lo va a rechazar igual.
            var refundableSumGross = refundableItems.Sum(i => i.Total);
            var scaleFactor = refundableSumGross > 0m ? fiscalAmountToCredit / refundableSumGross : 0m;

            var lines = new List<PartialCreditNoteLineDto>(refundableItems.Count);
            for (int i = 0; i < refundableItems.Count; i++)
            {
                var item = refundableItems[i];
                // Total escalado = item.Total * factor. Redondeo a 2 decimales para
                // que el XML al ARCA no lleve ruido. La ultima linea absorbe el
                // residuo de redondeo para que SUM(Lines.Total) == FiscalAmountToCredit
                // exacto (defensa contra mismatch en validacion pre-envio del job F2.2).
                decimal scaledTotal;
                if (i < refundableItems.Count - 1)
                {
                    scaledTotal = Math.Round(item.Total * scaleFactor, 2, MidpointRounding.AwayFromZero);
                }
                else
                {
                    var sumSoFar = lines.Sum(l => l.Total);
                    scaledTotal = Math.Round(fiscalAmountToCredit - sumSoFar, 2, MidpointRounding.AwayFromZero);
                }

                // UnitPrice mantiene relacion con quantity original. Si quantity es 0
                // (defensive), fallback a 1 para no dividir por cero.
                var qty = item.Quantity > 0m ? item.Quantity : 1m;
                var unitPrice = Math.Round(scaledTotal / qty, 2, MidpointRounding.AwayFromZero);

                lines.Add(new PartialCreditNoteLineDto(
                    Description: item.Description,
                    Quantity: qty,
                    UnitPrice: unitPrice,
                    Total: scaledTotal,
                    AlicuotaIvaId: item.AlicuotaIvaId));
            }
            return lines;
        }

        // Casos 2 y 3: no hay items no reintegrables. Vemos si la factura es mono o
        // multi-alicuota para decidir cuantas lineas armar.
        //
        // OJO: cambiamos el shape del groupBy para incluir un item "representativo"
        // por grupo. En MULTI-alicuota usamos la Description de ese item (MENOR 3
        // backend reviewer 2026-05-28): si dos lineas distintas comparten el mismo
        // template renderizado, el comprobante fisico no permite distinguirlas.
        // Tomar la descripcion del primer item del grupo preserva la trazabilidad
        // fiscal hacia los items originales de la factura.
        var alicuotaGroups = invoiceItems
            .GroupBy(i => i.AlicuotaIvaId)
            .Select(g => new
            {
                AlicuotaId = g.Key,
                GroupTotal = g.Sum(i => i.Total),
                RepresentativeDescription = g.First().Description ?? string.Empty,
            })
            .ToList();

        // Caso 3 (mono-alicuota): una sola linea con template rendered.
        // Justificacion: con UNA sola alicuota no hay ambiguedad entre lineas, y la
        // factura entera se acredita en un unico item — usamos la descripcion
        // narrativa del template ("NC parcial s/Fc... monto fiscal acreditado: $X").
        if (alicuotaGroups.Count == 1)
        {
            return new[]
            {
                new PartialCreditNoteLineDto(
                    Description: RenderPartialNcDescription(bc, settings, fiscalAmountToCredit),
                    Quantity: 1m,
                    UnitPrice: fiscalAmountToCredit,
                    Total: fiscalAmountToCredit,
                    AlicuotaIvaId: alicuotaGroups[0].AlicuotaId),
            };
        }

        // Caso 2 (multi-alicuotas): default (RH-001/OQ-2) preservar TODAS las alicuotas
        // con prorrateo proporcional al total por alicuota. Una linea por alicuota.
        // El setting IvaProrrateoMode puede cambiar el comportamiento en el FUTURO
        // (ProportionalToNet => colapsar a dominante), pero el plan F2.3 confirmo que
        // el default conservador es PerItem-like: preservar fidelidad fiscal.
        // DEUDA F2.x: cuando el contador confirme F1 (pregunta IvaProrrateoMode), si
        // dice "colapsar a dominante", aca habria que ramificar segun settings.IvaProrrateoMode.
        //
        // MENOR 3 (backend reviewer 2026-05-28): cada linea usa la Description del
        // item representativo de SU grupo de alicuota (no el template comun). Asi
        // dos lineas con alicuotas distintas quedan distinguibles en el comprobante
        // fisico. El render del template solo se usa en el caso mono-alicuota (ver
        // arriba), donde la factura completa se acredita y no hay riesgo de
        // ambiguedad.
        var totalGross = alicuotaGroups.Sum(g => g.GroupTotal);
        var multiLines = new List<PartialCreditNoteLineDto>(alicuotaGroups.Count);
        for (int i = 0; i < alicuotaGroups.Count; i++)
        {
            var g = alicuotaGroups[i];
            decimal lineTotal;
            if (i < alicuotaGroups.Count - 1)
            {
                // Prorrateo: porcion del FiscalAmountToCredit proporcional al peso de la
                // alicuota en la factura origen.
                var factor = totalGross > 0m ? g.GroupTotal / totalGross : 0m;
                lineTotal = Math.Round(fiscalAmountToCredit * factor, 2, MidpointRounding.AwayFromZero);
            }
            else
            {
                // Ultima linea absorbe residuo de redondeo (mismo patron que el caso 1).
                var sumSoFar = multiLines.Sum(l => l.Total);
                lineTotal = Math.Round(fiscalAmountToCredit - sumSoFar, 2, MidpointRounding.AwayFromZero);
            }

            // Truncado defensivo a 200 chars (mismo limite que aplica RenderPartialNcDescription).
            // InvoiceItem.Description en BD tiene MaxLength=200; si pasamos mas la insercion
            // del job F2.2 rebotaria.
            var description = g.RepresentativeDescription;
            if (description.Length > 200)
                description = description[..200];

            multiLines.Add(new PartialCreditNoteLineDto(
                Description: description,
                Quantity: 1m,
                UnitPrice: lineTotal,
                Total: lineTotal,
                AlicuotaIvaId: g.AlicuotaId));
        }
        return multiLines;
    }

    /// <summary>
    /// F2.3 — devuelve el id de alicuota IVA dominante (el que tiene mayor Total
    /// acumulado en los items de la factura origen).
    ///
    /// <para><b>R2 contador (2026-05-28)</b>: si la lista llega vacia, NO devolvemos
    /// un default fiscal (antes devolviamos 5 = 21%). Razon: una factura de hoteleria
    /// puede estar al 10.5% (alicuota 4); si por un bug aguas arriba se filtra mal
    /// y los items quedan vacios, devolver 21% por defecto haria que la NC parcial
    /// salga al ARCA con la alicuota equivocada = error fiscal silencioso. Mejor
    /// explotar aca con mensaje claro y que el operador investigue el bug.</para>
    ///
    /// <para><b>Visibilidad</b>: <c>internal static</c> (no <c>private</c>) para que el
    /// proyecto de tests pueda chequear esta regla directamente sin tener que armar
    /// todo el escenario de BookingCancellation. <c>InternalsVisibleTo</c> de
    /// TravelApi.Infrastructure -> TravelApi.Tests ya esta configurado en el csproj.</para>
    /// </summary>
    internal static int GetDominantAlicuotaId(IReadOnlyList<InvoiceItem> items)
    {
        if (items.Count == 0)
        {
            throw new InvalidOperationException(
                "GetDominantAlicuotaId llamado sin items. " +
                "No se puede inferir alicuota IVA. " +
                "Esto indica un bug aguas arriba (factura sin InvoiceItems o filtrado incorrecto).");
        }

        return items
            .GroupBy(i => i.AlicuotaIvaId)
            .OrderByDescending(g => g.Sum(i => i.Total))
            .First()
            .Key;
    }

    /// <summary>
    /// F2.3 — renderiza el template <see cref="OperationalFinanceSettings.PartialNcDescriptionTemplate"/>
    /// reemplazando las variables conocidas (<c>{invoiceType}</c>, <c>{invoiceNumber}</c>,
    /// <c>{fiscalAmount}</c>, etc.) con los valores del BC.
    ///
    /// <para><b>Truncado defensivo</b>: <c>InvoiceItem.Description</c> tiene
    /// <c>MaxLength=200</c>. Si el template renderizado supera ese limite, truncamos
    /// a 200 chars para no romper el INSERT en el job F2.2.</para>
    /// </summary>
    private static string RenderPartialNcDescription(
        BookingCancellation bc,
        OperationalFinanceSettings settings,
        decimal fiscalAmount)
    {
        var template = string.IsNullOrWhiteSpace(settings.PartialNcDescriptionTemplate)
            ? "Cancelacion parcial de reserva {invoiceNumber}." // fallback defensivo
            : settings.PartialNcDescriptionTemplate;

        var invoice = bc.OriginatingInvoice;
        var currency = bc.FiscalLiquidation?.Currency ?? bc.FiscalSnapshot?.CurrencyAtEvent ?? "ARS";
        var nonRefAmount = bc.FiscalLiquidation?.NonRefundableItemsAmount ?? 0m;
        var penaltyAmount = bc.FiscalLiquidation?.OperatorPenaltyAmount ?? 0m;

        var rendered = template
            .Replace("{invoiceType}", invoice?.TipoComprobante.ToString() ?? "?")
            .Replace("{invoiceNumber}", invoice?.NumeroComprobante.ToString() ?? "?")
            .Replace("{pointOfSale}", invoice?.PuntoDeVenta.ToString() ?? "?")
            .Replace("{fiscalAmount}", fiscalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{currency}", currency)
            .Replace("{cancellationReason}", bc.Reason ?? "")
            .Replace("{nonRefundableAmount}", nonRefAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{operatorPenaltyAmount}", penaltyAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{customerName}", bc.Customer?.FullName ?? "")
            .Replace("{customerTaxId}", bc.Customer?.TaxId ?? "");

        // Truncado defensivo a 200 chars (MaxLength de InvoiceItem.Description).
        return rendered.Length > 200 ? rendered[..200] : rendered;
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
            // NOTA (B-007): este Include trae la factura origen pero NO sus Tributes
            // (impuestos provinciales / IIBB) a proposito: el DTO actual no los proyecta,
            // asi que cargarlos seria traer datos al pedo en cada lectura.
            // CUANDO la UI futura necesite MOSTRAR los tributos provinciales, hay que
            // agregar aca: .ThenInclude(i => i.Tributes)  -- igual que en los 2 callers
            // del calculador (ConfirmAsync / EditLiquidationAsync). Si te lo olvidas,
            // la coleccion Tributes llega vacia (new List<>()) y el front muestra "sin
            // impuestos" aunque la base tenga 5 IIBB. Es el mismo bug fantasma del B-001:
            // sin lazy proxies, una navigation collection no incluida no es null, es vacia.
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

        // FC1.3 Fase 2 (RH-002): proyectar el owned VO FiscalLiquidation si existe.
        // Los owned types se cargan automaticamente con la entidad (no necesitan
        // Include explicito). Null = BC sin liquidacion calculada.
        FiscalLiquidationSummaryDto? liquidationDto = null;
        if (bc.FiscalLiquidation != null)
        {
            liquidationDto = new FiscalLiquidationSummaryDto
            {
                OriginalInvoiceAmount = bc.FiscalLiquidation.OriginalInvoiceAmount,
                CancellationAmount = bc.FiscalLiquidation.CancellationAmount,
                OperatorPenaltyAmount = bc.FiscalLiquidation.OperatorPenaltyAmount,
                NonRefundableItemsAmount = bc.FiscalLiquidation.NonRefundableItemsAmount,
                FiscalAmountToCredit = bc.FiscalLiquidation.FiscalAmountToCredit,
                AmountToRefundCustomer = bc.FiscalLiquidation.AmountToRefundCustomer,
                FinalNetInvoiced = bc.FiscalLiquidation.FinalNetInvoiced,
                Currency = bc.FiscalLiquidation.Currency,
                ComputedAt = bc.FiscalLiquidation.ComputedAt,
                ComputedByUserId = bc.FiscalLiquidation.ComputedByUserId,
                ComputedByUserName = bc.FiscalLiquidation.ComputedByUserName,
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
            FiscalLiquidation = liquidationDto,
            ArcaConfirmedManuallyAt = bc.ArcaConfirmedManuallyAt,
            ArcaConfirmedManuallyByUserId = bc.ArcaConfirmedManuallyByUserId,
            ArcaErrorMessage = bc.ArcaErrorMessage,
            // ADR-013/014: estado de la penalidad + de la ND, como string (igual que Status).
            PenaltyStatus = bc.PenaltyStatus.ToString(),
            DebitNoteStatus = bc.DebitNoteStatus.ToString(),
        };
    }
}
