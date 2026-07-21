using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Helpers;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.2.2 v3 §6.3 (2026-05-18): orquesta los ingresos fisicos que la agencia
/// recibe del operador (T2 del flujo) y los reparte N:M contra los
/// <see cref="BookingCancellation"/> via <see cref="OperatorRefundAllocation"/>.
///
/// <para>
/// <b>Patron de transacciones</b>: cada metodo abre una unidad de trabajo que
/// commitea con un solo <c>SaveChangesAsync</c> al final (HC1 plan v3). Los
/// services internos invocados (BookingCancellationService, ClientCreditService)
/// solo hacen <c>Add()</c> en memoria — el commit lo manejamos nosotros para
/// mantener atomicidad real entre allocation, deductions, manual cash movement
/// y client credit entry.
/// </para>
///
/// <para>
/// <b>Concurrencia N:M</b>: cuando dos cashiers allocate paralelos contra el
/// mismo refund, el cap se valida con un CHECK SQL Postgres
/// (<c>chk_OperatorRefundsReceived_allocated_not_exceeds</c>). Si supera, EF
/// recibe <c>SqlState 23514</c> y el <see cref="BusinessInvariantInterceptor"/>
/// lo traduce a <see cref="BusinessInvariantViolationException"/> con codigo
/// INV-084. El service ademas implementa retry xmin limitado: la primera
/// transaccion que detecta concurrencia recarga el refund + BC y reintenta.
/// </para>
///
/// <para>
/// <b>Matriz fiscal Mono/RI</b>: el modulo se comporta distinto segun la
/// condicion fiscal cristalizada en el <see cref="FiscalSnapshot"/> del BC:
/// <list type="bullet">
///   <item>INV-105: Supplier Monotributo + deducciones tipo retencion AR
///     (kinds 10..39) → rechazar (no esta inscripto en el regimen).</item>
///   <item>INV-115: Agency Monotributo + deducciones tipo retencion AR →
///     rechazar (no genera credito fiscal IVA).</item>
/// </list>
/// </para>
/// </summary>
public class OperatorRefundService : IOperatorRefundService
{
    private const int MaxConcurrencyRetries = 3;

    private readonly AppDbContext _db;
    private readonly IBookingCancellationService _bcService;
    private readonly IClientCreditService _clientCreditService;
    private readonly IAuditService _auditService;
    private readonly IOperationalFinanceSettingsService _settings;
    private readonly ILogger<OperatorRefundService> _logger;

    public OperatorRefundService(
        AppDbContext db,
        IBookingCancellationService bcService,
        IClientCreditService clientCreditService,
        IAuditService auditService,
        IOperationalFinanceSettingsService settings,
        ILogger<OperatorRefundService> logger)
    {
        _db = db;
        _bcService = bcService;
        _clientCreditService = clientCreditService;
        _auditService = auditService;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Pasos B/C (2026-06-29): reconcilia el POOL de saldo a favor del operador tras imputar / anular un reembolso
    /// (que movio el reembolso recibido y el receivable Y). Transaction-agnostic, dentro de la transaccion del
    /// caller; se llama DESPUES del SaveChanges que persistio el cambio. Idempotente; net-neutral en el caso normal
    /// (recibido &lt;= cap), asi que no toca el pool — solo lo mantiene coherente si el balance estaba viejo.
    /// </summary>
    private Task ReconcileSupplierCreditPoolAsync(int supplierId, string? actorUserId, string? actorUserName, CancellationToken ct)
        => TravelApi.Infrastructure.Reservations.SupplierCreditReconciler.ReconcileAsync(
            _db, supplierId, sourceSupplierPaymentId: null, actorUserId, actorUserName, _auditService, ct);

    // =========================================================================
    // RecordReceivedAsync
    // =========================================================================

    public Task<OperatorRefundReceivedDto> RecordReceivedAsync(
        RecordOperatorRefundRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
        // Flujo publico (2 pasos): NO sella llave de idempotencia, la deja null.
        => RecordReceivedInternalAsync(request, userId, userName, idempotencyKey: null, ct);

    /// <summary>
    /// Nucleo de <see cref="RecordReceivedAsync"/>. La <paramref name="idempotencyKey"/> se sella EN EL INSERT del
    /// ingreso (no en un UPDATE posterior). Esto es clave para la idempotencia del atajo record-and-allocate:
    /// <list type="bullet">
    ///   <item>El indice UNICO parcial dispara determinísticamente en el INSERT, ANTES de cualquier conflicto xmin
    ///         del BookingCancellation aguas abajo (el UPDATE post-insert corria ese riesgo — B1).</item>
    ///   <item>Si un retry posterior hace <c>ChangeTracker.Clear()</c>, al recargar el refund la llave viene DESDE
    ///         la fila (no se pierde). Sellarla como cambio pendiente separado la perdia con el Clear.</item>
    /// </list>
    /// El flujo de 2 pasos la deja en null (ese camino no tiene candado de idempotencia).
    /// </summary>
    private async Task<OperatorRefundReceivedDto> RecordReceivedInternalAsync(
        RecordOperatorRefundRequest request,
        string userId,
        string? userName,
        Guid? idempotencyKey,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        if (request.ReceivedAmount <= 0m)
        {
            // Defensivo aunque el DataAnnotation ya valida — el service tambien
            // puede ser invocado desde background jobs que no pasan por validator.
            throw new ArgumentException("El monto recibido debe ser mayor a cero.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != 3)
        {
            throw new ArgumentException("La moneda debe ser un código de 3 letras (por ejemplo: ARS o USD).", nameof(request));
        }

        // 1) Resolver Supplier (Include para que ManualCashMovementBuilder
        //    pueda armar la descripcion "Devolucion del operador {Name}").
        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.PublicId == request.SupplierPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"Supplier {request.SupplierPublicId} no encontrado.");

        // 2) Crear el aggregate del ingreso.
        var refund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id,
            Supplier = supplier, // navigation seteada explicitamente para que el builder NO tire
            ReceivedAt = request.ReceivedAt.ToUniversalTime(),
            ReceivedAmount = ReservationEconomicPolicy.RoundCurrency(request.ReceivedAmount),
            AllocatedAmount = 0m,
            Method = string.IsNullOrWhiteSpace(request.Method) ? "Transfer" : request.Method!,
            Reference = request.Reference,
            Currency = request.Currency.ToUpperInvariant(),
            // ExchangeRateAtReceipt: en MVP se setea = 1 si la moneda es ARS y no
            // viene en el request. Una FC futura agrega FetchedAt + fuente.
            ExchangeRateAtReceipt = 1m,
            ReceivedByUserId = userId,
            ReceivedByUserName = userName ?? string.Empty,
            // Sello de idempotencia EN EL INSERT (ver doc del metodo). Null en el flujo de 2 pasos.
            IdempotencyKey = idempotencyKey,
        };
        _db.OperatorRefundReceived.Add(refund);

        // 3) Crear el ManualCashMovement Income asociado al ingreso fisico
        //    (cierra INV-CONT-09: el deposito se ve en el Libro de Caja).
        //    El builder valida que Supplier no sea null y ReceivedAmount > 0.
        //
        //    Trainee/junior — bug fix 2026-05-18:
        //    El builder setea la NAVIGATION property `OperatorRefundReceived = refund`
        //    (no el FK escalar refund.Id). Eso es clave porque en este punto el
        //    refund acaba de hacer Add() y todavia NO se persistio: refund.Id == 0
        //    hasta que el SaveChangesAsync final corra. Si el builder seteara el
        //    FK escalar a 0, Postgres recibiria una FK invalida y rompe el INSERT
        //    con 23503. EF resuelve esto al guardar en orden topologico cuando
        //    seteamos la navigation property: inserta primero el refund, obtiene
        //    el Id real, y despues inserta el movement con la FK correcta.
        var movement = ManualCashMovementBuilder.BuildIncomeForRefund(refund, userId);
        // Sobrescribimos la Description si vino un Notes en el request — util
        // para que el cashier deje contexto operativo en caja.
        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            // Mantenemos el formato base + un sufijo con las notas (recorta a 500).
            var withNotes = $"{movement.Description} — {request.Notes!.Trim()}";
            movement.Description = withNotes.Length > 500 ? withNotes[..500] : withNotes;
        }
        _db.ManualCashMovements.Add(movement);

        // ADR-022 §4.4 (B1): asiento de caja del ingreso del refund, en la MISMA SaveChanges que el
        // ManualCashMovement. UN solo asiento por el manual (RK-1: NO se asienta el OperatorRefundReceived
        // por separado). La MONEDA sale del ORIGEN REAL (refund.Currency), NO del manual (que nace en ARS
        // por default y no refleja el hecho). Asi un refund en USD asienta en USD aunque el manual este en ARS.
        var refundLedgerEntry = TravelApi.Domain.Helpers.CashLedgerEntryFactory.ForManualMovement(
            movement, currencyOverride: refund.Currency, actorUserId: userId, actorUserName: userName);
        _db.CashLedgerEntries.Add(refundLedgerEntry);

        // 4) Audit. Lo armamos con todo el contexto fiscal del ingreso para que
        //    el contador pueda reconstruir el evento sin ir a la tabla principal.
        //    EntityId: usamos refund.PublicId (Guid asignado por default en el
        //    field de la entidad). El Id int todavia es 0 — solo lo tendremos
        //    despues del SaveChangesAsync final. AuditService persiste su propia
        //    fila inmediatamente (Repository.AddAsync llama SaveChanges), por
        //    eso necesitamos un identificador estable AHORA, no despues.
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.OperatorRefundReceivedRegistered,
            entityName: AuditActions.OperatorRefundReceivedEntityName,
            entityId: refund.PublicId.ToString(),
            details: JsonSerializer.Serialize(new
            {
                refundPublicId = refund.PublicId,
                supplierId = supplier.Id,
                supplierPublicId = supplier.PublicId,
                supplierName = supplier.Name,
                refund.ReceivedAmount,
                refund.Currency,
                refund.Method,
                refund.Reference,
                refund.ReceivedAt,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        // 5) SaveChanges unico: refund + movement + audit log se persisten juntos.
        await _db.SaveChangesAsync(ct);

        // FC1.2.7b counter: la caja registro un ingreso del operador. La
        // diferencia con el audit log es de rol: audit = traza fiscal, counter
        // = senial para metricas. Si refunds_received / dia cae a cero, puede
        // haber un problema con el operador o con la caja.
        _logger.LogInformation(
            "metric:operator_refund_received | RefundPublicId={RefundPublicId} SupplierId={SupplierId} Amount={Amount} Currency={Currency}",
            refund.PublicId, supplier.Id, refund.ReceivedAmount, refund.Currency);

        // 6) Mapeo de salida.
        return MapRefund(refund);
    }

    // =========================================================================
    // AllocateAsync (concurrencia N:M con retry xmin)
    // =========================================================================

    public Task<OperatorRefundAllocationDto> AllocateAsync(
        Guid refundPublicId,
        AllocateRefundRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
        // Flujo publico (2 pasos, SIN transaccion ambiente): SI reintenta internamente el conflicto xmin — es su
        // unica linea de defensa contra la carrera, y el ChangeTracker.Clear() del retry es seguro porque no hay
        // otra escritura envolvente que corromper.
        => AllocateCoreAsync(refundPublicId, request, userId, userName, allowInternalRetry: true, ct);

    /// <summary>
    /// Nucleo de <see cref="AllocateAsync"/>. <paramref name="allowInternalRetry"/> decide si un conflicto xmin se
    /// reintenta ACA (con <c>ChangeTracker.Clear()</c>) o si se deja BURBUJEAR al caller.
    ///
    /// <para><b>Por que existe el switch (B1, sub-modo b)</b>: cuando el atajo record-and-allocate corre este metodo
    /// DENTRO de su transaccion ambiente, el retry interno seria peligroso: el <c>ChangeTracker.Clear()</c> tiraria
    /// tambien las escrituras del ingreso ya hechas en esa transaccion (ingreso, movimiento de caja) y podria
    /// re-insertar / dejar estado parcial. Por eso el atajo llama con <c>allowInternalRetry=false</c>: un conflicto
    /// xmin PROPAGA hacia la ExecutionStrategy/transaccion externa -> rollback TOTAL -> el controller responde 409 ->
    /// el usuario reintenta con la MISMA llave -> el check-previo de idempotencia devuelve la operacion original.
    /// Asi no hay escrituras parciales ni doble cobro.</para>
    /// </summary>
    private async Task<OperatorRefundAllocationDto> AllocateCoreAsync(
        Guid refundPublicId,
        AllocateRefundRequest request,
        string userId,
        string? userName,
        bool allowInternalRetry,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        // Validaciones tempranas del payload (defensivo).
        if (request.GrossAmount <= 0m)
        {
            throw new ArgumentException("GrossAmount debe ser > 0.", nameof(request));
        }
        if (request.Deductions is null)
        {
            throw new ArgumentException("Deductions no puede ser null (usar lista vacia).", nameof(request));
        }
        foreach (var dl in request.Deductions)
        {
            if (dl.Amount <= 0m)
            {
                throw new ArgumentException("Cada deduccion debe tener Amount > 0.", nameof(request));
            }
        }

        // Dentro de la transaccion ambiente del atajo: NO reintentamos aca. Un conflicto xmin debe propagar para que
        // la transaccion externa haga rollback total (ver doc del metodo). Sin ChangeTracker.Clear() envolvente.
        if (!allowInternalRetry)
        {
            return await TryAllocateOnceAsync(refundPublicId, request, userId, userName, ct);
        }

        // Retry loop ante concurrencia xmin. Cada iteracion abre un Detach
        // implicito porque cargamos las entidades de nuevo desde la BD.
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            try
            {
                return await TryAllocateOnceAsync(refundPublicId, request, userId, userName, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Otra tx modifico el refund (o el BC) entre nuestro Load y
                // nuestro Save. Loggeamos a debug + clear ChangeTracker para
                // forzar reload, y reintentamos. La tercera vez relanzamos:
                // un cliente legitimo se rinde con un 409.
                _logger.LogWarning(ex,
                    "AllocateAsync concurrency conflict on attempt {Attempt}/{Max} for refund {RefundPublicId}. Retrying.",
                    attempt + 1, MaxConcurrencyRetries, refundPublicId);

                // Soltamos lo trackeado para que el siguiente Load no pelee con
                // las entidades stale del intento previo. ChangeTracker.Clear
                // es net8/EF8 — equivalente al viejo Detach manual.
                _db.ChangeTracker.Clear();

                if (attempt == MaxConcurrencyRetries - 1)
                {
                    throw;
                }

                // Backoff exponencial corto: 100ms, 400ms. Sin jitter porque
                // los conflicts genuinos son raros y un par de retries ordenados
                // alcanzan.
                var delayMs = (int)Math.Pow(4, attempt) * 100;
                await Task.Delay(delayMs, ct);
            }
        }

        // Unreachable (el loop o retorna o relanza), pero el compilador lo necesita. N1: mensaje generico en espanol,
        // sin jerga ni nombres de metodo, por si alguna vez llegara al usuario a traves del controller.
        throw new InvalidOperationException("La operación no pudo completarse, volvé a intentar.");
    }

    private async Task<OperatorRefundAllocationDto> TryAllocateOnceAsync(
        Guid refundPublicId,
        AllocateRefundRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // 1) Cargar refund + supplier (Include para el matrix fiscal del operador).
        var refund = await _db.OperatorRefundReceived
            .Include(r => r.Supplier)
            .FirstOrDefaultAsync(r => r.PublicId == refundPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"OperatorRefundReceived {refundPublicId} no encontrado.");

        // 2) Cargar BC target con su Reserva (necesaria para que el callback de
        //    BookingCancellationService pueda actualizar el Status de la Reserva).
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.Supplier)
            .Include(b => b.Lines)   // ADR-025: INV-126 se imputa a la(s) linea(s) del operador del refund
            .FirstOrDefaultAsync(b => b.PublicId == request.BookingCancellationPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"BookingCancellation {request.BookingCancellationPublicId} no encontrado.");

        // 3-4-bis) Guardas de plata compartidas con el atajo de 1 paso (RecordAndAllocateAsync):
        //          INV-093 (estado imputable) + INV-126 (operador coincide con alguna linea) + INV-118 (moneda
        //          coincide). Extraidas a un helper para tener UNA sola fuente de verdad de estas reglas (el atajo
        //          las reusa sin duplicarlas). Devuelve las lineas del operador del refund, que reusamos abajo
        //          (paso 10-bis) para imputar el neto recibido. Ver el helper para el detalle de cada regla.
        var operatorLines = EnsureBookingCancellationCanReceiveOperatorRefund(bc, refund.SupplierId, refund.Currency);

        // 5) Validar matriz fiscal Mono/RI usando el FiscalSnapshot cristalizado.
        //    Hacemos PRIMERO la validacion porque rechaza antes de tocar BD:
        //    un usuario equivocado se entera con HTTP 409 sin pasar por retry.
        ValidateFiscalMatrix(
            agencyTaxConditionAtEvent: bc.FiscalSnapshot?.AgencyTaxConditionAtEvent,
            supplierTaxConditionAtEvent: bc.FiscalSnapshot?.SupplierTaxConditionAtEvent,
            deductions: request.Deductions);

        // 5-bis) ADR-013 INV-ADR013-001 (anti-doble-cobro, §3.3) — DISYUNCION DURA.
        //
        //   La penalidad propia de la agencia se materializa EXACTAMENTE UNA VEZ. Si
        //   el concepto de la cancelacion es "ingreso propio de la agencia" (la ND
        //   propia cobra esa plata), entonces esa MISMA penalidad NO puede ademas
        //   bajar el refund que recibe el cliente. La via de neteo del refund es
        //   cargar una deduction Kind=CancellationPenalty en la allocation del
        //   operador (netAmount = gross - deducciones). Las dos vias son mutuamente
        //   excluyentes para un mismo monto.
        //
        //   Por eso: si el concepto del BC EMITE una ND por la penalidad, RECHAZAMOS
        //   cargar una deduction CancellationPenalty. Esa penalidad ya se cobra (o se va
        //   a cobrar) con la ND; netearla aca seria cobrarla dos veces.
        //
        //   IMPORTANTE (regla fiscal firmada): el universo de "emite ND" es MAS amplio
        //   que "ingreso propio de la agencia". Incluye TAMBIEN la penalidad pass-through
        //   del operador, que se le cobra al cliente con una ND no gravada. Si miraramos
        //   solo ConceptIsAgencyOwnedDebitNote, un pass-through con ND emitida podria
        //   ademas netearse del refund = doble cobro de la misma multa. Por eso usamos
        //   ConceptEmitsDebitNote (cubre agency-owned + pass-through). La guarda espejo del
        //   lado de la ND (TryEmitCancellationDebitNoteAsync) ya bloquea por la existencia
        //   de la deduction sin importar el concepto, asi que ambos lados quedan cerrados.
        //
        //   La validacion vive en runtime (no en un CHECK SQL) porque es
        //   cross-aggregate (BC + allocation del operador): un CHECK acoplaria dos
        //   tablas. Mismo precedente que INV-126 (validacion cross-aggregate en
        //   runtime, no en BD).
        //   Sutileza del flag (byte-identidad con OFF): con EnableCancellationDebitNote=OFF
        //   la penalidad pass-through NO emite ND y se cobra UNICAMENTE neteando el refund
        //   (camino legacy). En ese caso NO debemos bloquear la deduction, o romperiamos el
        //   comportamiento previo. Por eso el pass-through entra a la disyuncion SOLO con el
        //   flag ON (cuando la ND realmente esta en juego). El cargo propio de la agencia, en
        //   cambio, solo puede existir con el flag ON (la clasificacion se cortocircuita con
        //   OFF), asi que mirarlo siempre es seguro.
        var settings = await _settings.GetEntityAsync(ct);
        var penaltyChargedViaDebitNote =
            BookingCancellationService.ConceptIsAgencyOwnedDebitNote(bc.ConceptKind) ||
            (settings.EnableCancellationDebitNote &&
             bc.ConceptKind == CancellationConceptKind.OperatorPenaltyPassThrough);
        if (penaltyChargedViaDebitNote &&
            request.Deductions.Any(d => d.Kind == DeductionKind.CancellationPenalty))
        {
            throw new BusinessInvariantViolationException(
                "La penalidad de esta cancelacion se cobra al cliente con una Nota de Debito; " +
                "no puede ademas descontarse del reintegro al cliente (seria doble cobro). " +
                "Quita la deduccion de penalidad o reclasifica el concepto.",
                invariantCode: "INV-ADR013-001");
        }

        // 6) Calcular NetAmount + total deducciones. INV-112 (CHECK SQL) valida
        //    NetAmount >= 0 y GrossAmount >= NetAmount, pero validamos antes
        //    para devolver un mensaje claro.
        var totalDeductions = request.Deductions.Sum(d => d.Amount);
        var netAmount = ReservationEconomicPolicy.RoundCurrency(request.GrossAmount - totalDeductions);
        if (netAmount < 0m)
        {
            throw new BusinessInvariantViolationException(
                $"El monto neto quedaría negativo: las deducciones ({totalDeductions}) no pueden superar " +
                $"al monto bruto del reintegro ({request.GrossAmount}).",
                invariantCode: "INV-112");
        }

        // 7) Crear la allocation. El CHECK SQL del cap se evalua al SaveChanges
        //    final (cuando refund.AllocatedAmount tenga el valor incrementado).
        var allocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id,
            Refund = refund,
            BookingCancellationId = bc.Id,
            BookingCancellation = bc,
            GrossAmount = ReservationEconomicPolicy.RoundCurrency(request.GrossAmount),
            NetAmount = netAmount,
            IsVoided = false,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = userId,
        };

        // 8) Crear DeductionLines (1:N con la allocation).
        foreach (var dlReq in request.Deductions)
        {
            var line = new DeductionLine
            {
                Kind = dlReq.Kind,
                Amount = ReservationEconomicPolicy.RoundCurrency(dlReq.Amount),
                CertificateNumber = dlReq.CertificateNumber,
                CertificateDate = dlReq.CertificateDate,
                CertificatePdfUrl = dlReq.CertificatePdfUrl,
                Jurisdiction = dlReq.Jurisdiction,
                ForeignCountryCode = dlReq.ForeignCountryCode,
                Description = dlReq.Description,
                SupportingDocumentRef = dlReq.SupportingDocumentRef,
                JustificationComment = dlReq.JustificationComment,
                MissingFiscalSupport = dlReq.MissingFiscalSupport,
                Comment = dlReq.Comment,
                RequiresAccountingReview = dlReq.RequiresAccountingReview,
                Allocation = allocation,
            };
            allocation.Deductions.Add(line);
        }

        _db.OperatorRefundAllocations.Add(allocation);

        // 9) Incrementar el cap del refund con el NETO (NO el gross).
        //    Por que NetAmount y no GrossAmount: el contrato de
        //    OperatorRefundReceived.AllocatedAmount es "SUM(allocations.NetAmount
        //    WHERE NOT IsVoided)" (ver doc de la propiedad + ADR-002 §2.5 +
        //    plan tactico FC1.2 v3 §6.3 paso 4). Las deducciones del operador
        //    (penalidades, comisiones, retenciones que se queda el operador)
        //    NO entran a la caja de la agencia, por lo tanto NO consumen el cap
        //    del ingreso fisico. Si pusieramos GrossAmount el cap se consumiria
        //    de mas y la agencia "perderia" capacidad de asignar a otros BC.
        //    El CHECK SQL chk_OperatorRefundsReceived_allocated_not_exceeds
        //    valida AllocatedAmount <= ReceivedAmount al SaveChanges final.
        refund.AllocatedAmount += allocation.NetAmount;

        // 10) Actualizar el denormalizado del BC: ReceivedRefundAmount = SUM
        //     allocations.NetAmount activas. Lo incrementamos en memoria; un
        //     test invariante periodico (CancellationFlowE2E) lo reconcilia
        //     contra la suma real para detectar drift (HC del plan).
        bc.ReceivedRefundAmount += allocation.NetAmount;

        // 10-bis) ADR-025 (B2): imputar el neto recibido a la(s) linea(s) del operador,
        //         AGREGADO por operador. Llenamos linea por linea hasta su RefundCap
        //         (cuando hay cap definido) y el excedente cae en la ultima linea, asi el
        //         total imputado al operador == lo recibido. NUNCA dejamos saldo negativo.
        //         Las lineas del operador que quedan con su cap cubierto pasan a Settled.
        DistributeReceivedRefundToOperatorLines(operatorLines, allocation.NetAmount);

        // 10-ter) ADR-044 T3b Decision 3 (2026-07-10): esta allocation puede ser la LIQUIDACION real de un cargo
        // Retenida cuya ND ya salio con conversion de moneda (TC definitivo fijado al emitir). Si es asi, registra
        // el ajuste de diferencia de cambio de tesoreria (delta entre el TC de la ND y el TC de este recibo). Es
        // gestion interna (no toca comprobantes ni el saldo del cliente): idempotente, no-op si no aplica.
        await TreasuryFxAdjustmentEngine.RegisterForRetainedChargesAsync(_db, allocation, _logger, ct);

        // 11) Notificar al BookingCancellationService que hubo allocation.
        //     Si era la primera activa, transiciona el BC a ClientCreditApplied.
        //     IMPORTANTE: el callback NO commitea (HC1) — solo modifica el Status
        //     en memoria. El SaveChanges unico viene al final.
        await _bcService.OnAllocationRecordedAsync(bc.Id, allocation.NetAmount, ct);

        // 12) Crear el ClientCreditEntry: el cliente recibe NetAmount como
        //     saldo a favor. RemainingBalance arranca = NetAmount; FC1.2.3
        //     gestionara retiros.
        //
        //     Guard defensivo: si las deducciones consumen el gross completo
        //     (netAmount == 0), no tiene sentido crear un entry con saldo 0.
        //     ClientCreditService.CreateEntryAsync tira ArgumentException
        //     internamente, pero validamos aca para fallar ANTES del Add() del
        //     entry — asi la transaccion envolvente no queda con cambios parciales
        //     (allocation + bc.Status modificado) que el rollback tiene que limpiar.
        //     Bajo el patron HC1 (no SaveChanges intermedio) el rollback se hace
        //     solo via ChangeTracker.Clear() del retry, pero un fail temprano es
        //     menos costoso y mas legible.
        if (allocation.NetAmount <= 0m)
        {
            throw new ArgumentException(
                "Las deducciones igualaron o superaron al GrossAmount: el cliente recibiria saldo cero. " +
                "Revisar los montos del refund o las deducciones.",
                nameof(request));
        }

        // ADR-042 §3.3.2 (C1, 2026-07-01): resolver la moneda del saldo a favor. El minteo es 1:1 con lo que
        // devolvio el operador (allocation.NetAmount, en refund.Currency) — NUNCA se inventa FX ni se mintea
        // plata que no entro. Pero SOLO se mintea si esa moneda coincide con la de alguna obligacion imputada
        // del cliente. Divergencia (operador reembolsa en moneda != obligacion) -> revision manual, mismo
        // criterio conservador que el pre-flight de TC=1. Sin obligaciones imputadas (solo pagos a cuenta) ->
        // a cuenta = moneda de pago, se mintea. El discriminador de la obligacion es Payment.ImputedCurrency
        // (ADR-021), no un vinculo pago<->factura (LinkedInvoiceId es informativo).
        //
        // TOPE del credito (S2 review 2026-07-02): el MONTO minteado (allocation.NetAmount) lo acota el cap del
        // operator-refund — refund.AllocatedAmount += NetAmount con CHECK SQL AllocatedAmount <= ReceivedAmount
        // (chk_OperatorRefundsReceived_allocated_not_exceeds, aplicado en el SaveChanges final). O sea, el saldo
        // a favor esta acotado por lo EFECTIVAMENTE DEVUELTO por el operador. En el caso normal eso es <= lo
        // cobrado al cliente por construccion (markup). El "tope = cobrado" estricto por-moneda de §3.3.2 NO se
        // enforce como segundo candado aca (seria redundante en el caso normal y un cambio de politica del
        // operator-refund fuera del alcance de C1). Ver CreditAllocationCurrencyResolver para el detalle.
        var obligationCurrencies = await _db.Payments
            .Where(p => p.ReservaId == bc.ReservaId
                     && !p.IsDeleted
                     && p.Status != "Cancelled"
                     && p.ImputedCurrency != null)
            .Select(p => p.ImputedCurrency!)
            .Distinct()
            .ToListAsync(ct);

        var creditDecision = CreditAllocationCurrencyResolver.ResolveCreditCurrency(
            refund.Currency, obligationCurrencies);
        if (creditDecision.RequiresManualReview)
        {
            _logger.LogWarning(
                "metric:operator_refund_credit_currency_divergence | BcId={BcId} RefundCurrency={RefundCurrency} " +
                "ObligationCurrencies={ObligationCurrencies}",
                bc.Id, refund.Currency, string.Join("/", obligationCurrencies));
            throw new BusinessInvariantViolationException(
                "El reembolso del operador esta en una moneda distinta de la que el cliente tiene registrada. " +
                "Revisalo manualmente antes de generar el saldo a favor.",
                invariantCode: "INV-042-CREDIT-CURRENCY");
        }

        // Pasamos la entidad y NO el Id, porque al momento de esta llamada
        // allocation.Id == 0 (todavia no se persistio — HC1 plan v3: un solo
        // SaveChanges al final). EF8 resuelve la FK al hacer SaveChanges en
        // orden topologico: primero inserta la allocation (obtiene Id real), y
        // despues el entry con esa FK ya resuelta. Setear el Id en el entry
        // ahora dejaria 0 escrito en Postgres y rompe la FK.
        await _clientCreditService.CreateEntryAsync(
            bookingCancellationId: bc.Id,
            operatorRefundAllocation: allocation,
            customerId: bc.CustomerId,
            netAmount: allocation.NetAmount,
            currency: creditDecision.Currency,
            userId: userId,
            userName: userName,
            ct: ct);

        // 13) Audit con metadata completa.
        //     EntityId: usamos el PublicId (Guid asignado en el constructor de
        //     la entidad, default = Guid.NewGuid()). NO podemos usar el Id int
        //     porque todavia es 0 hasta el SaveChanges final — el audit log se
        //     persiste antes (AuditService.LogBusinessEventAsync hace su propio
        //     SaveChanges interno). El PublicId es estable desde el momento del
        //     Add() y permite trazar la fila aun antes de que la BD le asigne Id.
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.OperatorRefundAllocated,
            entityName: AuditActions.OperatorRefundAllocationEntityName,
            entityId: allocation.PublicId.ToString(),
            details: JsonSerializer.Serialize(new
            {
                allocationPublicId = allocation.PublicId,
                refundPublicId = refund.PublicId,
                bcPublicId = bc.PublicId,
                allocation.GrossAmount,
                allocation.NetAmount,
                refundAllocatedAmountAfter = refund.AllocatedAmount,
                deductions = request.Deductions.Select(d => new
                {
                    d.Kind,
                    d.Amount,
                    d.Description,
                }).ToList(),
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        // 14) SaveChanges UNICO al final. EF persiste:
        //       - allocation (+ deductions cascade)
        //       - bc.Status update + bc.ReceivedRefundAmount
        //       - refund.AllocatedAmount update (xmin check)
        //       - client credit entry
        //       - manual cash movement (si aplicara — aca no, ya se hizo en
        //         RecordReceivedAsync)
        //       - audit log
        await _db.SaveChangesAsync(ct);

        // Pasos B/C (2026-06-29): imputar un reembolso movio el REEMBOLSO RECIBIDO(+) y bajo el receivable Y(-) del
        // operador en igual monto (net-neutral, §4.6), asi que el sobrepago economico no cambia y el reconciler
        // suele ser no-op; pero lo disparamos para mantener el pool coherente con el estado YA committed. Misma
        // transaccion del caller. El reconciler hace su propio SaveChanges.
        await ReconcileSupplierCreditPoolAsync(refund.SupplierId, userId, userName, ct);

        // FC1.2.7b counter: una allocation N:M aplicada. Util cruzar contra
        // operator_refund_received para ver si los ingresos quedan "huerfanos"
        // (recibidos pero no allocados a ninguna BC). Si el ratio
        // received/allocated diverge, la caja tiene plata sin imputar.
        _logger.LogInformation(
            "metric:operator_refund_allocated | AllocationPublicId={AllocationPublicId} RefundPublicId={RefundPublicId} BcPublicId={BcPublicId} NetAmount={NetAmount}",
            allocation.PublicId, refund.PublicId, bc.PublicId, allocation.NetAmount);

        return MapAllocation(allocation);
    }

    // =========================================================================
    // RecordAndAllocateAsync (atajo atomico: registrar + imputar en 1 llamada)
    // =========================================================================

    public async Task<OperatorRefundAllocationDto> RecordAndAllocateAsync(
        RecordAndAllocateRefundRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        // Validaciones de payload (defensivo; los DataAnnotations ya validan en el borde HTTP, pero el service
        // tambien puede invocarse desde otros callers que no pasan por el validator del controller).
        if (request.ReceivedAmount <= 0m)
        {
            throw new ArgumentException("El monto recibido debe ser mayor a cero.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != 3)
        {
            throw new ArgumentException("La moneda debe ser un código de 3 letras (por ejemplo: ARS o USD).", nameof(request));
        }
        // [Required] sobre un Guid nunca rechaza Guid.Empty (no es null). Sin una llave real no hay candado de
        // idempotencia, asi que la exigimos aca. Es la unica proteccion server-side contra el doble cobro.
        if (request.IdempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("Falta la referencia de la operación. Volvé a abrir la ficha e intentá de nuevo.");
        }

        // Idempotencia — CHECK PREVIO (camino feliz del reintento): si YA registramos un reembolso con esta misma
        // llave, NO creamos un segundo. Devolvemos la operacion original como EXITO idempotente (misma respuesta que
        // la primera vez), transparente para el usuario. Un doble clic / reintento de red / dos pestañas no duplica
        // ni el ingreso ni el saldo a favor del cliente. La red DURA contra la carrera real (dos requests a la vez)
        // es el indice UNICO parcial de mas abajo; este check evita el trabajo (y el 23505) en el caso comun.
        var alreadyProcessed = await TryGetExistingAllocationByIdempotencyKeyAsync(request.IdempotencyKey, ct);
        if (alreadyProcessed is not null)
        {
            return alreadyProcessed;
        }

        // Pre-flight ANTES de registrar plata en caja. Doble proposito:
        //   (a) Mensaje amable y accionable para el caso "abandonada por vencimiento" (necesita reabrirse primero).
        //   (b) Atomicidad-por-orden: si la cancelacion no puede recibir el reembolso, no llegamos a crear el
        //       ingreso fisico -> no queda plata huerfana. Contra Postgres ademas envolvemos todo en una
        //       transaccion (rollback real); en tests InMemory (sin transacciones) este pre-flight es lo unico
        //       que garantiza el no-huerfano. La validacion AUTORITATIVA vuelve a correr dentro de AllocateAsync
        //       (misma guarda compartida) como defensa en profundidad.
        var currencyUpper = request.Currency.ToUpperInvariant();
        await PreflightForRecordAndAllocateAsync(
            request.SupplierPublicId, request.BookingCancellationPublicId, currencyUpper, ct);

        var recordRequest = new RecordOperatorRefundRequest(
            SupplierPublicId: request.SupplierPublicId,
            ReceivedAmount: request.ReceivedAmount,
            Currency: request.Currency,
            ReceivedAt: request.ReceivedAt,
            Method: request.Method,
            Reference: request.Reference,
            Notes: request.Notes);

        // Camino SIMPLE: sin deducciones fiscales. Todo el bruto va a saldo a favor del cliente (Net == Gross).
        var allocateRequest = new AllocateRefundRequest(
            BookingCancellationPublicId: request.BookingCancellationPublicId,
            GrossAmount: request.ReceivedAmount,
            Deductions: new List<DeductionLineRequest>());

        // Resultado que devuelve la lambda transaccional.
        OperatorRefundAllocationDto result = null!;

        async Task RegisterAndAllocateAsync()
        {
            // Reusamos los nucleos internos (sin duplicar su logica ni sus validaciones). Cada uno hace su propio
            // SaveChanges; dentro de la transaccion envolvente ambos escriben a la MISMA transaccion y solo se
            // materializan en el CommitAsync final.
            //
            // B1 fix (2 partes):
            //  (1) La llave se sella EN EL INSERT del ingreso (idempotencyKey pasada a RecordReceivedInternalAsync),
            //      NO en un UPDATE post-insert. Asi el indice UNICO parcial dispara determinísticamente en el INSERT
            //      (23505 capturado afuera y resuelto idempotentemente), antes de cualquier baile de xmin del BC, y
            //      la llave sobrevive a un eventual ChangeTracker.Clear() (se recarga desde la fila).
            //  (2) La imputacion corre con allowInternalRetry=false: dentro de esta transaccion ambiente NO queremos
            //      el retry-con-Clear de AllocateAsync (corromperia/re-insertaria). Un conflicto xmin PROPAGA ->
            //      rollback total -> controller 409 -> el usuario reintenta con la misma llave -> replay idempotente.
            var refund = await RecordReceivedInternalAsync(
                recordRequest, userId, userName, idempotencyKey: request.IdempotencyKey, ct);

            result = await AllocateCoreAsync(
                refund.PublicId, allocateRequest, userId, userName, allowInternalRetry: false, ct);
        }

        try
        {
            if (_db.Database.IsRelational())
            {
                // Mismo patron que ClientCreditService/ReservaService: el ExecutionStrategy reintenta la lambda entera
                // ante errores TRANSITORIOS de Postgres, y la transaccion garantiza rollback total. Si la imputacion
                // falla, el ingreso NUNCA queda registrado: no hay plata huerfana en caja. Una violacion de unicidad
                // (23505) NO es transitoria: el ExecutionStrategy no la reintenta, la relanza — la maneja el catch.
                var strategy = _db.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                    await RegisterAndAllocateAsync();
                    await transaction.CommitAsync(ct);
                });
            }
            else
            {
                // Proveedor no-relacional (tests InMemory): no soporta transacciones NI indices unicos. La atomicidad
                // la cubre el pre-flight de arriba (validamos antes de registrar) y la carrera la cubre el CHECK PREVIO
                // por llave. El rollback REAL y el candado 23505 se testean en integracion Postgres.
                await RegisterAndAllocateAsync();
            }
        }
        catch (DbUpdateException ex) when (IsIdempotencyKeyUniqueViolation(ex))
        {
            // CARRERA REAL: entre nuestro CHECK PREVIO y nuestro commit, otro request con la MISMA llave gano el
            // INSERT. La transaccion ya hizo ROLLBACK total, asi que NO quedo ingreso huerfano ni saldo a favor
            // duplicado. Resolvemos idempotentemente: soltamos el tracker (quedo sucio por el intento abortado),
            // recargamos la operacion original y la devolvemos como exito — el usuario ve lo mismo que el ganador,
            // sin un 500 ni un duplicado.
            _db.ChangeTracker.Clear();
            var winner = await TryGetExistingAllocationByIdempotencyKeyAsync(request.IdempotencyKey, ct);
            if (winner is not null)
            {
                return winner;
            }

            // Defensa: si la llave no aparece (no deberia pasar tras un 23505 sobre ese indice), relanzamos para
            // no devolver un resultado inventado. El controller lo traduce a 409, nunca a un doble cobro.
            throw;
        }

        return result;
    }

    /// <summary>
    /// Idempotencia (2026-07-01): busca la imputacion YA creada por un request previo con la misma
    /// <paramref name="idempotencyKey"/>. Devuelve el DTO de esa allocation (exito idempotente) o null si esta llave
    /// todavia no se proceso. Solo lee (AsNoTracking) — no interfiere con el tracking de la transaccion posterior.
    ///
    /// <para>Solo considera allocations NO anuladas: el atajo crea exactamente UNA por ingreso, y si esa se anulara
    /// despues por otro flujo, el reintento no debe "revivirla".</para>
    /// </summary>
    private async Task<OperatorRefundAllocationDto?> TryGetExistingAllocationByIdempotencyKeyAsync(
        Guid idempotencyKey,
        CancellationToken ct)
    {
        var allocation = await _db.OperatorRefundAllocations
            .AsNoTracking()
            .Include(a => a.Refund)
            .Include(a => a.BookingCancellation)
            .Include(a => a.Deductions)
            .FirstOrDefaultAsync(
                a => a.Refund.IdempotencyKey == idempotencyKey && !a.IsVoided, ct);

        return allocation is null ? null : MapAllocation(allocation);
    }

    /// <summary>
    /// Detecta la violacion del indice UNICO de IDEMPOTENCIA (dos requests con la misma llave). ESPECIFICO a ese
    /// indice a proposito: NO tragamos otras violaciones de unicidad del modulo (p.ej. el unico parcial "una
    /// allocation activa por refund+cancelacion"), que deben seguir su flujo normal y no representan un reintento.
    /// Mismo patron de deteccion que <c>AfipService.IsUniqueConstraintViolation</c>.
    /// </summary>
    private static bool IsIdempotencyKeyUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is Npgsql.PostgresException pg
            && pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation
            && pg.ConstraintName is not null
            && pg.ConstraintName.Contains("IdempotencyKey", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pre-flight del atajo: carga la cancelacion y valida que PUEDA recibir el reembolso ANTES de registrar el
    /// ingreso fisico. Da un mensaje accionable para el caso "abandonada" y reusa la MISMA guarda de plata que la
    /// imputacion de 2 pasos (estado / operador / moneda). Solo lee (AsNoTracking) — no muta nada.
    /// </summary>
    private async Task PreflightForRecordAndAllocateAsync(
        Guid supplierPublicId,
        Guid bookingCancellationPublicId,
        string refundCurrency,
        CancellationToken ct)
    {
        // Resolvemos el operador por PublicId para poder chequear la coincidencia INV-126 (que trabaja con el Id
        // interno de la linea). Si no existe -> 404, igual que RecordReceivedAsync.
        var supplier = await _db.Suppliers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.PublicId == supplierPublicId, ct)
            ?? throw new KeyNotFoundException($"Supplier {supplierPublicId} no encontrado.");

        var bc = await _db.BookingCancellations
            .AsNoTracking()
            .Include(b => b.Lines)
            .FirstOrDefaultAsync(b => b.PublicId == bookingCancellationPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"BookingCancellation {bookingCancellationPublicId} no encontrado.");

        // Caso "abandonada por vencimiento": el job de timeout la cerro porque el operador no reintegro a tiempo.
        // NO es imputable directo — hay que REABRIRLA primero (transicion controlada que extiende el plazo). En vez
        // del INV-093 generico devolvemos un mensaje que dice EXACTAMENTE que hacer, sin jerga ni nombres internos.
        if (bc.Status == BookingCancellationStatus.AbandonedByOperator)
        {
            throw new BusinessInvariantViolationException(
                "Esta cancelación figura como vencida porque el operador no reintegró a tiempo. " +
                "Para registrar un reembolso tardío, primero reabrila desde la bandeja de reembolsos a cobrar " +
                "y después registrá el ingreso.",
                invariantCode: "INV-093");
        }

        // Resto de guardas (estado imputable / operador coincide / moneda coincide): misma fuente de verdad que
        // AllocateAsync. Descartamos las lineas devueltas: aca solo validamos.
        EnsureBookingCancellationCanReceiveOperatorRefund(bc, supplier.Id, refundCurrency);
    }

    /// <summary>
    /// ADR-025 (B2 / decision #2): reparte el neto recibido de un operador entre SUS lineas, agregado.
    /// El operador devuelve UN monto, no "por servicio"; lo distribuimos linea por linea hasta el
    /// <see cref="BookingCancellationLine.RefundCap"/> de cada una y el excedente cae en la ultima
    /// (asi el total imputado == lo recibido). Marca Settled las lineas cuyo cap quedo cubierto.
    /// </summary>
    // internal (no private) para poder testear la regla de imputacion B2 como funcion pura
    // (sin DB) desde TravelApi.Tests (InternalsVisibleTo). No tiene dependencias de EF.
    internal static void DistributeReceivedRefundToOperatorLines(
        List<BookingCancellationLine> operatorLines,
        decimal netReceived)
    {
        if (operatorLines.Count == 0) return;

        decimal remaining = netReceived;
        for (int i = 0; i < operatorLines.Count; i++)
        {
            var line = operatorLines[i];
            bool isLastLine = i == operatorLines.Count - 1;

            // Capacidad libre de la linea segun su cap. La ultima linea absorbe todo lo que reste
            // (incluso por encima del cap, para no "perder" plata recibida que no entra en ningun cap).
            decimal freeCapacity = line.RefundCap - line.ReceivedRefundAmount;
            decimal toThisLine = isLastLine
                ? remaining
                : Math.Min(Math.Max(freeCapacity, 0m), remaining);

            line.ReceivedRefundAmount += toThisLine;
            remaining -= toThisLine;

            // Settled cuando ya cubrio su cap (o cuando hay cap 0 y recibio algo). El cap 0 significa
            // "no se esperaba refund de esta linea"; si igual entro plata, queda en PendingOperatorRefund
            // salvo que la cubra completamente la imputacion.
            if (line.RefundCap > 0m && line.ReceivedRefundAmount >= line.RefundCap)
                line.RefundStatus = BookingCancellationLineRefundStatus.Settled;
            else if (line.ReceivedRefundAmount > 0m && line.RefundStatus == BookingCancellationLineRefundStatus.None)
                line.RefundStatus = BookingCancellationLineRefundStatus.PendingOperatorRefund;

            if (remaining <= 0m) break;
        }
    }

    /// <summary>
    /// ADR-041 T4 (MENOR 1, review 2026-06-28): INVERSA de <see cref="DistributeReceivedRefundToOperatorLines"/>.
    /// Cuando se ANULA una allocation, hay que sacar el neto anulado de las MISMAS lineas del operador a las que
    /// se imputo al recibir, sino el estimado por linea del read-model "esperando reembolso" queda subestimado
    /// (la linea figura con plata recibida que en realidad se anulo). Drenamos en orden INVERSO al de la
    /// imputacion (Distribute llena de la primera a la ultima y la ultima absorbe el excedente; al revertir
    /// sacamos primero de la ultima) y recomputamos el <see cref="BookingCancellationLineRefundStatus"/> de cada
    /// linea tocada. Nunca deja una linea en negativo (clamp por linea). El total drenado == lo recibido por el
    /// operador en esas lineas, asi que el AGREGADO por operador que usa el read-model queda exacto.
    /// </summary>
    // internal static (sin EF) para poder testear la simetria como funcion pura desde TravelApi.Tests.
    internal static void RemoveReceivedRefundFromOperatorLines(
        List<BookingCancellationLine> operatorLines,
        decimal netRemoved)
    {
        if (operatorLines.Count == 0) return;

        decimal remaining = netRemoved;
        for (int i = operatorLines.Count - 1; i >= 0 && remaining > 0m; i--)
        {
            var line = operatorLines[i];

            decimal take = Math.Min(line.ReceivedRefundAmount, remaining);
            line.ReceivedRefundAmount -= take;
            remaining -= take;

            // Recompute del estado tras restar: si seguia cubriendo el cap, Settled; si quedo con algo, pendiente;
            // si quedo en cero, vuelve a None (la linea ya no recibio nada del operador).
            if (line.RefundCap > 0m && line.ReceivedRefundAmount >= line.RefundCap)
                line.RefundStatus = BookingCancellationLineRefundStatus.Settled;
            else if (line.ReceivedRefundAmount > 0m)
                line.RefundStatus = BookingCancellationLineRefundStatus.PendingOperatorRefund;
            else
                line.RefundStatus = BookingCancellationLineRefundStatus.None;
        }
    }

    /// <summary>
    /// Guardas de plata que decide si una cancelacion puede recibir un reembolso del operador. UNA sola fuente de
    /// verdad para la imputacion de 2 pasos (<see cref="AllocateAsync"/>) y para el atajo de 1 paso
    /// (<see cref="RecordAndAllocateAsync"/>). Devuelve las lineas del operador del refund (se reusan aguas abajo
    /// para imputar el neto recibido).
    ///
    /// <para>NO valida la matriz fiscal ni la disyuncion ADR-013 (esas dependen de las deducciones del request y
    /// quedan en el caller). Requiere que <c>bc.Lines</c> este cargado.</para>
    /// </summary>
    private static List<BookingCancellationLine> EnsureBookingCancellationCanReceiveOperatorRefund(
        BookingCancellation bc,
        int refundSupplierId,
        string refundCurrency)
    {
        // INV-093: solo BCs post-CAE (esperando reembolso o con credito ya aplicado) pueden recibir imputaciones.
        var validStates = new[]
        {
            BookingCancellationStatus.AwaitingOperatorRefund,
            BookingCancellationStatus.ClientCreditApplied,
        };
        if (!validStates.Contains(bc.Status))
        {
            throw new BusinessInvariantViolationException(
                "Esta cancelación no está en condiciones de recibir el reembolso del operador en este momento.",
                invariantCode: "INV-093");
        }

        // INV-126 (ADR-025: reformulado a nivel LINEA): el operador del ingreso (refundSupplierId) tiene que
        // coincidir con el operador de AL MENOS UNA linea de esta cancelacion. Sin este check un cashier podria
        // allocate por error un ingreso de Despegar contra una cancelacion que no tiene a Despegar como operador de
        // ninguna linea, contaminando el cap y los reportes por operador. Validacion runtime cross-aggregate (no BD).
        //
        // IMPUTACION AGREGADA POR OPERADOR (B2 / decision #2 de Gaston): un operador puede tener 2+ servicios
        // cancelados en el mismo evento (hotel + traslado del mismo operador). El reintegro es UN monto agregado, no
        // "por servicio". Por eso usamos Where(...).ToList() y NUNCA SingleOrDefault: con 2+ lineas del mismo operador
        // SingleOrDefault tiraria InvalidOperationException (500 no controlado).
        var operatorLines = bc.Lines
            .Where(l => l.SupplierId == refundSupplierId)
            .ToList();
        if (operatorLines.Count == 0)
        {
            throw new BusinessInvariantViolationException(
                "El proveedor del reintegro no corresponde a ninguna linea de esta cancelacion.",
                invariantCode: "INV-126");
        }

        // INV-118 (M-B, ADR-025): coherencia de moneda del refund contra la moneda de las LINEAS del operador que
        // recibe el reintegro, NO contra el FiscalSnapshot del evento. Por que: la cara fiscal hacia el cliente
        // (FiscalSnapshot) es UNA en la moneda de la factura de venta, pero cada operador cobra/devuelve en SU moneda.
        // En multi-operador con monedas mixtas (hotel USD + aereo ARS), validar contra el snapshot rechazaria/aceptaria
        // en la moneda equivocada. La moneda real del circuito de proveedor vive en la linea. Caso mono-operador
        // (1 linea, misma moneda que el snapshot): byte-equivalente al check historico.
        var operatorLineCurrencies = operatorLines
            .Select(l => l.Currency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool refundCurrencyMatchesAnyLine = operatorLineCurrencies
            .Any(c => string.Equals(c, refundCurrency, StringComparison.OrdinalIgnoreCase));
        if (!refundCurrencyMatchesAnyLine)
        {
            throw new BusinessInvariantViolationException(
                $"La moneda del reintegro ({refundCurrency}) no coincide con la moneda de los " +
                $"servicios del operador en esta cancelación ({string.Join("/", operatorLineCurrencies)}). " +
                "Revisá que el ingreso del operador sea el correcto.",
                invariantCode: "INV-118");
        }

        return operatorLines;
    }

    /// <summary>
    /// Valida la matriz Agencia × Operador × Deducciones segun el snapshot
    /// fiscal capturado al confirmar el BC. Las reglas vivien aca porque son
    /// runtime (no se pueden expresar como CHECK SQL sin acoplar dos tablas).
    /// </summary>
    private static void ValidateFiscalMatrix(
        string? agencyTaxConditionAtEvent,
        string? supplierTaxConditionAtEvent,
        IReadOnlyList<DeductionLineRequest> deductions)
    {
        var agencyCanonical = TaxConditionNormalizer.Normalize(agencyTaxConditionAtEvent);
        var supplierCanonical = TaxConditionNormalizer.Normalize(supplierTaxConditionAtEvent);

        // INV-118: si el snapshot quedo incoherente algo se rompio en Confirm.
        if (agencyCanonical == TaxConditionCanonical.Unknown ||
            supplierCanonical == TaxConditionCanonical.Unknown)
        {
            throw new BusinessInvariantViolationException(
                "No se pudo determinar la condición fiscal de la agencia o del operador. " +
                "No se pueden calcular las retenciones impositivas.",
                invariantCode: "INV-118");
        }

        // Kinds 10..39 = retenciones impositivas AR (IVA + Ganancias + IIBB).
        // Forzamos rechazo cuando Agency o Supplier es Monotributo.
        foreach (var d in deductions)
        {
            var kindNumber = (int)d.Kind;
            var isArgentineWithholding = kindNumber >= 10 && kindNumber <= 39;

            if (!isArgentineWithholding) continue;

            if (supplierCanonical == TaxConditionCanonical.Monotributista)
            {
                // INV-105: operador Mono no esta inscripto en el regimen — registrar
                // una retencion suya seria invent un credito fiscal inexistente.
                throw new BusinessInvariantViolationException(
                    "El operador es Monotributo: no se pueden registrar retenciones impositivas argentinas sobre su reintegro. " +
                    "Usá una deducción de gasto administrativo, costo bancario u otro concepto, con justificación.",
                    invariantCode: "INV-105");
            }

            if (agencyCanonical == TaxConditionCanonical.Monotributista)
            {
                // INV-115: agencia Mono NO genera credito fiscal IVA, asentar una
                // retencion seria "regalarsela" al operador sin contrapartida.
                throw new BusinessInvariantViolationException(
                    "La agencia es Monotributo: no se pueden registrar retenciones impositivas argentinas. " +
                    "El Monotributo no genera crédito fiscal de IVA.",
                    invariantCode: "INV-115");
            }
        }
    }

    // =========================================================================
    // VoidAllocationAsync
    // =========================================================================

    public async Task<OperatorRefundAllocationDto> VoidAllocationAsync(
        Guid allocationPublicId,
        VoidAllocationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 20)
        {
            throw new ArgumentException("Reason debe tener al menos 20 chars.", nameof(request));
        }

        // Retry envolvente: si el cap-update del refund pelea con otra tx.
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            try
            {
                return await TryVoidOnceAsync(allocationPublicId, request, userId, userName, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "VoidAllocationAsync concurrency conflict on attempt {Attempt}/{Max} for allocation {AllocationPublicId}. Retrying.",
                    attempt + 1, MaxConcurrencyRetries, allocationPublicId);

                _db.ChangeTracker.Clear();

                if (attempt == MaxConcurrencyRetries - 1)
                {
                    throw;
                }

                await Task.Delay((int)Math.Pow(4, attempt) * 100, ct);
            }
        }

        throw new InvalidOperationException("VoidAllocationAsync retry loop exhausted.");
    }

    private async Task<OperatorRefundAllocationDto> TryVoidOnceAsync(
        Guid allocationPublicId,
        VoidAllocationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // 1) Cargar allocation + refund + bc + entries + withdrawals
        //    (Include profundo porque validamos que NO se haya consumido el
        //    saldo del cliente antes de permitir void).
        var allocation = await _db.OperatorRefundAllocations
            .Include(a => a.Refund)
            .Include(a => a.BookingCancellation).ThenInclude(b => b.Lines) // MENOR 1: revertir la imputacion por linea
            .Include(a => a.Deductions)
            .FirstOrDefaultAsync(a => a.PublicId == allocationPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"OperatorRefundAllocation {allocationPublicId} no encontrada.");

        // 2) Idempotencia + double-void: si ya esta voided rechazamos con 409.
        //    A diferencia del Force ARCA (que es no-op), aca el cliente esta
        //    intentando una operacion que no tiene efecto y conviene avisar.
        if (allocation.IsVoided)
        {
            throw new BusinessInvariantViolationException(
                "La allocation ya esta anulada. No se puede anular dos veces.",
                invariantCode: "INV-093");
        }

        // 3) Buscar ClientCreditEntry asociado (por OperatorRefundAllocationId).
        //    Si tiene withdrawals consumidos (kind != KeptAsCredit), rechazamos:
        //    no podemos "deshacer" plata que ya salio de caja sin un Reversal
        //    explicito (FC1.2.3).
        var creditEntry = await _db.ClientCreditEntries
            .Include(c => c.Withdrawals)
            .FirstOrDefaultAsync(c => c.OperatorRefundAllocationId == allocation.Id, ct);

        if (creditEntry != null)
        {
            var hasConsumedWithdrawals = creditEntry.Withdrawals
                .Any(w => w.Kind != WithdrawalKind.KeptAsCredit);
            if (hasConsumedWithdrawals)
            {
                // Mensaje en criollo, sin nombres de clase internos (gate de exposicion de datos): el saldo a
                // favor que generó este reembolso ya salió de caja (retiro en efectivo/transferencia) o se
                // aplicó a otra reserva. No se puede simplemente "deshacer" ese movimiento: primero hay que
                // revertirlo con autorización (circuito de Tesorería), y recién ahí anular el reembolso.
                throw new InvalidOperationException(
                    "No se puede anular este reembolso: el saldo a favor que generó ya fue retirado o " +
                    "aplicado por el cliente. Para deshacerlo primero hay que revertir ese uso del saldo, " +
                    "lo cual requiere autorización — consultá con Tesorería.");
            }

            // El entry queda con balance cero + IsFullyConsumed = true (asi no
            // aparece como saldo activo). NO eliminamos la fila: audit trail.
            creditEntry.RemainingBalance = 0m;
            creditEntry.IsFullyConsumed = true;
        }

        // 4) Marcar soft-void + metadata humana del actor.
        allocation.IsVoided = true;
        allocation.VoidedAt = DateTime.UtcNow;
        allocation.VoidedByUserId = userId;
        allocation.VoidedReason = request.Reason.Trim();

        // 4-bis) ADR-044 T3b Decision 3 (M4, 2026-07-10): si esta allocation ya tenia un ajuste de diferencia de
        // cambio VIGENTE (Decision 3), queda superseded (historia intacta, NO se borra). Si mas adelante llega
        // una allocation de reemplazo (correccion), el motor calcula una fila nueva sola al procesarla — no hay
        // reemplazo automatico aca porque anular no implica que venga una nueva liquidacion.
        await TreasuryFxAdjustmentEngine.SupersedeForVoidedOriginAsync(
            _db, ct, voidedOperatorRefundAllocationId: allocation.Id);

        // 5) Liberar el cap del refund con el NETO (NO el gross).
        //    Espejo de paso 9 de AllocateAsync: el cap se incrementa con
        //    NetAmount, asi que tambien se libera con NetAmount. Si decrementaramos
        //    con GrossAmount, el AllocatedAmount podria quedar negativo (rompiendo
        //    el CHECK SQL: AllocatedAmount >= 0) cuando la allocation tenia
        //    deducciones.
        allocation.Refund.AllocatedAmount -= allocation.NetAmount;

        // 6) Ajustar el denormalizado del BC.
        allocation.BookingCancellation.ReceivedRefundAmount -= allocation.NetAmount;

        // 6-bis) MENOR 1 (review 2026-06-28): simetria con el allocate. Al imputar,
        //        DistributeReceivedRefundToOperatorLines sumo el neto a las lineas del operador; al anular hay que
        //        restarlo de las MISMAS lineas (mismo set: las del SupplierId del refund), sino el estimado por
        //        linea del read-model "esperando reembolso" queda subestimado. Mismo criterio de set que el allocate
        //        (todas las lineas del operador, sin filtrar por moneda — Distribute tampoco filtra).
        var operatorLines = allocation.BookingCancellation.Lines
            .Where(l => l.SupplierId == allocation.Refund.SupplierId)
            .ToList();
        RemoveReceivedRefundFromOperatorLines(operatorLines, allocation.NetAmount);

        // 7) Notificar al BC service para que evalue revertir el Status si
        //    no quedan mas allocations activas.
        await _bcService.OnAllocationVoidedAsync(
            allocation.BookingCancellationId,
            allocation.NetAmount,
            ct);

        // 8) Audit con metadata previa al cambio (capturada en serialize-time).
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.OperatorRefundAllocationVoided,
            entityName: AuditActions.OperatorRefundAllocationEntityName,
            entityId: allocation.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                allocationPublicId = allocation.PublicId,
                refundPublicId = allocation.Refund.PublicId,
                bcPublicId = allocation.BookingCancellation.PublicId,
                allocation.GrossAmount,
                allocation.NetAmount,
                reason = allocation.VoidedReason,
                voidedByUserId = userId,
                refundAllocatedAmountAfter = allocation.Refund.AllocatedAmount,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        // 9) SaveChanges unico.
        await _db.SaveChangesAsync(ct);

        // Pasos B/C (2026-06-29): anular la imputacion devolvio el receivable Y(+) y bajo el reembolso recibido(-)
        // del operador en igual monto (net-neutral). Reconciliamos el pool con el estado YA committed. Misma
        // transaccion del caller.
        await ReconcileSupplierCreditPoolAsync(allocation.Refund.SupplierId, userId, userName, ct);

        return MapAllocation(allocation);
    }

    // =========================================================================
    // ReassociateAllocationAsync
    // =========================================================================

    public async Task<OperatorRefundAllocationDto> ReassociateAllocationAsync(
        Guid allocationPublicId,
        ReassociateAllocationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 20)
        {
            throw new ArgumentException("Reason debe tener al menos 20 chars.", nameof(request));
        }

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            try
            {
                return await TryReassociateOnceAsync(allocationPublicId, request, userId, userName, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "ReassociateAllocationAsync concurrency conflict on attempt {Attempt}/{Max} for allocation {AllocationPublicId}. Retrying.",
                    attempt + 1, MaxConcurrencyRetries, allocationPublicId);

                _db.ChangeTracker.Clear();

                if (attempt == MaxConcurrencyRetries - 1)
                {
                    throw;
                }

                await Task.Delay((int)Math.Pow(4, attempt) * 100, ct);
            }
        }

        throw new InvalidOperationException("ReassociateAllocationAsync retry loop exhausted.");
    }

    private async Task<OperatorRefundAllocationDto> TryReassociateOnceAsync(
        Guid allocationPublicId,
        ReassociateAllocationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // 1) Cargar allocation vieja + refund + bc viejo.
        var oldAllocation = await _db.OperatorRefundAllocations
            .Include(a => a.Refund).ThenInclude(r => r.Supplier)
            .Include(a => a.BookingCancellation).ThenInclude(b => b.Reserva)
            .Include(a => a.Deductions)
            .FirstOrDefaultAsync(a => a.PublicId == allocationPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"OperatorRefundAllocation {allocationPublicId} no encontrada.");

        if (oldAllocation.IsVoided)
        {
            throw new BusinessInvariantViolationException(
                "La allocation ya esta anulada. No se puede reasociar.",
                invariantCode: "INV-093");
        }

        // 2) Validar que el ClientCreditEntry asociado no tenga withdrawals
        //    consumidos (mismo guard que VoidAllocation: no se puede mover
        //    plata que ya salio de caja).
        var oldCreditEntry = await _db.ClientCreditEntries
            .Include(c => c.Withdrawals)
            .FirstOrDefaultAsync(c => c.OperatorRefundAllocationId == oldAllocation.Id, ct);
        if (oldCreditEntry != null)
        {
            var hasConsumed = oldCreditEntry.Withdrawals.Any(w => w.Kind != WithdrawalKind.KeptAsCredit);
            if (hasConsumed)
            {
                // Mismo criterio criollo que TryVoidOnceAsync (ver comentario ahi): no se puede mover a otra
                // reserva un reembolso cuyo saldo a favor ya salio de caja o se aplico a otra reserva.
                throw new InvalidOperationException(
                    "No se puede reasociar este reembolso a otra reserva: el saldo a favor que generó ya " +
                    "fue retirado o aplicado por el cliente. Para reasociarlo primero hay que revertir ese " +
                    "uso del saldo, lo cual requiere autorización — consultá con Tesorería.");
            }
        }

        // 3) Cargar BC destino con su FiscalSnapshot completo.
        var newBc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .FirstOrDefaultAsync(b => b.PublicId == request.NewBookingCancellationPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"BookingCancellation destino {request.NewBookingCancellationPublicId} no encontrado.");

        var validStates = new[]
        {
            BookingCancellationStatus.AwaitingOperatorRefund,
            BookingCancellationStatus.ClientCreditApplied,
        };
        if (!validStates.Contains(newBc.Status))
        {
            throw new BusinessInvariantViolationException(
                "La cancelación de destino no está en condiciones de recibir este reembolso en este momento.",
                invariantCode: "INV-093");
        }

        // 4) Validar moneda del refund con el snapshot del BC nuevo (puede ser
        //    distinto al viejo BC si vienen de FiscalSnapshots diferentes).
        // Resolvemos la moneda de destino en una variable local para que el mensaje al usuario no
        // exponga el camino interno (FiscalSnapshot.CurrencyAtEvent): solo mostramos el codigo de moneda.
        var destinationCurrency = newBc.FiscalSnapshot?.CurrencyAtEvent;
        if (!string.Equals(
                oldAllocation.Refund.Currency,
                destinationCurrency,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessInvariantViolationException(
                $"La moneda del reintegro ({oldAllocation.Refund.Currency}) no coincide con la de la " +
                $"cancelación de destino ({destinationCurrency ?? "sin definir"}).",
                invariantCode: "INV-118");
        }

        // 5) Recomputar matriz fiscal con el snapshot del BC destino.
        //    Las deducciones existentes podrian no ser validas en el nuevo
        //    contexto (Agency/Supplier distintos). Las re-mapeamos desde la
        //    allocation original.
        var deductionsAsRequest = oldAllocation.Deductions
            .Select(d => new DeductionLineRequest(
                Kind: d.Kind,
                Amount: d.Amount,
                Description: d.Description,
                CertificateNumber: d.CertificateNumber,
                CertificateDate: d.CertificateDate,
                CertificatePdfUrl: d.CertificatePdfUrl,
                Jurisdiction: d.Jurisdiction,
                ForeignCountryCode: d.ForeignCountryCode,
                SupportingDocumentRef: d.SupportingDocumentRef,
                JustificationComment: d.JustificationComment,
                MissingFiscalSupport: d.MissingFiscalSupport,
                Comment: d.Comment,
                RequiresAccountingReview: d.RequiresAccountingReview))
            .ToList();
        ValidateFiscalMatrix(
            agencyTaxConditionAtEvent: newBc.FiscalSnapshot?.AgencyTaxConditionAtEvent,
            supplierTaxConditionAtEvent: newBc.FiscalSnapshot?.SupplierTaxConditionAtEvent,
            deductions: deductionsAsRequest);

        // 6) Soft-void de la vieja + ajustes contables del BC viejo y refund.
        var oldBcId = oldAllocation.BookingCancellationId;
        oldAllocation.IsVoided = true;
        oldAllocation.VoidedAt = DateTime.UtcNow;
        oldAllocation.VoidedByUserId = userId;
        oldAllocation.VoidedReason = $"Reassociate: {request.Reason.Trim()}";

        // ADR-044 T3b Decision 3 (M4, K1, 2026-07-10): la reasociacion SI es un reemplazo (a diferencia del void
        // PURO de VoidAllocationAsync, que anula sin sustituto): la MISMA plata pasa a liquidar el/los cargo(s)
        // del BC NUEVO. Aca marcamos superseded el/los ajuste(s) FX vigente(s) de la vieja allocation; mas abajo,
        // tras crear la allocation nueva, registramos el/los ajuste(s) de la liquidacion NUEVA y los enlazamos
        // (LinkSupersededTo) para dejar la cadena de trazabilidad old -> new.
        var supersededFxAdjustments = await TreasuryFxAdjustmentEngine.SupersedeForVoidedOriginAsync(
            _db, ct, voidedOperatorRefundAllocationId: oldAllocation.Id);

        // Liberar cap del refund con NetAmount (espejo de AllocateAsync).
        // Ver explicacion en paso 9 de TryAllocateOnceAsync: el cap acumula netos,
        // por lo tanto se libera con neto.
        oldAllocation.Refund.AllocatedAmount -= oldAllocation.NetAmount;
        oldAllocation.BookingCancellation.ReceivedRefundAmount -= oldAllocation.NetAmount;

        if (oldCreditEntry != null)
        {
            oldCreditEntry.RemainingBalance = 0m;
            oldCreditEntry.IsFullyConsumed = true;
        }

        await _bcService.OnAllocationVoidedAsync(oldBcId, oldAllocation.NetAmount, ct);

        // 7) Crear allocation nueva apuntando al BC nuevo. Misma estructura
        //    economica que la vieja, snapshot fiscal recalculado del destino.
        var newAllocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = oldAllocation.OperatorRefundReceivedId,
            Refund = oldAllocation.Refund,
            BookingCancellationId = newBc.Id,
            BookingCancellation = newBc,
            GrossAmount = oldAllocation.GrossAmount,
            NetAmount = oldAllocation.NetAmount,
            IsVoided = false,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = userId,
            VoidsAllocationId = oldAllocation.Id, // FK retro: trazabilidad
        };

        foreach (var d in oldAllocation.Deductions)
        {
            newAllocation.Deductions.Add(new DeductionLine
            {
                Kind = d.Kind,
                Amount = d.Amount,
                CertificateNumber = d.CertificateNumber,
                CertificateDate = d.CertificateDate,
                CertificatePdfUrl = d.CertificatePdfUrl,
                Jurisdiction = d.Jurisdiction,
                ForeignCountryCode = d.ForeignCountryCode,
                Description = d.Description,
                SupportingDocumentRef = d.SupportingDocumentRef,
                JustificationComment = d.JustificationComment,
                MissingFiscalSupport = d.MissingFiscalSupport,
                Comment = d.Comment,
                RequiresAccountingReview = d.RequiresAccountingReview,
                Allocation = newAllocation,
            });
        }

        _db.OperatorRefundAllocations.Add(newAllocation);
        // Incrementar cap del refund con NetAmount de la NUEVA allocation
        // (mismo principio que AllocateAsync paso 9: cap acumula netos).
        oldAllocation.Refund.AllocatedAmount += newAllocation.NetAmount;
        newBc.ReceivedRefundAmount += newAllocation.NetAmount;

        // 8) Crear ClientCreditEntry nuevo para el cliente del BC nuevo.
        //    Pasamos la entidad y no el Id por el mismo motivo que en AllocateAsync:
        //    newAllocation.Id == 0 hasta el SaveChanges final, asi que setear el
        //    Id ahora rompe la FK. La navigation property hace que EF resuelva la
        //    FK al persistir en orden topologico.
        await _clientCreditService.CreateEntryAsync(
            bookingCancellationId: newBc.Id,
            operatorRefundAllocation: newAllocation,
            customerId: newBc.CustomerId,
            netAmount: newAllocation.NetAmount,
            currency: oldAllocation.Refund.Currency,
            userId: userId,
            userName: userName,
            ct: ct);

        // 9) Notificar al BC service nuevo.
        await _bcService.OnAllocationRecordedAsync(newBc.Id, newAllocation.NetAmount, ct);

        // 9-bis) ADR-044 T3b Decision 3 (K1, 2026-07-10): registrar el ajuste de diferencia de cambio de la
        // liquidacion NUEVA (los cargos Retenida del BC nuevo cuya ND ya salio con conversion) y enlazar cada
        // fila nueva con la vieja superseded correspondiente. El enlace es best-effort posicional: en el caso
        // comun hay 1 vieja + 1 nueva (un cargo). Si las cantidades no coinciden (multi-cargo con distinta
        // elegibilidad entre el BC viejo y el nuevo), la supersede vieja igual quedo marcada y la nueva igual se
        // registra; solo no se dibuja la flecha old->new (trazabilidad parcial, nunca dato incorrecto).
        var newFxAdjustments = await TreasuryFxAdjustmentEngine.RegisterForRetainedChargesAsync(
            _db, newAllocation, _logger, ct);
        if (supersededFxAdjustments.Count > 0 && supersededFxAdjustments.Count == newFxAdjustments.Count)
        {
            for (int i = 0; i < newFxAdjustments.Count; i++)
                TreasuryFxAdjustmentEngine.LinkSupersededTo(supersededFxAdjustments[i], newFxAdjustments[i]);
        }

        // 10) Audit UNICO "Reassociated" (no doble Void+Allocated) para que la
        //     historia se lea como evento atomico.
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.OperatorRefundAllocationReassociated,
            entityName: AuditActions.OperatorRefundAllocationEntityName,
            entityId: oldAllocation.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                oldAllocationPublicId = oldAllocation.PublicId,
                newAllocationPublicId = newAllocation.PublicId,
                refundPublicId = oldAllocation.Refund.PublicId,
                fromBcId = oldBcId,
                fromBcPublicId = oldAllocation.BookingCancellation.PublicId,
                toBcId = newBc.Id,
                toBcPublicId = newBc.PublicId,
                netAmount = newAllocation.NetAmount,
                grossAmount = newAllocation.GrossAmount,
                reason = request.Reason.Trim(),
                actorUserId = userId,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        return MapAllocation(newAllocation);
    }

    // =========================================================================
    // Queries
    // =========================================================================

    public async Task<OperatorRefundReceivedDto?> GetByPublicIdAsync(
        Guid publicId,
        CancellationToken ct)
    {
        var refund = await _db.OperatorRefundReceived
            .AsNoTracking()
            .Include(r => r.Supplier)
            .Include(r => r.Allocations).ThenInclude(a => a.BookingCancellation)
            .Include(r => r.Allocations).ThenInclude(a => a.Deductions)
            .FirstOrDefaultAsync(r => r.PublicId == publicId, ct);

        return refund is null ? null : MapRefund(refund);
    }

    // =========================================================================
    // Helpers privados
    // =========================================================================

    private async Task EnsureFeatureFlagOnAsync(CancellationToken ct)
    {
        var settings = await _settings.GetEntityAsync(ct);
        if (!settings.EnableNewCancellationFlow)
        {
            throw new InvalidOperationException(
                "El módulo de cancelaciones no está disponible en este momento.");
        }
    }

    private static OperatorRefundReceivedDto MapRefund(OperatorRefundReceived refund)
    {
        return new OperatorRefundReceivedDto
        {
            PublicId = refund.PublicId,
            SupplierPublicId = refund.Supplier?.PublicId ?? Guid.Empty,
            SupplierName = refund.Supplier?.Name ?? string.Empty,
            ReceivedAmount = refund.ReceivedAmount,
            AllocatedAmount = refund.AllocatedAmount,
            RemainingCap = refund.ReceivedAmount - refund.AllocatedAmount,
            Currency = refund.Currency,
            ReceivedAt = refund.ReceivedAt,
            Method = refund.Method,
            Reference = refund.Reference,
            ReceivedByUserId = refund.ReceivedByUserId,
            ReceivedByUserName = refund.ReceivedByUserName,
            Allocations = refund.Allocations?
                .Select(a => MapAllocation(a, refundPublicIdFallback: refund.PublicId))
                .ToList() ?? new List<OperatorRefundAllocationDto>(),
        };
    }

    private static OperatorRefundAllocationDto MapAllocation(
        OperatorRefundAllocation a,
        Guid? refundPublicIdFallback = null)
    {
        return new OperatorRefundAllocationDto
        {
            PublicId = a.PublicId,
            RefundPublicId = a.Refund?.PublicId ?? refundPublicIdFallback ?? Guid.Empty,
            BookingCancellationPublicId = a.BookingCancellation?.PublicId ?? Guid.Empty,
            GrossAmount = a.GrossAmount,
            NetAmount = a.NetAmount,
            IsVoided = a.IsVoided,
            VoidedAt = a.VoidedAt,
            VoidedByUserId = a.VoidedByUserId,
            VoidedReason = a.VoidedReason,
            CreatedAt = a.CreatedAt,
            CreatedByUserId = a.CreatedByUserId,
            Deductions = a.Deductions?.Select(MapDeduction).ToList() ?? new List<DeductionLineDto>(),
        };
    }

    private static DeductionLineDto MapDeduction(DeductionLine d)
    {
        return new DeductionLineDto
        {
            PublicId = d.PublicId,
            Kind = d.Kind,
            Amount = d.Amount,
            Description = d.Description,
            CertificateNumber = d.CertificateNumber,
            CertificateDate = d.CertificateDate,
            CertificatePdfUrl = d.CertificatePdfUrl,
            Jurisdiction = d.Jurisdiction,
            ForeignCountryCode = d.ForeignCountryCode,
            SupportingDocumentRef = d.SupportingDocumentRef,
            JustificationComment = d.JustificationComment,
            MissingFiscalSupport = d.MissingFiscalSupport,
            Comment = d.Comment,
            RequiresAccountingReview = d.RequiresAccountingReview,
        };
    }
}
