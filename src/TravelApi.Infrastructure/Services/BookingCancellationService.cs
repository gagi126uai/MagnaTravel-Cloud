using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Helpers;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations; // SupplierCancellationCircuitReader (fuente unica del receivable del operador)
using TravelApi.Infrastructure.Services.Reservations; // MutationGuards (candado fiscal CAE/voucher, SEC-B1)

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
    // SEC-B1b (ADR-025): cbteTipo de las Notas de Credito (3=A, 8=B, 13=C, 53=M). Se usa para EXCLUIR las NC
    // al buscar la factura de VENTA viva que ancla el BC de la cancelacion parcial. Espejo de
    // MutationGuards.LiveInvoiceCreditNoteTypes (EF Core no traduce InvoiceComprobanteHelpers.IsCreditNote a
    // SQL); mantener ambos sincronizados si se agrega un tipo de NC.
    private static readonly int[] LiveInvoiceCreditNoteTypes = { 3, 8, 13, 53 };

    // cbteTipo de las Notas de Debito (2=A, 7=B, 12=C, 52=M). Fuente autoritativa:
    // InvoiceComprobanteHelpers.IsDebitNote / GetDebitNoteTypeForAssociated (ADR-013 §3.9).
    // Se usa junto con LiveInvoiceCreditNoteTypes para dejar SOLO facturas de VENTA vivas al
    // decidir INV-100 y elegir la factura originante del BC: una reserva puede tener, ademas de su
    // factura de venta, una NC (anulacion previa) y/o una ND (ej. multa del operador). Sin esta
    // exclusion, esas notas se contaban como "facturas" y disparaban un INV-100 FALSO (bug 2026-07-01),
    // ademas de que la nota mas reciente por CreatedAt podia quedar como originatingInvoice en vez de la
    // factura de venta. EF Core no traduce InvoiceComprobanteHelpers.IsDebitNote a SQL, por eso se expande
    // inline; mantener sincronizado con el helper si se agrega un tipo de ND.
    private static readonly int[] LiveInvoiceDebitNoteTypes = { 2, 7, 12, 52 };

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

    // ADR-042 N10 fix (2026-07-02): el pre-check de approval multi-factura debe resolver "¿requiere approval?"
    // EXACTAMENTE como InvoiceService.EnqueueAnnulmentAsync — via la ApprovalPolicy configurable (B1.15 Fase B'',
    // 2026-05-11), NO el setting global viejo (deprecado). Opcional en el ctor (default null) para no romper los
    // tests unit/integration que construyen el service a mano: si no se inyecta, se cae al fallback del setting
    // (mismo comportamiento que InvoiceService cuando la policy no esta presente).
    private readonly IApprovalPolicyService? _approvalPolicyService;

    public BookingCancellationService(
        AppDbContext db,
        IInvoiceService invoiceService,
        IApprovalRequestService approvalService,
        IAuditService auditService,
        ILogger<BookingCancellationService> logger,
        IOperationalFinanceSettingsService settings,
        IFiscalLiquidationCalculator calculator,
        IAdminUserCountService adminUserCount,
        IFourEyesBypassEvaluator? fourEyesBypassEvaluator = null,
        IApprovalPolicyService? approvalPolicyService = null)
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
        _approvalPolicyService = approvalPolicyService;
    }

    // =========================================================================
    // Comandos publicos (IBookingCancellationService)
    // =========================================================================
    //
    // NOTA (2026-07-04): los ex-helpers privados LogReservaStatusChange (rastro auditable) y
    // DiscardUnacknowledgedChangesOnCancellationAsync (descarte de la marca "confirmada con cambios") MURIERON:
    // sus dos responsabilidades quedaron absorbidas por el PUNTO ÚNICO de transición
    // (TravelApi.Infrastructure.Reservations.ReservaStatusTransitioner.ApplyAsync), que ahora escribe el log y
    // limpia las marcas de revisión de forma consistente en TODOS los cambios de Reserva.Status del sistema.

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

        // 2) Localizar la FACTURA DE VENTA activa de la reserva. Usamos la mas
        //    reciente no anulada. Si <c>OnePerReservaInvoicePolicy</c> esta on y hay
        //    multiples activas: rechazar con INV-100 (review BR4 — el patron
        //    de FC1 deja una Invoice por reserva en estado normal).
        //
        //    Se EXCLUYEN Notas de Credito (LiveInvoiceCreditNoteTypes) y Notas de
        //    Debito (LiveInvoiceDebitNoteTypes): NO son facturas de venta. Contarlas
        //    disparaba un INV-100 falso cuando la reserva tenia 1 factura + su NC/ND
        //    (bug 2026-07-01), y podia elegir la nota como originatingInvoice.
        //
        //    Se EXIGE ademas CAE emitido (!string.IsNullOrEmpty(i.CAE)): una fila de
        //    venta encolada (PENDING) o RECHAZADA por ARCA (Resultado="R", CAE=null) NO
        //    es una factura fiscal real (nunca tuvo CAE). Sin este filtro, un intento
        //    de emision fallido/reintento dejaba una fila fantasma que la cuenta sumaba
        //    y disparaba un INV-100 falso al anular (bug 2026-07-01). Se usa el MISMO
        //    criterio "CAE presente" que los sitios hermanos que significan "factura de
        //    venta viva": hasLiveInvoice (mas abajo en esta clase), originatingInvoice de
        //    la cancelacion parcial, y MutationGuards. Con CAE presente el
        //    originatingInvoice siempre es una factura emitida (lo que el flujo de NC
        //    necesita aguas abajo). NOTA: se mantiene deliberadamente el disparo de
        //    INV-100 cuando hay DOS facturas de venta CON CAE reales (caso multimoneda
        //    legitimo USD+ARS): ADR-042 lo levanta (ver mas abajo).
        var activeInvoices = await _db.Invoices
            .Where(i => i.ReservaId == reserva.Id
                     && i.AnnulmentStatus != AnnulmentStatus.Succeeded
                     && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante) // excluye NC
                     && !LiveInvoiceDebitNoteTypes.Contains(i.TipoComprobante)  // excluye ND
                     && !string.IsNullOrEmpty(i.CAE))                           // excluye fila fantasma (encolada/rechazada, sin CAE)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        if (activeInvoices.Count == 0)
            throw new InvalidOperationException(
                $"La reserva {reserva.NumeroReserva} no tiene factura activa para anular.");

        // ADR-042 (2026-07-01): se LEVANTA el freno INV-100 para el caso multi-factura CON CAE (varias
        // facturas de venta vivas, ej. una USD + una ARS). Al anular se emite UNA NC por factura, cada una
        // en su moneda; la completitud se maneja con las filas hijas BookingCancellationCreditNote. El
        // caso verdaderamente ambiguo (ninguna factura) ya se rechazo arriba (activeInvoices.Count == 0).
        // El puntero PRINCIPAL (bc.OriginatingInvoiceId) queda en la factura mas reciente por CreatedAt
        // (criterio ya vigente); las demas viajan como hijas al confirmar.
        var originatingInvoice = activeInvoices[0];

        // 3) INV-081: una sola cancelacion ACTIVA por reserva.
        //
        //    REGLA REAL (no "una fila por reserva"): INV-081 protege contra DOS
        //    cancelaciones ACTIVAS simultaneas sobre la misma reserva. Una fila
        //    muerta (un draft que nunca llego a confirmarse, o un abort) NO es una
        //    cancelacion activa y NO debe trabar la reserva para siempre.
        //
        //    Bug que arregla (B1, 2026-06-03): el front hace draft -> confirm en
        //    dos llamadas. Si el confirm falla por red/AFIP/500, quedaba un BC
        //    huerfano y la reserva NO se podia volver a cancelar nunca mas (ni con
        //    abort, porque AbortAsync solo MARCA Status=Aborted, no borra la fila,
        //    y tanto este check como el UNIQUE total de la BD seguian viendo la fila).
        //
        //    REGLA MENTAL CENTRAL (fiscal): un BC es LIBERABLE para re-cancelar SOLO
        //    si no dejo ninguna nota de credito viva (CAE aprobado). Si dejo una NC
        //    viva, liberarlo arriesga una SEGUNDA NC sobre la misma factura =
        //    incidente fiscal grave. Ante la duda, NO liberamos (rechazamos).
        //
        //    Resolucion de la fila existente (ver TryResolveExistingBcAsync):
        //      a) Draft "puro" (Drafted + sin NC + sin ND) -> REUSAR esa misma fila
        //         (reintento idempotente del confirm).
        //      b) Aborted -> el vendedor abandono ese intento; creamos un BC NUEVO.
        //      c) ArcaRejected SIN NC viva (CreditNoteInvoiceId null) -> AFIP rechazo
        //         la NC, no quedo comprobante vivo -> auto-abortamos esa fila y creamos
        //         un BC NUEVO (el vendedor corrige el dato y reintenta desde el modal).
        //      d) Cualquier otro estado (AwaitingFiscalConfirmation, ClientCreditApplied,
        //         Closed, AbandonedByOperator, ManualReview*, o un ArcaRejected que por
        //         algun camino tuviera NC viva) -> rechazo INV-081.
        //
        //    El UNIQUE parcial de la BD (migracion B1_AddBookingCancellationPartialUniqueIndexes)
        //    excluye Status=6 (Aborted), por eso tras (b)/(c) el INSERT de mas abajo
        //    no colisiona: en (c) primero movemos la fila vieja a Aborted=6.
        var reuseDto = await TryResolveExistingBcAsync(reserva, userId, userName, request.Reason, ct);
        if (reuseDto is not null)
            return reuseDto; // caso (a): devolvimos el draft existente (con el motivo actualizado si cambio).

        // PreviousBcPublicId: si hubo una fila previa liberada (Aborted preexistente
        // o ArcaRejected auto-abortada), guardamos su PublicId para registrar el
        // linaje de intentos en la auditoria del BC nuevo (FIX 3). Null si es el
        // primer intento de cancelacion de la reserva.
        var previousBcPublicId = await ResolvePreviousBcPublicIdAsync(reserva.Id, ct);

        // 4) MIG2 (plan v3): si la Invoice original ya esta anulada (Succeeded),
        //    no tiene sentido cancelar.
        if (originatingInvoice.AnnulmentStatus == AnnulmentStatus.Succeeded)
            throw new BusinessInvariantViolationException(
                "La factura original ya fue anulada (NC aprobada). No se puede cancelar la reserva sobre una factura muerta.",
                invariantCode: "INV-100");

        // 5) Inferir Customer y Supplier:
        //    - Customer: el Payer de la reserva.
        //    - Supplier: lo deducimos del conjunto de operadores DISTINTOS que
        //      aparecen en TODOS los servicios de la reserva (ver InferSingleSupplierIdAsync).
        if (reserva.PayerId is null)
            throw new InvalidOperationException(
                $"La reserva {reserva.NumeroReserva} no tiene Payer asignado. No se puede crear cancelacion.");

        // ADR-025: levanta INV-152. En vez de exigir UN solo operador, construimos UNA LINEA por
        // servicio/operador de la reserva (Scope=Full: es una cancelacion TOTAL del file). La cara
        // fiscal hacia el cliente sigue siendo UNICA (factura/NC en el padre); la multiplicidad de
        // operadores vive en las lineas. El operador "principal" del BC (bc.SupplierId, denormalizado
        // para compat fiscal: regimen Mono/RI de la NC) es el de la primera linea.
        var cancellationLines = await BuildCancellationLinesAsync(reserva, BookingCancellationLineScope.Full, ct);
        var supplierId = cancellationLines[0].SupplierId;

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
            SupplierId = supplierId,
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

        // ADR-025: adjuntar las lineas (una por servicio/operador). EF las inserta en cascada con el
        // padre en el mismo SaveChanges. El path mono-operador queda con 1 linea (byte-equivalente).
        foreach (var line in cancellationLines)
            bc.Lines.Add(line);

        _db.BookingCancellations.Add(bc);

        // FIX 2 (B1, 2026-06-03): el INSERT puede colisionar bajo concurrencia.
        // Dos requests (doble click, retry de red, dos vendedores) pueden pasar el
        // SELECT de existingBc viendo null/Aborted y ambos intentan insertar. El
        // UNIQUE parcial de la BD serializa: uno gana, el otro recibe un 23505
        // (unique_violation) que EF envuelve en DbUpdateException. Sin este catch
        // sube como 500. Lo convertimos en una respuesta determinista: re-resolvemos
        // el BC ganador y aplicamos la MISMA politica (draft puro -> idempotente;
        // cualquier otra cosa -> INV-081 limpio). Nunca un 500.
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            _logger.LogWarning(ex,
                "DraftAsync race: el INSERT del BC para la reserva {ReservaPublicId} colisiono con el " +
                "UNIQUE parcial (otro request gano la carrera). Re-resolvemos el BC ganador.",
                reserva.PublicId);

            // Sacamos del ChangeTracker la entidad perdedora: quedo en estado Added
            // y un proximo SaveChanges la reintentaria. EF Detached la descarta.
            _db.Entry(bc).State = EntityState.Detached;

            var winnerDto = await TryResolveExistingBcAsync(reserva, userId, userName, request.Reason, ct);
            if (winnerDto is not null)
                return winnerDto; // el ganador quedo en Drafted puro -> idempotencia real.

            // El ganador no es reusable (paso a Confirm, o cualquier estado fiscal) ->
            // INV-081 limpio, no 500. TryResolveExistingBcAsync ya tira INV-081 para
            // los estados no liberables; si llegamos aca el ganador estaba Aborted y
            // (carrera muy improbable) volvio a desaparecer -> rechazamos conservador.
            throw new BusinessInvariantViolationException(
                $"La reserva {reserva.NumeroReserva} ya tiene una cancelacion activa creada por otra operacion simultanea.",
                invariantCode: "INV-081");
        }

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
                // Linaje de intentos (FIX 3): si esta cancelacion nace tras liberar un
                // BC previo (Aborted o ArcaRejected auto-abortado), dejamos la referencia
                // al PublicId anterior para poder reconstruir la cadena de reintentos.
                PreviousBcPublicId = previousBcPublicId,
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

    /// <summary>
    /// ADR-025 (DT.3.1): cancela UN servicio dentro de una reserva, dejando el resto del file vivo.
    /// Ver la doc del contrato en <see cref="IBookingCancellationService.CancelServiceAsync"/>.
    ///
    /// <para>Flujo: (1) validar que el servicio pertenece a la reserva (server-side, INV-151); (2) marcar
    /// el Status del servicio en cancelado + CancelledAt/By; (3) recalcular la plata de la reserva
    /// (el servicio cancelado sale del saldo solo, ADR-020); (4) <b>B1</b>: recalcular la deuda del
    /// operador de ESE servicio en la MISMA transaccion (el Status cancelado NO recalcula la deuda solo).
    /// NO mueve el estado de la reserva (decision #1) ni emite NC automatica (decision #3).</para>
    /// </summary>
    public async Task<CancelServiceResultDto> CancelServiceAsync(
        CancelServiceRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        if (!Enum.TryParse<CancellableServiceTable>(request.ServiceTable, ignoreCase: true, out var serviceTable))
            throw new ArgumentException(
                $"Tipo de servicio invalido: '{request.ServiceTable}'. " +
                "Valores validos: Generic, Flight, Hotel, Transfer, Package, Assistance.",
                nameof(request));

        var reserva = await _db.Reservas.FirstOrDefaultAsync(r => r.PublicId == request.ReservaPublicId, ct)
            ?? throw new KeyNotFoundException($"Reserva {request.ReservaPublicId} no encontrada.");

        // ADR-033 (2026-06-16, E4/B6) + G3 (2026-06-24): gate de ESTADO VIVO. Cancelar un servicio solo tiene
        // sentido si la reserva esta operativamente viva (En gestion / Confirmada). Antes este path NO validaba
        // estado (F10) y dejaba cancelar servicios en reservas pre-venta (Cotizacion/Presupuesto: nada se
        // concreto -> ahi un servicio se BORRA, no se cancela), Perdidas, ya Canceladas, Finalizadas o esperando
        // refund -> bajaba el saldo de una reserva muerta a escondidas. Se reusa ActiveCollectionStatuses
        // (operativo vivo, SIN Closed ni Traveling): una Finalizada no "des-cancela" un servicio, y En viaje no
        // se cancela (se corrige por NC/ajuste, ADR-035). Es el MISMO conjunto que la capacidad CanCancelServices
        // que el front usa para mostrar "Cancelar servicio" vs "Borrar servicio".
        // InvalidOperationException -> 409 (igual que el candado fiscal de abajo).
        if (!EstadoReserva.IsCollectableStatus(reserva.Status))
            throw new InvalidOperationException(
                ReservaCapabilityPolicy.ServiceNotCancellableStatusReason);

        // 0) ADR-044 T5 Addendum, Decision A (2026-07-11): SEC-B1 (bloqueo binario por factura viva) se
        //    REEMPLAZA por una compuerta de 3 salidas resuelta mas abajo (paso 5): con factura viva, cancelar
        //    un servicio YA NO se bloquea de punta a punta — se resuelve la nota de credito de ese servicio
        //    (a que factura, por cuanto) o queda visible/accionable para resolucion manual, pero NUNCA baja
        //    el saldo en silencio (el agujero que SEC-B1 vino a cerrar sigue cerrado, con otro mecanismo).
        //    El candado de VOUCHER emitido sigue EXACTAMENTE igual que hoy (reserva-level, sin cambios): un
        //    voucher entregado no se reescribe. InvalidOperationException -> 409 (igual que siempre).
        var voucherBlockReason = await MutationGuards.GetReservaVoucherOnlyBlockReasonAsync(_db, reserva.Id, ct);
        if (voucherBlockReason is not null)
            throw new InvalidOperationException(voucherBlockReason);

        // 0-bis) R1 (Pasos B/C, plata viva, 2026-06-30): NO cancelar un servicio PAGADO al operador si no se puede
        //    ANCLAR el receivable "me tiene que devolver". El registro del lado-operador (la linea de cancelacion
        //    con su RefundCap) cuelga de un BookingCancellation, que exige <c>OriginatingInvoiceId</c> (factura de
        //    venta viva) — es ancla ESTRUCTURAL (FK no-null + indice unico). Si la reserva todavia NO tiene factura
        //    (ADR-037 desacoplo facturar de cobrar: se le puede pagar al operador antes de facturar al cliente),
        //    cancelar dejaria la caja del operador en negativo SIN linea que represente el receivable -> el
        //    reconciler mintearia ese negativo como saldo a favor GASTABLE (no es saldo a favor: el operador te
        //    debe el reembolso). Hasta que exista la factura que ancle el receivable, BLOQUEAMOS con un mensaje
        //    claro (la accion se retoma tras facturar o gestionar el reembolso). Solo bloquea el caso con fuga real
        //    (servicio con plata pagada al operador + sin factura); un servicio impago, o con factura, sigue igual.
        await EnsurePaidServiceCancellationHasReceivableAnchorAsync(reserva, serviceTable, request.ServicePublicId, ct);

        // 0-ter) ADR-044 T5 Addendum, Decision A punto 4 (2026-07-11): bloqueo duro SOLO si hay factura de
        //    venta viva PERO la reserva no tiene Payer asignado. Antes esto se resolvia en silencio dentro de
        //    RecordPartialCancellationLineAsync (un "return" sin rastro): el servicio se cancelaba igual y la
        //    factura quedaba viva sin ningun evento de credito registrado — el mismo agujero fiscal que SEC-B1
        //    vino a cerrar. Sin Payer no hay a quien facturarle la Nota de Credito, asi que ahora se bloquea
        //    ANTES de tocar nada (409 explicito), en vez de cancelar dejando ese hueco sin rastro.
        if (reserva.PayerId is null && await ReservaHasLiveSaleInvoiceAsync(reserva.Id, ct))
            throw new InvalidOperationException(
                "No se puede cancelar este servicio: la reserva tiene una factura emitida pero no tiene un " +
                "cliente asignado para facturarle la nota de crédito. Asigná un cliente a la reserva antes de cancelar.");

        // 1) PLAN de credito (solo LECTURA): resolvemos el Id del servicio, las facturas de venta vivas y a
        //    que factura le corresponde el credito ANTES de abrir la transaccion. Asi la transaccion de mas
        //    abajo abre a lo sumo UN "FOR UPDATE" (sobre esa factura) y nunca anida BeginTransactionAsync
        //    (Npgsql rechaza transacciones anidadas). Server-side (espejo INV-151): la pertenencia del
        //    servicio a la reserva se valida aca, no se confia en el frontend.
        var creditPlan = await PlanServiceCancellationCreditAsync(reserva, serviceTable, request, ct);

        // Resultado del acto (se completa dentro de la unidad transaccional de abajo).
        bool serviceWasCancelledNow = false;
        int? affectedSupplierId = null;

        // 2-5) UNIDAD DE TRABAJO ATOMICA (ADR-044 T5, fix seguridad B1): marcar el servicio cancelado +
        //       recalcular plata/deuda + resolver la linea/ancla de la nota de credito commitean TODO JUNTO o
        //       NADA. Antes cada paso commiteaba por separado: si el ultimo fallaba, quedaba el servicio
        //       cancelado (venta bajada) con la factura viva por el total y SIN linea que respalde el credito
        //       — el mismo agujero fiscal que SEC-B1 cerraba, reaparecido por una falla a mitad de camino.
        async Task RunCancellationUnitAsync()
        {
            // Partir SIEMPRE de un ChangeTracker limpio: si la estrategia de ejecucion reintenta el cuerpo
            // ante un error transitorio, o si reintentamos por colision del primer BC (mas abajo), las
            // entidades del intento anterior NO deben quedar rastreadas (doble-add / estado stale en EF).
            _db.ChangeTracker.Clear();

            // 2) Marcar el Status cancelado del servicio + CancelledAt/By (no hace SaveChanges: lo hacemos aca).
            var (supplierId, alreadyCancelled) = await MarkTypedServiceCancelledAsync(
                serviceTable, request.ServicePublicId, reserva.Id, userId, userName, ct);
            affectedSupplierId = supplierId;
            if (alreadyCancelled)
            {
                // Idempotencia: el servicio ya estaba cancelado (doble click / reintento). Nada que hacer.
                serviceWasCancelledNow = false;
                return;
            }
            serviceWasCancelledNow = true;

            // 2-bis) Persistir el Status cancelado (participa de la transaccion externa: si algo de abajo
            //         falla, este cambio tambien se revierte).
            await _db.SaveChangesAsync(ct);

            // 3) Recalcular la plata de la reserva: el servicio cancelado sale del saldo del cliente solo
            //    (ServiceResolutionRules lo excluye). ReservaMoneyPersister hace su propio SaveChanges (que
            //    tambien participa de la transaccion externa).
            await TravelApi.Infrastructure.Reservations.ReservaMoneyPersister.PersistAsync(_db, reserva.Id, ct);

            // 4) B1 (OBLIGATORIO): recalcular la deuda del operador del servicio cancelado en la misma
            //    operacion. El cambio de Status NO recalcula la deuda solo (bug P1 del ADR-022): hay que
            //    invocar el persister explicitamente, igual que el path generico de ReservaService.
            if (supplierId.HasValue)
            {
                await TravelApi.Infrastructure.Reservations.SupplierDebtPersister.PersistAsync(_db, supplierId.Value, ct);
                await _db.SaveChangesAsync(ct);
            }

            // 5) SEC-B1b + ADR-044 T5 Addendum, Decisiones A/B (2026-07-11): dejar RASTRO y ANCLA del evento
            //    parcial. Con FACTURA VIVA (el caso normal, antes bloqueado de punta a punta por SEC-B1) la
            //    linea resuelve a que factura y por cuanto le corresponde el credito de este servicio —
            //    automatico si hay una sola factura activa de la misma moneda, capeado contra el remanente
            //    vivo de esa factura (ya estamos bajo el FOR UPDATE de esa factura, tomado por la transaccion
            //    externa). La EMISION fiscal real (la Nota de Credito) sigue un paso de confirmacion aparte
            //    (mismo patron Draft/Confirm del modulo): esta llamada deja la linea lista y VISIBLE en la
            //    bandeja "Comprobantes por resolver", nunca silenciosa, pero no dispara AFIP por si sola.
            //    Sin factura viva: comportamiento IDENTICO a hoy (RecordPartialCancellationLineAsync).
            var creditOutcome = await ApplyServiceCancellationCreditLineAsync(
                reserva, serviceTable, creditPlan, request, userId, userName, ct);

            // Auditoria de negocio (M3 del re-review): el monto y la factura destino de la decision de credito
            // quedan en el evento INMUTABLE, no solo en columnas mutables de la linea. STAGED para entrar en el
            // MISMO commit que la mutacion (si el commit revierte, no queda auditoria de un acto que no paso).
            _auditService.StageBusinessEvent(
                action: AuditActions.BookingCancellationDrafted, // reusamos la accion del modulo; el detail aclara que es parcial
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: reserva.PublicId.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    kind = "PartialServiceCancellation",
                    ReservaPublicId = reserva.PublicId,
                    request.ServiceTable,
                    request.ServicePublicId,
                    AffectedSupplierId = supplierId,
                    request.Reason,
                    TargetInvoiceId = creditOutcome.TargetInvoiceId,
                    ConfirmedGrossCreditAmount = creditOutcome.ConfirmedGrossCreditAmount,
                }),
                userId: userId,
                userName: userName);

            // SaveChanges final: la linea de credito + la auditoria stageada, en la misma transaccion.
            await _db.SaveChangesAsync(ct);

            // Pasos B/C (2026-06-29): NO disparamos el reconcile del pool aca a proposito. La cancelacion parcial
            // deja la caja del operador en negativo, pero el receivable que lo compensa lo ancla la LINEA parcial
            // (con su RefundCap). El fix de RAIZ (SupplierCancellationCircuitReader, Y atado al servicio cancelado)
            // hace que el PROXIMO reconcile de ese operador (un pago al operador, u otra cancelacion) cuente ese
            // receivable y NO mintee — sin importar que la BC siga en Drafted.
        }

        // Ejecutar la unidad de trabajo de forma ATOMICA sobre Postgres (una sola transaccion con a lo sumo un
        // FOR UPDATE) + reintento UNICO ante la colision del primer BookingCancellation de la reserva.
        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await using var tx = await _db.Database.BeginTransactionAsync(ct);

                        await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", ct);

                        // Un solo FOR UPDATE de la factura destino (si la resolvimos): serializa
                        // leer-remanente + decidir-monto + escribir contra ESA factura, sin anidar locks. Si el
                        // caso es ambiguo (2+ facturas sin eleccion) o no hay factura viva, no hay una unica
                        // factura que lockear -> transaccion sin FOR UPDATE, igual atomica.
                        if (creditPlan.TargetInvoiceId is int lockInvoiceId)
                        {
                            await _db.Database.ExecuteSqlRawAsync(
                                "SELECT 1 FROM \"Invoices\" WHERE \"Id\" = {0} FOR UPDATE",
                                new object[] { lockInvoiceId }, ct);
                        }

                        await RunCancellationUnitAsync();

                        await tx.CommitAsync(ct);
                        break;
                    }
                    catch (DbUpdateException ex) when (attempt == 0 && IsUniqueConstraintViolation(ex))
                    {
                        // Colision del PRIMER BookingCancellation de la reserva: dos cancelaciones concurrentes
                        // que son ambas "el primer servicio cancelado" chocan en el INSERT del BC (unico
                        // parcial por ReservaId). La transaccion ya revirtio TODO (Status del servicio
                        // incluido): NUNCA queda servicio-sin-linea. Reintentamos UNA vez -> el segundo
                        // GetOrCreate encuentra al BC ganador y reusa su fila (mismo patron que DraftAsync).
                        _logger.LogWarning(ex,
                            "CancelServiceAsync race: el INSERT del primer BC de la reserva {ReservaPublicId} " +
                            "colisiono con el unico parcial. Reintentamos una vez reusando el BC ganador.",
                            reserva.PublicId);
                        // El loop reintenta; ChangeTracker.Clear al inicio de RunCancellationUnitAsync limpia
                        // el grafo perdedor (el BC en estado Added que perdio la carrera).
                    }
                }
            });
        }
        else
        {
            // InMemory: no soporta FOR UPDATE ni transacciones (los tests de atomicidad/concurrencia real
            // viven en integracion Postgres). Corremos la unidad directa.
            await RunCancellationUnitAsync();
        }

        if (serviceWasCancelledNow)
        {
            _logger.LogInformation(
                "metric:partial_service_cancelled | ReservaPublicId={ReservaPublicId} ServiceTable={ServiceTable} ServicePublicId={ServicePublicId} SupplierId={SupplierId}",
                reserva.PublicId, request.ServiceTable, request.ServicePublicId, affectedSupplierId);
        }

        // Contadores para el header "N de M servicios cancelado" (decision #1: dato calculado, no estado nuevo).
        var (cancelledCount, totalWithSupplier) = await CountServicesAsync(reserva.Id, ct);

        return new CancelServiceResultDto(
            ReservaPublicId: reserva.PublicId,
            ServicePublicId: request.ServicePublicId,
            ServiceTable: request.ServiceTable,
            CancelledServicesCount: cancelledCount,
            TotalServicesWithSupplierCount: totalWithSupplier);
    }

    /// <summary>
    /// ADR-025 (DT.3.1): carga el servicio puntual por (tabla, PublicId), valida que pertenece a la
    /// reserva y marca su Status en cancelado + CancelledAt/By. Devuelve (SupplierId afectado,
    /// yaEstabaCancelado). NO llama a SaveChanges: lo hace el caller.
    ///
    /// <para>El aereo se cancela con un codigo IATA de cancelacion ("UN"): su Status mapea por codigo
    /// IATA (UN/UC/HX/NO = cancelado), no por texto generico, asi que poner "Cancelado" literal NO lo
    /// sacaria del saldo ni de la deuda. El resto de los tipos usan el literal "Cancelado".</para>
    /// </summary>
    private async Task<(int? supplierId, bool alreadyCancelled)> MarkTypedServiceCancelledAsync(
        CancellableServiceTable serviceTable,
        Guid servicePublicId,
        int reservaId,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        switch (serviceTable)
        {
            case CancellableServiceTable.Flight:
            {
                var flight = await _db.FlightSegments.FirstOrDefaultAsync(f => f.PublicId == servicePublicId, ct)
                    ?? throw new KeyNotFoundException("Vuelo no encontrado.");
                if (flight.ReservaId != reservaId) throw new KeyNotFoundException("Vuelo no encontrado.");
                if (ServiceResolutionRules.IsCancelled(flight)) return (flight.SupplierId, true);
                flight.Status = "UN"; // codigo IATA de cancelacion (MapFlightStatus -> Cancelado)
                flight.CancelledAt = DateTime.UtcNow;
                flight.CancelledByUserId = userId;
                flight.CancelledByUserName = userName;
                return (flight.SupplierId, false);
            }
            case CancellableServiceTable.Hotel:
            {
                var hotel = await _db.HotelBookings.FirstOrDefaultAsync(h => h.PublicId == servicePublicId, ct)
                    ?? throw new KeyNotFoundException("Hotel no encontrado.");
                if (hotel.ReservaId != reservaId) throw new KeyNotFoundException("Hotel no encontrado.");
                if (ServiceResolutionRules.IsCancelled(hotel)) return (hotel.SupplierId, true);
                hotel.Status = WorkflowStatuses.Cancelado;
                hotel.CancelledAt = DateTime.UtcNow;
                hotel.CancelledByUserId = userId;
                hotel.CancelledByUserName = userName;
                return (hotel.SupplierId, false);
            }
            case CancellableServiceTable.Transfer:
            {
                var transfer = await _db.TransferBookings.FirstOrDefaultAsync(t => t.PublicId == servicePublicId, ct)
                    ?? throw new KeyNotFoundException("Traslado no encontrado.");
                if (transfer.ReservaId != reservaId) throw new KeyNotFoundException("Traslado no encontrado.");
                if (ServiceResolutionRules.IsCancelled(transfer)) return (transfer.SupplierId, true);
                transfer.Status = WorkflowStatuses.Cancelado;
                transfer.CancelledAt = DateTime.UtcNow;
                transfer.CancelledByUserId = userId;
                transfer.CancelledByUserName = userName;
                return (transfer.SupplierId, false);
            }
            case CancellableServiceTable.Package:
            {
                var package = await _db.PackageBookings.FirstOrDefaultAsync(p => p.PublicId == servicePublicId, ct)
                    ?? throw new KeyNotFoundException("Paquete no encontrado.");
                if (package.ReservaId != reservaId) throw new KeyNotFoundException("Paquete no encontrado.");
                if (ServiceResolutionRules.IsCancelled(package)) return (package.SupplierId, true);
                package.Status = WorkflowStatuses.Cancelado;
                package.CancelledAt = DateTime.UtcNow;
                package.CancelledByUserId = userId;
                package.CancelledByUserName = userName;
                return (package.SupplierId, false);
            }
            case CancellableServiceTable.Assistance:
            {
                var assistance = await _db.AssistanceBookings.FirstOrDefaultAsync(a => a.PublicId == servicePublicId, ct)
                    ?? throw new KeyNotFoundException("Asistencia no encontrada.");
                if (assistance.ReservaId != reservaId) throw new KeyNotFoundException("Asistencia no encontrada.");
                if (ServiceResolutionRules.IsCancelled(assistance)) return (assistance.SupplierId, true);
                assistance.Status = WorkflowStatuses.Cancelado;
                assistance.CancelledAt = DateTime.UtcNow;
                assistance.CancelledByUserId = userId;
                assistance.CancelledByUserName = userName;
                return (assistance.SupplierId, false);
            }
            case CancellableServiceTable.Generic:
            default:
            {
                var service = await _db.Servicios.FirstOrDefaultAsync(s => s.PublicId == servicePublicId, ct)
                    ?? throw new KeyNotFoundException("Servicio no encontrado.");
                if (service.ReservaId != reservaId) throw new KeyNotFoundException("Servicio no encontrado.");
                if (ServiceResolutionRules.IsCancelled(service)) return (service.SupplierId, true);
                service.Status = WorkflowStatuses.Cancelado;
                service.CancelledAt = DateTime.UtcNow;
                service.CancelledByUserId = userId;
                service.CancelledByUserName = userName;
                return (service.SupplierId, false);
            }
        }
    }

    /// <summary>
    /// CAMBIO 2 (2026-06-24): al ANULAR la reserva COMPLETA (Draft -> Confirm del BookingCancellation), marca
    /// TODOS los servicios de la reserva como Cancelado, reusando EXACTAMENTE la misma logica por servicio que
    /// <see cref="MarkTypedServiceCancelledAsync"/> (vuelos con codigo IATA "UN" porque su Status mapea por
    /// codigo, no por texto; el resto con <see cref="WorkflowStatuses.Cancelado"/>; seteando CancelledAt/By).
    ///
    /// <para><b>Por que existe</b>: hasta hoy los servicios solo se marcaban cancelados al cancelar UNO suelto
    /// (cancelacion parcial). Al anular TODA la reserva, los servicios quedaban en "Confirmado" aunque la
    /// reserva estuviera en <c>PendingOperatorRefund</c> — la ficha mostraba servicios "Confirmados" de una
    /// reserva anulada. NO inventamos un estado "en devolucion" del servicio: el servicio queda Cancelado y el
    /// "esperando reembolso del operador" es dato de la RESERVA, no del servicio (alineado a los ERPs).</para>
    ///
    /// <para><b>Idempotente</b>: si un servicio ya esta cancelado (<see cref="ServiceResolutionRules.IsCancelled"/>),
    /// no lo vuelve a tocar (no pisa CancelledAt/By previos). <b>NO hace SaveChanges</b>: corre dentro de la
    /// misma transaccion del caller (ConfirmAsync paso 9 / ForceArcaConfirmationAsync) para ser atomico con la
    /// transicion de estado de la reserva.</para>
    /// </summary>
    private async Task CancelAllReservaServicesAsync(
        int reservaId, string userId, string? userName, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Aereos: Status mapea por codigo IATA. "UN" = cancelado (MapFlightStatus). Poner "Cancelado" literal
        // NO lo sacaria del saldo ni de la deuda, por eso el codigo, igual que el path por-servicio.
        var flights = await _db.FlightSegments.Where(f => f.ReservaId == reservaId).ToListAsync(ct);
        foreach (var flight in flights)
        {
            if (ServiceResolutionRules.IsCancelled(flight)) continue;
            flight.Status = "UN";
            flight.CancelledAt = now;
            flight.CancelledByUserId = userId;
            flight.CancelledByUserName = userName;
        }

        var hotels = await _db.HotelBookings.Where(h => h.ReservaId == reservaId).ToListAsync(ct);
        foreach (var hotel in hotels)
        {
            if (ServiceResolutionRules.IsCancelled(hotel)) continue;
            hotel.Status = WorkflowStatuses.Cancelado;
            hotel.CancelledAt = now;
            hotel.CancelledByUserId = userId;
            hotel.CancelledByUserName = userName;
        }

        var transfers = await _db.TransferBookings.Where(t => t.ReservaId == reservaId).ToListAsync(ct);
        foreach (var transfer in transfers)
        {
            if (ServiceResolutionRules.IsCancelled(transfer)) continue;
            transfer.Status = WorkflowStatuses.Cancelado;
            transfer.CancelledAt = now;
            transfer.CancelledByUserId = userId;
            transfer.CancelledByUserName = userName;
        }

        var packages = await _db.PackageBookings.Where(p => p.ReservaId == reservaId).ToListAsync(ct);
        foreach (var package in packages)
        {
            if (ServiceResolutionRules.IsCancelled(package)) continue;
            package.Status = WorkflowStatuses.Cancelado;
            package.CancelledAt = now;
            package.CancelledByUserId = userId;
            package.CancelledByUserName = userName;
        }

        var assistances = await _db.AssistanceBookings.Where(a => a.ReservaId == reservaId).ToListAsync(ct);
        foreach (var assistance in assistances)
        {
            if (ServiceResolutionRules.IsCancelled(assistance)) continue;
            assistance.Status = WorkflowStatuses.Cancelado;
            assistance.CancelledAt = now;
            assistance.CancelledByUserId = userId;
            assistance.CancelledByUserName = userName;
        }

        var genericServices = await _db.Servicios.Where(s => s.ReservaId == reservaId).ToListAsync(ct);
        foreach (var service in genericServices)
        {
            if (ServiceResolutionRules.IsCancelled(service)) continue;
            service.Status = WorkflowStatuses.Cancelado;
            service.CancelledAt = now;
            service.CancelledByUserId = userId;
            service.CancelledByUserName = userName;
        }
    }

    /// <summary>
    /// ARREGLO 1 (2026-06-24, bloqueante): tras anular la reserva COMPLETA (todos los servicios a Cancelado),
    /// recalcula EN EL MISMO request: (1) la deuda de CADA operador afectado y (2) la plata del cliente + la
    /// comision del vendedor de la reserva. Replica EXACTAMENTE el patron del path de cancelar UN servicio
    /// suelto (<see cref="CancelServiceAsync"/>), que ya recalculaba bien; antes la anulacion total dejaba
    /// esto para el job de AFIP, que solo tocaba la plata del cliente -> la deuda agregada con el operador
    /// quedaba inflada (contaba servicios ya anulados) y la comision colgada hasta que algo mas tocara al
    /// proveedor o un Admin recalculara a mano.
    ///
    /// <para><b>Reusa los persisters existentes</b> (no inventa calculo):
    /// <list type="bullet">
    /// <item><see cref="SupplierDebtPersister"/> por cada operador distinto de los servicios de la reserva.
    ///   Con los servicios ya en Cancelado, esas compras dejan de contar y la deuda baja a lo real.</item>
    /// <item><see cref="ReservaMoneyPersister"/> para el cliente; este persister, al final, ya dispara
    ///   <c>CommissionAccrualPersister</c> (chokepoint unico de la plata), asi que la comision se pone en
    ///   cero por su tope-cero al quedar la reserva sin servicios vivos. No hay que llamarlo aparte.</item>
    /// </list></para>
    ///
    /// <para><b>SaveChanges</b>: <see cref="SupplierDebtPersister"/> NO guarda (lo hace el caller), por eso
    /// hacemos un <c>SaveChanges</c> explicito tras recalcular todos los proveedores. <see cref="ReservaMoneyPersister"/>
    /// SI guarda internamente. Cuando este metodo corre dentro de la transaccion de <see cref="ConfirmAsync"/>
    /// (ARREGLO 3), todos esos SaveChanges participan de la misma transaccion -> atomico con la anulacion.</para>
    /// </summary>
    private async Task RecalculateMoneyAfterTotalCancellationAsync(
        int reservaId, string? actorUserId, string? actorUserName, CancellationToken ct)
    {
        // 1) Deuda de cada operador afectado. Reusamos el helper compartido (2026-06-26): junta los SupplierId
        //    distintos de los 6 tipos de servicio, recalcula con SupplierDebtPersister por cada uno y hace un
        //    SaveChanges. La MISMA regla la usa el caso (3) del flujo "Anular reserva"
        //    (ReservaService.ApplyAnnulWithPaymentsToCreditAsync), para no tener una tercera copia que diverja.
        await TravelApi.Infrastructure.Reservations.SupplierDebtPersister.PersistForReservaSuppliersAsync(_db, reservaId, ct);

        // 2) Plata del cliente (+ comision por el chokepoint de ReservaMoneyPersister). Guarda internamente.
        await TravelApi.Infrastructure.Reservations.ReservaMoneyPersister.PersistAsync(_db, reservaId, ct);

        // 3) Pasos B/C — C0 + C1 (2026-06-29): tras recalcular el balance de cada operador (paso 1, ya committed),
        //    reconciliamos su POOL de saldo a favor. Anular un servicio pagado deja la caja en negativo por el
        //    total pagado, pero ese negativo es un REEMBOLSO POR COBRAR (Y), NO un saldo a favor consumible: el
        //    reconciler con la formula economica lo deja en 0 (no mintea la fuga). Antes, el pool NO se tocaba al
        //    anular (solo en eventos de pago) -> el negativo de caja quedaba como credito gastable fantasma. Va en
        //    la MISMA transaccion de la anulacion (caller). Net-neutral: anular nunca BAJA el sobrepago, asi que el
        //    throw INV-SUPCREDIT-001 es inalcanzable por este camino (diseño rev 2 §4.6).
        var affectedSuppliers = await TravelApi.Infrastructure.Reservations.SupplierDebtPersister
            .GetReservaSupplierIdsAsync(_db, reservaId, ct);
        foreach (var supplierId in affectedSuppliers)
        {
            await ReconcileSupplierCreditPoolAsync(supplierId, actorUserId, actorUserName, ct);
        }
    }

    /// <summary>
    /// Pasos B/C (2026-06-29): dispara el reconciler del POOL de saldo a favor del operador (transaction-agnostic,
    /// dentro de la transaccion del caller). Se llama DESPUES de persistir el cambio de estado/balance que movio el
    /// sobrepago economico (multa confirmada / cierre sin multa / anulacion). Idempotente. El reconciler hace su
    /// propio SaveChanges con la auditoria staged. Actor null -> el audit del pool queda como "System" (la accion
    /// de negocio ya queda auditada con el actor real en su propio evento).
    /// </summary>
    private Task ReconcileSupplierCreditPoolAsync(int supplierId, string? actorUserId, string? actorUserName, CancellationToken ct)
        => TravelApi.Infrastructure.Reservations.SupplierCreditReconciler.ReconcileAsync(
            _db, supplierId, sourceSupplierPaymentId: null, actorUserId, actorUserName, _auditService, ct);

    /// <summary>
    /// R1 (Pasos B/C, plata viva, 2026-06-30): impide cancelar un servicio PAGADO al operador cuando NO hay forma de
    /// ANCLAR el receivable "me tiene que devolver" (no existe factura de venta viva que sostenga el
    /// <see cref="BookingCancellation"/> padre de la linea). Sin ese ancla, cancelar deja la caja del operador en
    /// negativo sin linea que represente el receivable y el reconciler mintearia ese negativo como saldo a favor.
    ///
    /// <para>Bloquea SOLO el caso con fuga real: el servicio tiene plata pagada al operador (su <c>RefundCap</c>
    /// reconstruido seria &gt; 0) Y la reserva no tiene factura viva. Un servicio impago (RefundCap 0) o una reserva
    /// con factura no se bloquean. El <c>RefundCap</c> se calcula con el MISMO armado que el path real
    /// (<see cref="BuildCancellationLinesAsync"/> + <see cref="AssignRefundCapsAsync"/>), read-only, ANTES de tocar
    /// el servicio, para no dejar estado a medias. Lanza <see cref="InvalidOperationException"/> -> el controller la
    /// mapea a 409, igual que los demas candados de esta operacion.</para>
    /// </summary>
    private async Task EnsurePaidServiceCancellationHasReceivableAnchorAsync(
        Reserva reserva, CancellableServiceTable serviceTable, Guid servicePublicId, CancellationToken ct)
    {
        // Resolver el Id (int) del servicio puntual, validando pertenencia a la reserva. Si no resuelve, no
        // bloqueamos: el servicio se valida/404ea al marcarlo mas adelante.
        var serviceId = await ResolveServiceIdAsync(serviceTable, servicePublicId, reserva.Id, ct);
        if (serviceId is null) return;

        // Nucleo compartido con la anulacion TOTAL: reconstruye, read-only, lo pagado al operador por ESTE servicio
        // (su RefundCap). 0 = impago o reserva con factura viva -> sin fuga. > 0 = hay plata sin ancla -> bloqueo.
        decimal wouldBeRefundCap = await ComputeUnanchoredOperatorRefundCapAsync(
            reserva, BookingCancellationLineScope.Partial, serviceTable, serviceId.Value, ct);

        if (wouldBeRefundCap > 0m)
            throw new InvalidOperationException(
                "No se puede cancelar este servicio todavía: ya tiene pagos al operador y la reserva aún no tiene " +
                "factura emitida para registrar el reembolso a tu favor. Emití la factura de venta o gestioná el " +
                "reembolso con el operador antes de cancelar el servicio.");
    }

    /// <summary>
    /// R1 — VARIANTE TOTAL (plata viva, gemela de <see cref="EnsurePaidServiceCancellationHasReceivableAnchorAsync"/>,
    /// 2026-06-30): impide ANULAR una reserva entera ("Anular con saldo a favor", flujo
    /// <c>ReservaService.AnnulWithPaymentsToCreditAsync</c>) cuando se le pagó al operador por uno o más servicios y
    /// NO hay factura de venta viva que ancle el receivable "me tiene que devolver".
    ///
    /// <para><b>Por que existe</b>: ese flujo cancela TODOS los servicios vivos (caja del operador queda negativa por
    /// lo pagado) pero, a diferencia de la anulación formal con Nota de Crédito, NO crea ninguna
    /// <see cref="BookingCancellationLine"/>. Como el receivable Y se deriva EXCLUSIVAMENTE de esas líneas, sin línea
    /// Y=0 y el reconciler (<c>SupplierCreditReconciler</c>) materializaría el negativo de caja como saldo a favor
    /// GASTABLE — plata que en realidad el operador debe devolver. Mismo agujero que R1 para un servicio suelto,
    /// extendido a la anulación total.</para>
    ///
    /// <para><b>Que bloquea</b>: SOLO el caso con fuga real (algún servicio con plata pagada al operador + reserva
    /// SIN factura viva). Reserva con factura (el path normal ancla el receivable), servicios impagos al operador
    /// (RefundCap 0), o reserva sin ningún servicio con operador (no hay receivable posible) NO se bloquean. El cálculo
    /// es READ-ONLY (reusa <see cref="ComputeUnanchoredOperatorRefundCapAsync"/>), corre ANTES de mutar nada y lanza
    /// <see cref="InvalidOperationException"/> -> el controller la mapea a 409, igual que los demás candados de plata.</para>
    /// </summary>
    public async Task EnsureReservaAnnulHasReceivableAnchorAsync(int reservaId, CancellationToken ct)
    {
        // Carga liviana de la reserva (solo Id/NumeroReserva se usan aguas abajo). AsNoTracking: es solo lectura y
        // corre dentro de la transaccion de anulacion del caller, antes de cualquier mutacion.
        var reserva = await _db.Reservas.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservaId, ct);
        if (reserva is null) return; // la reserva se valida en el flujo de anulacion; aca no bloqueamos.

        // Alcance TOTAL (todos los servicios con operador, sin filtro). Suma del RefundCap reconstruido de todas las
        // líneas; si algún servicio tiene plata pagada al operador y no hay factura, da > 0 y bloqueamos.
        decimal wouldBeRefundCap = await ComputeUnanchoredOperatorRefundCapAsync(
            reserva, BookingCancellationLineScope.Full, onlyServiceTable: null, onlyServiceId: null, ct);

        if (wouldBeRefundCap > 0m)
            throw new InvalidOperationException(
                "No se puede anular esta reserva con saldo a favor todavía: ya se le pagó al operador por uno o más " +
                "servicios y la reserva aún no tiene factura emitida para registrar el reembolso a tu favor. Emití la " +
                "factura de venta o gestioná el reembolso con el operador antes de anular la reserva.");
    }

    /// <summary>
    /// R1 — VARIANTE REASIGNACIÓN (plata viva, 2026-07-01): impide REASIGNAR el operador (o cambiar la moneda) de un
    /// servicio ya pagado al operador saliente cuando no hay factura viva que ancle el receivable. Es la TERCERA cara
    /// de la misma familia: comparte el núcleo <see cref="ComputeUnanchoredOperatorRefundCapAsync"/> con
    /// <see cref="EnsurePaidServiceCancellationHasReceivableAnchorAsync"/> (cancelar un servicio) y
    /// <see cref="EnsureReservaAnnulHasReceivableAnchorAsync"/> (anular la reserva), así los tres candados usan UN
    /// solo criterio y no pueden divergir.
    ///
    /// <para><b>Por qué es preciso (no over-block del prepago a cuenta)</b>: el pool de lo pagado al operador se arma
    /// con <c>SupplierPayments.Where(p =&gt; p.ReservaId == reservaId ...)</c> (ver <see cref="AssignRefundCapsAsync"/>),
    /// o sea EXCLUYE el prepago "a cuenta" (pagos con <c>ReservaId == null</c>). Un saldo a favor on-account del
    /// operador deja su caja GLOBAL negativa, pero NO cuenta como plata colgada de ESTE servicio -> RefundCap 0 -> no
    /// bloquea. Solo bloquea cuando hay plata imputada a ESTA reserva por este servicio que quedaría sin ancla.</para>
    ///
    /// <para>El caller (los <c>Update*Async</c>) es responsable de invocarlo SOLO cuando cambió el operador o la
    /// moneda y el servicio venía contando como compra confirmada del operador saliente. Corre ANTES de persistir la
    /// edición: el núcleo lee el servicio con <c>AsNoTracking</c>, así que ve el estado VIEJO (operador/costo/moneda
    /// previos) mientras el cambio sigue sin flushear.</para>
    /// </summary>
    public async Task EnsureServiceOperatorOrCurrencyChangeHasReceivableAnchorAsync(
        int reservaId, CancellableServiceTable serviceTable, int serviceId, bool isCurrencyChange, CancellationToken ct)
    {
        var reserva = await _db.Reservas.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservaId, ct);
        if (reserva is null) return; // la reserva la valida el propio Update; aca no bloqueamos.

        // Mismo núcleo que los otros dos candados de la familia, en alcance PARCIAL filtrado a ESTE servicio: 0 =
        // impago / con factura viva / sin operador -> sin fuga. > 0 = hay plata pagada al operador IMPUTADA A ESTA
        // RESERVA por este servicio que quedaría colgada al reasignarlo/cambiarle la moneda.
        decimal wouldBeRefundCap = await ComputeUnanchoredOperatorRefundCapAsync(
            reserva, BookingCancellationLineScope.Partial, serviceTable, serviceId, ct);

        if (wouldBeRefundCap > 0m)
            throw new InvalidOperationException(isCurrencyChange
                ? "No se puede cambiar la moneda de este servicio: hay pagos a este operador por esta reserva que " +
                  "todavía no tienen factura que los respalde. Emití la factura de venta o gestioná el reembolso con " +
                  "el operador antes de cambiar la moneda del servicio."
                : "No se puede cambiar el operador de este servicio: hay pagos a este operador por esta reserva que " +
                  "todavía no tienen factura que los respalde. Emití la factura de venta o gestioná el reembolso con " +
                  "el operador antes de cambiar el operador.");
    }

    /// <summary>
    /// Núcleo compartido de la guarda R1 (plata viva), para la cancelación de UN servicio (alcance
    /// <see cref="BookingCancellationLineScope.Partial"/> + filtro de servicio) y para la anulación TOTAL
    /// (<see cref="BookingCancellationLineScope.Full"/> sin filtro). UNA sola fuente de verdad: si esto cambia, ambos
    /// caminos cambian juntos y no pueden divergir.
    ///
    /// <para>Devuelve el RefundCap total reconstruido READ-ONLY (lo pagado al operador por los servicios en alcance,
    /// topeado por su costo), o 0 cuando NO hay fuga posible: (a) la reserva tiene factura de venta viva que ancla el
    /// receivable por el path normal, o (b) no hay ningún servicio con operador en el alcance (sin operador no hay
    /// plata que devolver). Reconstruye las líneas con el MISMO armado que el path real
    /// (<see cref="BuildCancellationLinesAsync"/> + <see cref="AssignRefundCapsAsync"/>); NO persiste nada.</para>
    /// </summary>
    private async Task<decimal> ComputeUnanchoredOperatorRefundCapAsync(
        Reserva reserva,
        BookingCancellationLineScope scope,
        CancellableServiceTable? onlyServiceTable,
        int? onlyServiceId,
        CancellationToken ct)
    {
        // (a) ¿Hay factura de venta viva que ancle el BC? (misma regla que RecordPartialCancellationLineAsync /
        //     DraftAsync). Si la hay, el path normal crea la línea con su receivable -> no hay fuga.
        bool hasLiveInvoice = await _db.Invoices.AsNoTracking()
            .AnyAsync(i => i.ReservaId == reserva.Id
                        && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante) // NC no es ancla de venta
                        && !LiveInvoiceDebitNoteTypes.Contains(i.TipoComprobante)  // ND tampoco (misma regla que DraftAsync)
                        && !string.IsNullOrEmpty(i.CAE)
                        && i.AnnulmentStatus != AnnulmentStatus.Succeeded, ct);
        if (hasLiveInvoice) return 0m;

        // (b) Reconstruir, sin persistir, las líneas que tendría la cancelación en este alcance, con su RefundCap
        //     (lo pagado al operador topeado por costo). throwIfNoOperatorService:false -> si no hay servicios con
        //     operador, devuelve lista vacía (no hay receivable) en vez de lanzar. Sumamos los caps: 0 = sin fuga.
        var wouldBeLines = await BuildCancellationLinesAsync(
            reserva, scope, ct, onlyServiceTable, onlyServiceId, throwIfNoOperatorService: false);
        return wouldBeLines.Sum(line => line.RefundCap);
    }

    /// <summary>
    /// SEC-B1b (ADR-025 DT.3.1 / §3.2): registra el evento de cancelacion PARCIAL como una linea hija de un
    /// <see cref="BookingCancellation"/> padre de la reserva. Crea (o reusa) el BC y le agrega UNA
    /// <see cref="BookingCancellationLine"/> con <c>Scope=Partial</c> para el servicio cancelado, con su
    /// operador, moneda, monto y <see cref="BookingCancellationLine.RefundCap"/> (lo pagado al operador
    /// topeado por el costo). Asi el refund del operador (OperatorRefundService) encuentra su ANCLA y el
    /// evento queda trazable.
    ///
    /// <para><b>Idempotente</b>: si ya existe una linea Partial para ese (tabla, servicio) en el BC del
    /// evento, no la duplica. <b>No emite NC</b> (decision #3): el armado queda como borrador para revision
    /// manual.</para>
    ///
    /// <para><b>Limitacion declarada (NO inventar)</b>: el padre <see cref="BookingCancellation"/> exige
    /// <c>OriginatingInvoiceId</c> (factura de venta) y hay un UNIQUE por reserva. Si la reserva NO tiene
    /// factura viva, no hay ancla fiscal donde colgar el BC: en ese caso se omite la creacion de la linea
    /// (no hay NC posible ni evento fiscal) y queda solo el servicio cancelado + la deuda recalculada. La
    /// reconciliacion del modelo BC-padre-unico vs cancelacion parcial sin factura es Q abierta del ADR
    /// (UNIQUE por reserva); fuera del alcance de este fix.</para>
    /// </summary>
    private async Task RecordPartialCancellationLineAsync(
        Reserva reserva,
        CancellableServiceTable serviceTable,
        Guid servicePublicId,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // Resolver el Id (int) del servicio cancelado en su tabla, validando pertenencia a la reserva.
        var serviceId = await ResolveServiceIdAsync(serviceTable, servicePublicId, reserva.Id, ct);
        if (serviceId is null) return; // no deberia pasar (ya se marco), defensivo.

        // Buscar la factura de venta VIVA de la reserva (misma regla que DraftAsync: no NC, CAE, no anulada).
        // Si no hay, no hay ancla fiscal para el BC padre -> no creamos linea (ver limitacion en el doc).
        var originatingInvoiceId = await _db.Invoices.AsNoTracking()
            .Where(i => i.ReservaId == reserva.Id
                     && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante) // no NC
                     && !LiveInvoiceDebitNoteTypes.Contains(i.TipoComprobante)  // no ND (misma regla que DraftAsync)
                     && !string.IsNullOrEmpty(i.CAE)
                     && i.AnnulmentStatus != AnnulmentStatus.Succeeded)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => (int?)i.Id)
            .FirstOrDefaultAsync(ct);
        if (originatingInvoiceId is null) return;

        if (reserva.PayerId is null) return; // sin Payer no hay BC posible (mismo precondicion que DraftAsync).

        var (bc, _, isNewLine) = await GetOrCreateServiceCancellationBcAndLineAsync(
            reserva, serviceTable, serviceId.Value, originatingInvoiceId.Value, userId, userName, ct);
        if (!isNewLine) return; // idempotencia: ya existia la linea para (tabla, servicio).

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// ADR-044 T5 Addendum, Decision C (2026-07-11): nucleo compartido de "conseguime el BC EN CURSO de esta
    /// reserva (o creame uno nuevo) y agregale la linea Partial de este servicio, si todavia no la tiene".
    /// Antes esta logica vivia duplicada; ahora <see cref="RecordPartialCancellationLineAsync"/> (sin factura
    /// viva, comportamiento historico) y <see cref="ResolveServiceCancellationCreditLineAsync"/> (con factura
    /// viva, T5) comparten el MISMO armado — no pueden divergir en como se resuelve/crea el BC padre.
    ///
    /// <para>Decision C (excluir Closed del UNIQUE, no solo Aborted) aplica aca: un BC Closed es un evento
    /// fiscal TERMINADO (NC con CAE, reembolso consumido); reenganchar una linea nueva a esa fila muerta la
    /// dejaria invisible para la maquina de estados que dispara NC/ND. Se abre un BC NUEVO en su lugar (mismo
    /// tratamiento que un BC Aborted).</para>
    ///
    /// <para><b>NO hace SaveChanges</b>: el caller decide cuando persistir (puede necesitar setear mas campos
    /// de la linea antes, ej. TargetInvoiceId/ConfirmedGrossCreditAmount en el camino T5).</para>
    /// </summary>
    private async Task<(BookingCancellation Bc, BookingCancellationLine Line, bool IsNewLine)> GetOrCreateServiceCancellationBcAndLineAsync(
        Reserva reserva,
        CancellableServiceTable serviceTable,
        int serviceId,
        int anchorInvoiceId,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // Include tanto Lines como CreditNotes: el camino T5 (ResolveServiceCancellationCreditLineAsync)
        // necesita agregar una fila hija a bc.CreditNotes (reserva del cap acumulativo) via la navegacion;
        // sin el Include, un BC EXISTENTE traeria esa coleccion "vacia" para el change tracker (no se
        // cargo), y un Add() por navegacion no se detectaria/persistiria en el SaveChanges del caller.
        var bc = await _db.BookingCancellations
            .Include(b => b.Lines)
            .Include(b => b.CreditNotes)
            .Where(b => b.ReservaId == reserva.Id
                     && b.Status != BookingCancellationStatus.Aborted
                     && b.Status != BookingCancellationStatus.Closed)
            .OrderByDescending(b => b.Id)
            .FirstOrDefaultAsync(ct);

        if (bc is null)
        {
            bc = new BookingCancellation
            {
                ReservaId = reserva.Id,
                CustomerId = reserva.PayerId!.Value,
                SupplierId = 0, // se setea abajo con el operador de la primera linea (denormalizado).
                OriginatingInvoiceId = anchorInvoiceId,
                Status = BookingCancellationStatus.Drafted,
                Reason = "Cancelacion parcial de servicio",
                DraftedAt = DateTime.UtcNow,
                DraftedByUserId = userId,
                DraftedByUserName = userName,
                AmountPaidAtCancellation = 0m,
                EstimatedRefundAmount = 0m,
                ReceivedRefundAmount = 0m,
                // Snapshot vacio: en Drafted el CHECK SQL lo permite. La emision fiscal completa el snapshot
                // en un paso de confirmacion aparte (mismo patron Draft/Confirm de siempre).
                FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
                IsLegacyPreCancellationModel = false,
            };
            _db.BookingCancellations.Add(bc);
        }

        // Idempotencia: no duplicar la linea si ya existe para (tabla, servicio).
        var existingLine = bc.Lines.FirstOrDefault(l => l.ServiceTable == serviceTable && l.ServiceId == serviceId);
        if (existingLine is not null)
            return (bc, existingLine, IsNewLine: false);

        // Construir la linea Partial reusando el mismo armado que el path total (incluye el RefundCap, B2).
        var builtLines = await BuildCancellationLinesAsync(
            reserva, BookingCancellationLineScope.Partial, ct,
            onlyServiceTable: serviceTable, onlyServiceId: serviceId);
        var partialLine = builtLines[0]; // BuildCancellationLinesAsync tira si no arma ninguna.

        bc.Lines.Add(partialLine);

        // Denormalizado del operador "principal" del BC (compat fiscal Mono/RI): si recien lo creamos y
        // todavia estaba en 0, lo fijamos con el operador de esta primera linea.
        if (bc.SupplierId == 0)
            bc.SupplierId = partialLine.SupplierId;

        return (bc, partialLine, IsNewLine: true);
    }

    /// <summary>
    /// ADR-044 T5 Addendum, Decision A+B (2026-07-11): plan (solo LECTURA) de a que factura de venta le
    /// corresponde el credito de UN servicio cancelado. Se resuelve ANTES de abrir la transaccion de
    /// <see cref="CancelServiceAsync"/> para que esa transaccion tome a lo sumo UN <c>FOR UPDATE</c> (sobre la
    /// factura destino) y nunca anide <c>BeginTransactionAsync</c>.
    ///
    /// <para><see cref="ServiceCancellationCreditPlan.LiveInvoiceCount"/> == 0 -> sin factura viva (camino
    /// historico). <see cref="ServiceCancellationCreditPlan.TargetInvoiceId"/> resuelto (1 factura, o eleccion
    /// que matchea) -> credito auto-resoluble. Null con 2+ facturas -> ambiguo (revision manual).</para>
    /// </summary>
    private sealed record ServiceCancellationCreditPlan(
        int? ServiceId,
        int LiveInvoiceCount,
        int? TargetInvoiceId,
        int? AnchorInvoiceId,
        string TargetInvoiceMonId,
        decimal TargetInvoiceMonCotiz);

    private async Task<ServiceCancellationCreditPlan> PlanServiceCancellationCreditAsync(
        Reserva reserva,
        CancellableServiceTable serviceTable,
        CancelServiceRequest request,
        CancellationToken ct)
    {
        var serviceId = await ResolveServiceIdAsync(serviceTable, request.ServicePublicId, reserva.Id, ct);

        // Facturas de venta VIVAS de la reserva (mismo criterio que DraftAsync: sin NC, sin ND, con CAE, no
        // anulada). Traemos MonId/MonCotiz para el guard de moneda del apply (fix fiscal-b). Mas de una es el
        // caso "2+ facturas activas" de la Decision B (T3b Decision 1 espejo).
        var liveInvoices = await _db.Invoices.AsNoTracking()
            .Where(i => i.ReservaId == reserva.Id
                     && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante)
                     && !LiveInvoiceDebitNoteTypes.Contains(i.TipoComprobante)
                     && !string.IsNullOrEmpty(i.CAE)
                     && i.AnnulmentStatus != AnnulmentStatus.Succeeded)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new { i.Id, i.PublicId, i.MonId, i.MonCotiz })
            .ToListAsync(ct);

        if (liveInvoices.Count == 0)
            return new ServiceCancellationCreditPlan(serviceId, 0, null, null, "PES", 1m);

        // A que factura le corresponde el credito (mismo patron que T3b Decision 1): 1 factura -> auto; 2+ ->
        // la que el vendedor eligio (TargetInvoicePublicId), o null (ambiguo) si no eligio o no matchea.
        int? targetInvoiceId = liveInvoices.Count == 1
            ? liveInvoices[0].Id
            : liveInvoices.FirstOrDefault(i => request.TargetInvoicePublicId == i.PublicId)?.Id;

        var target = targetInvoiceId is null
            ? null
            : liveInvoices.First(i => i.Id == targetInvoiceId.Value);

        return new ServiceCancellationCreditPlan(
            ServiceId: serviceId,
            LiveInvoiceCount: liveInvoices.Count,
            TargetInvoiceId: targetInvoiceId,
            // El ancla del BC padre (cuando la factura es ambigua) cuelga de la mas reciente, como DraftAsync.
            AnchorInvoiceId: liveInvoices[0].Id,
            TargetInvoiceMonId: target?.MonId ?? "PES",
            TargetInvoiceMonCotiz: target?.MonCotiz ?? 1m);
    }

    /// <summary>
    /// ADR-044 T5 Addendum, Decision A+B (2026-07-11): resuelve la linea de credito de UN servicio cancelado.
    /// Reemplaza el "queda como borrador mudo" de antes por una linea que SIEMPRE resuelve lo que se puede
    /// resolver automaticamente (factura + monto cuando no hay ambiguedad ni mismatch) y deja
    /// visible/accionable en la bandeja lo que no (factura ambigua, moneda que no coincide, TC incoherente, o
    /// monto que excede el remanente).
    ///
    /// <para><b>NO abre lock ni transaccion propia</b>: el caller (<see cref="CancelServiceAsync"/>) ya corre
    /// dentro de una transaccion que tomo el <c>FOR UPDATE</c> de la factura destino. Asi el tramo
    /// leer-remanente + decidir-monto + escribir queda serializado sin anidar transacciones (Npgsql las
    /// rechaza).</para>
    ///
    /// <para><b>Por que NO dispara la emision fiscal (Nota de Credito) en esta misma llamada</b>: emitir una
    /// NC exige un <see cref="FiscalSnapshot"/> completo (moneda, TC, condiciones fiscales — CHECK SQL
    /// <c>chk_BookingCancellations_fiscalsnapshot_consistent</c>, solo <c>Drafted</c>/<c>Aborted</c> admiten
    /// snapshot vacio) que <see cref="CancelServiceRequest"/> no trae y que este backend NO puede inventar sin
    /// que el usuario lo confirme (mismo criterio INV-118/INV-120). Por eso la linea queda lista (factura
    /// destino + monto) pero el BC permanece <c>Drafted</c>: la emision real es un paso de confirmacion APARTE
    /// (tanda de UI). El cap se reserva por la LINEA (TargetInvoiceId + ConfirmedGrossCreditAmount), NO por una
    /// hija Pending fantasma (esa hija se crea recien al emitir, en la tanda de la pantalla — fix B2-backend).</para>
    ///
    /// <para>Devuelve la decision de credito (factura destino + monto confirmado, o null/null) para que el
    /// caller la deje en la auditoria inmutable (M3), no solo en columnas mutables de la linea.</para>
    /// </summary>
    private async Task<(int? TargetInvoiceId, decimal? ConfirmedGrossCreditAmount)> ApplyServiceCancellationCreditLineAsync(
        Reserva reserva,
        CancellableServiceTable serviceTable,
        ServiceCancellationCreditPlan plan,
        CancelServiceRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        if (plan.ServiceId is null) return (null, null); // no deberia pasar (ya se marco), defensivo.

        if (plan.LiveInvoiceCount == 0)
        {
            // 1) Sin factura viva: comportamiento historico (el borrador queda sin ancla fiscal).
            await RecordPartialCancellationLineAsync(reserva, serviceTable, request.ServicePublicId, userId, userName, ct);
            return (null, null);
        }

        // A esta altura reserva.PayerId NUNCA es null: CancelServiceAsync ya bloqueo con 409 (paso 0-ter) si
        // hubiera factura viva sin Payer, ANTES de marcar el servicio cancelado.

        if (plan.TargetInvoiceId is null)
        {
            // 3a) Ambiguo (2+ facturas activas, sin eleccion valida): la linea queda SIN factura destino
            // resuelta -> visible/accionable en la bandeja, nunca silenciosa. El ancla del BC padre cuelga de
            // la factura mas reciente (mismo criterio que DraftAsync).
            var (_, _, _) = await GetOrCreateServiceCancellationBcAndLineAsync(
                reserva, serviceTable, plan.ServiceId.Value, plan.AnchorInvoiceId!.Value, userId, userName, ct);
            await _db.SaveChangesAsync(ct);
            return (null, null);
        }

        // 2) Factura destino resuelta. Ya estamos bajo el FOR UPDATE de esa factura (transaccion externa de
        // CancelServiceAsync): leer-remanente + decidir-monto + escribir corre serializado.
        var (bc, line, _) = await GetOrCreateServiceCancellationBcAndLineAsync(
            reserva, serviceTable, plan.ServiceId.Value, plan.TargetInvoiceId.Value, userId, userName, ct);

        line.TargetInvoiceId = plan.TargetInvoiceId.Value;

        // Guard de moneda (fix fiscal-b): la NC de una linea solo se puede acreditar contra una factura de la
        // MISMA moneda. Normalizamos ambos lados al catalogo ARCA (la factura guarda MonId en ARCA "PES"/"DOL";
        // la linea guarda Currency en ISO "ARS"/"USD"). Comparar los strings crudos daria mismatch SIEMPRE
        // (ARS != PES) y bloquearia el 100% de los casos legitimos en pesos.
        var lineCurrencyArca = ArcaCurrencyMapper.TryMap(line.Currency); // ISO -> ARCA (null si no soportada)
        var invoiceCurrencyArca = string.IsNullOrWhiteSpace(plan.TargetInvoiceMonId)
            ? "PES"
            : plan.TargetInvoiceMonId.Trim().ToUpperInvariant();
        bool currencyMatches = lineCurrencyArca is not null
            && string.Equals(lineCurrencyArca, invoiceCurrencyArca, StringComparison.OrdinalIgnoreCase);

        // Coherencia del TC de la factura destino: una factura extranjera con cotizacion 1 (o <=0) es dato
        // corrupto (misma regla que el guard de NC total, INV-156). No inventamos un TC: derivamos a manual.
        bool invoiceIsForeign = !string.Equals(invoiceCurrencyArca, "PES", StringComparison.OrdinalIgnoreCase);
        bool exchangeRateCoherent = plan.TargetInvoiceMonCotiz > 0m
            && (!invoiceIsForeign || plan.TargetInvoiceMonCotiz != 1m);

        if (!currencyMatches || !exchangeRateCoherent)
        {
            // 3b) Mismatch de moneda o TC incoherente: NO auto-acreditamos (inventar el credito exigiria un TC
            // que no tenemos confirmado). La linea queda con su factura destino resuelta pero SIN monto ->
            // visible como pendiente de resolucion manual. El servicio igual quedo cancelado.
            ClearLineCreditAmount(line);
            await _db.SaveChangesAsync(ct);
            return (plan.TargetInvoiceId, null);
        }

        var remainingCreditableAmount = await ComputeInvoiceRemainingCreditableAmountAsync(plan.TargetInvoiceId.Value, ct);

        // Monto propuesto: LineSaleAmount del servicio (cero friccion visual) salvo que el vendedor ya haya
        // confirmado un monto explicito (request.ConfirmedGrossCreditAmount, pantalla aparte).
        var proposedAmount = request.ConfirmedGrossCreditAmount ?? line.LineSaleAmount;

        if (proposedAmount > 0m && proposedAmount <= remainingCreditableAmount)
        {
            // 2) Auto-resuelto. == remanente -> NC TOTAL de esa factura; < remanente -> NC PARCIAL
            //    (CreditNoteKind.PartialOnOriginal). La linea guarda el monto confirmado; quien complete la
            //    confirmacion fiscal (paso aparte) decide con ese numero si emite total o parcial.
            //    El cap se reserva por la LINEA (TargetInvoiceId + ConfirmedGrossCreditAmount). NO se crea una
            //    hija Pending fantasma (fix B2-backend): ese registro reservaba remanente para siempre (nunca
            //    pasaba a Failed) y la levantaba el reconciler como "NC esperando CAE". La hija recien se crea
            //    al EMITIR (tanda de la pantalla).
            line.ConfirmedGrossCreditAmount = proposedAmount;
            line.CreditAmountConfirmedByUserId = userId;
            line.CreditAmountConfirmedByUserName = userName;
            line.CreditAmountConfirmedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return (plan.TargetInvoiceId, proposedAmount);
        }

        // > remanente (o remanente ya en 0): rechazado, NO se persiste monto (excederia lo que la factura
        // vale). La linea queda con su factura destino resuelta pero SIN monto -> visible como pendiente.
        ClearLineCreditAmount(line);
        await _db.SaveChangesAsync(ct);
        return (plan.TargetInvoiceId, null);
    }

    /// <summary>
    /// Limpia el monto de credito confirmado de una linea (y quien/cuando lo confirmo). Se usa cuando el
    /// credito NO se puede auto-resolver (moneda que no coincide, TC incoherente, o monto que excede el
    /// remanente): la linea queda visible como pendiente de resolucion manual, sin monto inventado.
    /// </summary>
    private static void ClearLineCreditAmount(BookingCancellationLine line)
    {
        line.ConfirmedGrossCreditAmount = null;
        line.CreditAmountConfirmedByUserId = null;
        line.CreditAmountConfirmedByUserName = null;
        line.CreditAmountConfirmedAt = null;
    }

    /// <summary>
    /// ADR-044 T5 Addendum, Decision A punto 4 (2026-07-11): true si la reserva tiene AL MENOS UNA factura de
    /// venta viva (mismo criterio que <see cref="ResolveServiceCancellationCreditLineAsync"/>/DraftAsync: sin
    /// NC, sin ND, con CAE, no anulada). Usado por el bloqueo duro de "factura viva sin Payer".
    /// </summary>
    private Task<bool> ReservaHasLiveSaleInvoiceAsync(int reservaId, CancellationToken ct)
        => _db.Invoices.AsNoTracking().AnyAsync(
            i => i.ReservaId == reservaId
                 && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante)
                 && !LiveInvoiceDebitNoteTypes.Contains(i.TipoComprobante)
                 && !string.IsNullOrEmpty(i.CAE)
                 && i.AnnulmentStatus != AnnulmentStatus.Succeeded,
            ct);

    /// <summary>
    /// Resuelve el Id (int) de un servicio por (tabla, PublicId) validando que pertenece a la reserva.
    /// Devuelve <c>null</c> si no existe o no es de la reserva. AsNoTracking: es solo lectura para armar la linea.
    /// </summary>
    private async Task<int?> ResolveServiceIdAsync(
        CancellableServiceTable serviceTable, Guid servicePublicId, int reservaId, CancellationToken ct)
    {
        return serviceTable switch
        {
            CancellableServiceTable.Flight => await _db.FlightSegments.AsNoTracking()
                .Where(f => f.PublicId == servicePublicId && f.ReservaId == reservaId).Select(f => (int?)f.Id).FirstOrDefaultAsync(ct),
            CancellableServiceTable.Hotel => await _db.HotelBookings.AsNoTracking()
                .Where(h => h.PublicId == servicePublicId && h.ReservaId == reservaId).Select(h => (int?)h.Id).FirstOrDefaultAsync(ct),
            CancellableServiceTable.Transfer => await _db.TransferBookings.AsNoTracking()
                .Where(t => t.PublicId == servicePublicId && t.ReservaId == reservaId).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct),
            CancellableServiceTable.Package => await _db.PackageBookings.AsNoTracking()
                .Where(p => p.PublicId == servicePublicId && p.ReservaId == reservaId).Select(p => (int?)p.Id).FirstOrDefaultAsync(ct),
            CancellableServiceTable.Assistance => await _db.AssistanceBookings.AsNoTracking()
                .Where(a => a.PublicId == servicePublicId && a.ReservaId == reservaId).Select(a => (int?)a.Id).FirstOrDefaultAsync(ct),
            _ => await _db.Servicios.AsNoTracking()
                .Where(s => s.PublicId == servicePublicId && s.ReservaId == reservaId).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct),
        };
    }

    /// <summary>
    /// ADR-025 (DT.3.1, decision #1): cuenta, sobre las 6 colecciones de servicios CON operador, cuantos
    /// estan cancelados y cuantos hay en total. Alimenta el contador "N de M servicios cancelado" del
    /// header (dato calculado, no estado nuevo de reserva).
    /// </summary>
    private async Task<(int cancelled, int total)> CountServicesAsync(int reservaId, CancellationToken ct)
    {
        int cancelled = 0;
        int total = 0;

        var hotels = await _db.HotelBookings.AsNoTracking().Where(h => h.ReservaId == reservaId).ToListAsync(ct);
        foreach (var h in hotels) { total++; if (ServiceResolutionRules.IsCancelled(h)) cancelled++; }

        var flights = await _db.FlightSegments.AsNoTracking().Where(f => f.ReservaId == reservaId).ToListAsync(ct);
        foreach (var f in flights) { total++; if (ServiceResolutionRules.IsCancelled(f)) cancelled++; }

        var transfers = await _db.TransferBookings.AsNoTracking().Where(t => t.ReservaId == reservaId).ToListAsync(ct);
        foreach (var t in transfers) { total++; if (ServiceResolutionRules.IsCancelled(t)) cancelled++; }

        var packages = await _db.PackageBookings.AsNoTracking().Where(p => p.ReservaId == reservaId).ToListAsync(ct);
        foreach (var p in packages) { total++; if (ServiceResolutionRules.IsCancelled(p)) cancelled++; }

        var assistances = await _db.AssistanceBookings.AsNoTracking().Where(a => a.ReservaId == reservaId).ToListAsync(ct);
        foreach (var a in assistances) { total++; if (ServiceResolutionRules.IsCancelled(a)) cancelled++; }

        var generics = await _db.Servicios.AsNoTracking().Where(s => s.ReservaId == reservaId).ToListAsync(ct);
        foreach (var s in generics) { total++; if (ServiceResolutionRules.IsCancelled(s)) cancelled++; }

        return (cancelled, total);
    }

    /// <summary>
    /// B1 (2026-06-03): resuelve que hacer con la cancelacion PREEXISTENTE de la
    /// reserva antes de crear una nueva. Es el corazon de la politica de reintento
    /// de INV-081 y la unica fuente de verdad sobre que estados son "liberables".
    ///
    /// <para>Devuelve un DTO NO-NULL solo en el caso (a) (draft puro reusable): el
    /// caller debe devolver ese DTO y NO crear fila nueva. Devuelve null cuando NO
    /// hay fila previa, o cuando la fila previa quedo liberada para crear una NUEVA
    /// (Aborted preexistente, o ArcaRejected auto-abortado aca dentro). Tira
    /// <see cref="BusinessInvariantViolationException"/> INV-081 para los estados
    /// no liberables.</para>
    ///
    /// <para><b>Regla fiscal dura</b>: un BC es liberable para re-cancelar SOLO si no
    /// dejo ninguna nota de credito viva (CAE aprobado). Por eso ArcaRejected exige
    /// <c>CreditNoteInvoiceId is null</c> antes de liberarse: si por cualquier camino
    /// tuviera una NC viva, lo tratamos como no liberable (rechazo). Esto blinda
    /// contra una segunda NC sobre la misma factura.</para>
    /// </summary>
    private async Task<BookingCancellationDto?> TryResolveExistingBcAsync(
        Reserva reserva,
        string userId,
        string? userName,
        string newReason,
        CancellationToken ct)
    {
        var existingBc = await _db.BookingCancellations
            .Where(b => b.ReservaId == reserva.Id)
            .OrderByDescending(b => b.Id)
            .FirstOrDefaultAsync(ct);

        if (existingBc is null)
            return null; // primer intento de cancelacion de la reserva.

        // Caso (a): draft puro sin nada fiscal emitido -> reusar la misma fila.
        // El triple check (Status + sin NC + sin ND) es defensivo: si por cualquier
        // camino una fila quedara en Drafted con un comprobante vinculado, NO la
        // tratamos como reusable.
        var isReusableDraft =
            existingBc.Status == BookingCancellationStatus.Drafted
            && existingBc.CreditNoteInvoiceId is null
            && existingBc.DebitNoteInvoiceId is null;

        if (isReusableDraft)
        {
            _logger.LogInformation(
                "DraftAsync reuse: BC {BcPublicId} ya estaba en Drafted sin comprobantes para " +
                "la reserva {ReservaPublicId}. Devolvemos el draft existente (reintento idempotente).",
                existingBc.PublicId, reserva.PublicId);

            // F2 (2026-07-02): si el vendedor edito el motivo entre el draft original y este re-draft
            // (toca "Volver", cambia el texto, vuelve a "Anular"), ACTUALIZAMOS el motivo del draft reusado.
            // Sin esto la anulacion quedaba auditada con el motivo VIEJO. El motivo es el del acto real; el
            // request nuevo manda. Dejamos rastro del cambio en el audit del reuse.
            var trimmedNewReason = newReason?.Trim() ?? string.Empty;
            string? previousReason = null;
            bool reasonChanged = trimmedNewReason.Length > 0
                && !string.Equals(existingBc.Reason, trimmedNewReason, StringComparison.Ordinal);
            if (reasonChanged)
            {
                previousReason = existingBc.Reason;
                existingBc.Reason = trimmedNewReason;
                await _db.SaveChangesAsync(ct);
            }

            // FIX 3: auditoria de negocio del reuse (antes solo habia LogInformation).
            await _auditService.LogBusinessEventAsync(
                action: AuditActions.BookingCancellationDraftReused,
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: existingBc.Id.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    existingBc.PublicId,
                    ReservaPublicId = reserva.PublicId,
                    reasonUpdated = reasonChanged,
                    previousReason,
                }),
                userId: userId,
                userName: userName,
                ct: ct);

            // FRENTE E (ADR-044 T5, D3): si este draft reusable es un BC de cancelacion PARCIAL (tiene lineas
            // Scope=Partial de servicios cancelados uno a uno via CancelServiceAsync) y AHORA se esta anulando
            // TODO el file, completamos el MISMO BC con las lineas Full de los servicios vivos restantes,
            // preservando la(s) Partial. Sin esto, el anular-total heredaria solo las Partial y sub-acreditaria
            // al cliente (los servicios vivos no recibirian linea/RefundCap). Un solo BC con Partial + Full;
            // el unico por ReservaId impide abrir otro.
            await CompleteReusedDraftWithFullLinesIfPartialAsync(existingBc, reserva, ct);

            return await MapToDtoAsync(existingBc.Id, ct)
                ?? throw new InvalidOperationException(
                    "BC existente no encontrada al reutilizar el draft. Estado inconsistente.");
        }

        // Caso (b): Aborted preexistente -> la fila vieja queda como rastro; el caller
        // crea un BC NUEVO (el UNIQUE parcial Status<>6 no la ve, no hay colision).
        if (existingBc.Status == BookingCancellationStatus.Aborted)
            return null;

        // Caso (c): ArcaRejected SIN NC viva -> AFIP rechazo la NC (CAE no aprobado),
        // no quedo comprobante fiscal vivo. Decision de negocio (dueno, 2026-06-03):
        // la reserva DEBE poder volver a cancelarse por la via normal. Auto-abortamos
        // la fila vieja (asi el UNIQUE parcial la deja salir y queda traza del intento
        // fallido) y devolvemos null para que el caller cree el BC nuevo.
        //
        // BLINDAJE FISCAL: SOLO si NO dejo ninguna NC viva. Un ArcaRejected con NC viva cae al rechazo
        // INV-081 de abajo: jamas liberamos algo con NC viva (segunda NC sobre la misma factura = incidente).
        //
        // ADR-042 §3.4 (2026-07-01): con multi-factura, el puntero principal (CreditNoteInvoiceId) puede ser
        // null y AUN ASI existir una hija con NC viva (una NC salio OK y otra fallo -> BC ArcaRejected parcial).
        // Por eso el guard mira TAMBIEN las hijas: "ninguna con NC viva". Sin hijas (legacy) = check del
        // puntero singular (comportamiento historico), plegado dentro del mismo guard (fallback B4).
        if (existingBc.Status == BookingCancellationStatus.ArcaRejected)
        {
            bool hasLiveCreditNote = existingBc.CreditNoteInvoiceId is not null
                || await BcHasLiveCreditNoteChildAsync(existingBc.Id, ct);
            if (!hasLiveCreditNote)
            {
                await AutoAbortArcaRejectedAsync(existingBc, reserva, userId, userName, ct);
                return null;
            }
            // hasLiveCreditNote -> cae al rechazo INV-081 de abajo (no liberar con NC viva).
        }

        // Caso (c-bis) ADR-044 T5 Addendum, Decision C (2026-07-11, hallazgo nuevo del re-review): Closed es
        // un evento fiscal TERMINADO (NC con CAE, reembolso ya consumido), no una cancelacion "en curso".
        // ANTES de este fix, un BC Closed caia en el caso (d) de abajo y rechazaba INV-081 para SIEMPRE:
        // una reserva con una cancelacion PARCIAL de un solo servicio ya cerrada quedaba IMPOSIBLE de volver
        // a anular (total o parcialmente) nunca mas, tratando un hecho ya terminado como si siguiera vivo.
        // Mismo tratamiento que el caso (b) Aborted: liberamos (return null) para que el caller abra un BC
        // NUEVO. No hay riesgo fiscal: Closed, por definicion, ya tiene su NC con CAE resuelta, asi que
        // liberar la fila NO reabre ni reinterpreta ningun comprobante — simplemente permite un evento NUEVO.
        if (existingBc.Status == BookingCancellationStatus.Closed)
            return null;

        // Caso (d): cualquier otro estado = cancelacion REALMENTE activa o con efecto
        // fiscal en juego (AwaitingFiscalConfirmation, AwaitingOperatorRefund,
        // ClientCreditApplied, AbandonedByOperator, ManualReview*, o un
        // ArcaRejected con NC viva) -> rechazo INV-081.
        //
        // ManualReviewRejected=11 (decision B1 2026-06-03): lo dejamos RECHAZANDO,
        // NO liberable. Razon: a ese estado se llega cuando un admin rechaza la
        // liquidacion de una NC parcial; la remediacion disenada es ResetToDraftAsync
        // (vuelve el MISMO BC a Drafted) o AbortAsync explicito, no crear un BC nuevo
        // por la espalda. Ademas no esta garantizado server-side que un
        // ManualReviewRejected no tenga CreditNoteInvoiceId vivo en todos los caminos
        // futuros de la Fase 2 (NC parcial real). Ante la duda fiscal, rechazamos:
        // el vendedor/admin usa el flujo de remediacion explicito de ese estado.
        throw new BusinessInvariantViolationException(
            $"La reserva {reserva.NumeroReserva} ya tiene una cancelación en curso.",
            invariantCode: "INV-081");
    }

    /// <summary>
    /// FRENTE E (ADR-044 T5, D3, 2026-07-11): cuando el anular-total reusa un draft que en realidad es un BC de
    /// cancelacion PARCIAL (tiene lineas <c>Scope=Partial</c> de servicios cancelados uno a uno), lo COMPLETA
    /// agregando las lineas <c>Scope=Full</c> de los servicios vivos restantes, preservando la(s) Partial. Asi
    /// el cliente se acredita por el TOTAL del file, no solo por lo que se habia cancelado parcialmente.
    ///
    /// <para>No hace nada si el draft no tiene lineas Partial (draft de anulacion total normal, ya armado con
    /// sus lineas Full por <c>DraftAsync</c>): es idempotente y seguro de llamar siempre en el reuse.</para>
    ///
    /// <para><b>Por que no duplica</b>: <c>BuildCancellationLinesAsync(Full)</c> excluye los servicios ya
    /// cancelados (B1(b)), asi que el servicio ya cancelado por T5 NO recibe una segunda linea — conserva su
    /// Partial; las nuevas Full cubren solo los vivos. El guard por (tabla, servicio) es defensivo extra.
    /// <c>throwIfNoOperatorService: false</c>: si ya no quedan servicios vivos con operador (todos cancelados),
    /// no rompe — el BC ya tiene su(s) Partial.</para>
    /// </summary>
    private async Task CompleteReusedDraftWithFullLinesIfPartialAsync(
        BookingCancellation existingBc, Reserva reserva, CancellationToken ct)
    {
        // La query de resolucion no trae las lineas: cargarlas para saber si hay Partial y para no duplicar.
        await _db.Entry(existingBc).Collection(b => b.Lines).LoadAsync(ct);

        bool hasPartialLine = existingBc.Lines.Any(l => l.Scope == BookingCancellationLineScope.Partial);
        if (!hasPartialLine)
            return; // draft de anulacion total normal (lineas Full o vacio): nada que completar.

        var fullLines = await BuildCancellationLinesAsync(
            reserva, BookingCancellationLineScope.Full, ct, throwIfNoOperatorService: false);

        foreach (var fullLine in fullLines)
        {
            bool alreadyHasLineForService = existingBc.Lines.Any(
                l => l.ServiceTable == fullLine.ServiceTable && l.ServiceId == fullLine.ServiceId);
            if (alreadyHasLineForService)
                continue;

            existingBc.Lines.Add(fullLine);
        }

        // El BC parcial nacio con AmountPaidAtCancellation/EstimatedRefundAmount en 0 (una cancelacion parcial
        // no estima refund del file). Al pasar a anulacion TOTAL lo emparejamos con un draft total fresco:
        // AmountPaidAtCancellation = suma de pagos activos de la reserva (mismo calculo que DraftAsync). Asi el
        // estimado de reembolso del file no queda en 0 por haber nacido de una parcial.
        //
        // FIX N2 (backend reviewer): este refresh corre SIEMPRE que el draft reusado sea parcial, INCLUSO si no
        // se agrego ninguna linea Full nueva (caso: todos los servicios vivos restantes ya estaban cancelados).
        // Aun sin lineas nuevas, la reserva puede tener pagos activos que el estimado de reembolso del file
        // debe reflejar — antes esto quedaba en 0 por salir temprano cuando no se agregaba ninguna linea.
        var amountPaid = await _db.Payments
            .Where(p => p.ReservaId == reserva.Id
                     && !p.IsDeleted
                     && p.Status != "Cancelled")
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        existingBc.AmountPaidAtCancellation = amountPaid;
        existingBc.EstimatedRefundAmount = amountPaid;

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// B1 (2026-06-03, FIX 1): transiciona un BC <c>ArcaRejected</c> (sin NC viva) a
    /// <c>Aborted</c> para destrabar la reserva. Deja traza de auditoria del intento
    /// fallido. NO emite nada fiscal (es un cambio de estado interno + ClosedAt).
    /// Precondicion ya verificada por el caller: <c>CreditNoteInvoiceId is null</c>.
    /// </summary>
    private async Task AutoAbortArcaRejectedAsync(
        BookingCancellation arcaRejectedBc,
        Reserva reserva,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        arcaRejectedBc.Status = BookingCancellationStatus.Aborted;
        arcaRejectedBc.ClosedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationAutoAbortedArcaRejected,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: arcaRejectedBc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                arcaRejectedBc.PublicId,
                ReservaPublicId = reserva.PublicId,
                previousStatus = nameof(BookingCancellationStatus.ArcaRejected),
                arcaErrorMessage = arcaRejectedBc.ArcaErrorMessage,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        _logger.LogInformation(
            "metric:cancellation_arca_rejected_auto_aborted | BcPublicId={BcPublicId} ReservaPublicId={ReservaPublicId} UserId={UserId}",
            arcaRejectedBc.PublicId, reserva.PublicId, userId);
    }

    /// <summary>
    /// B1 (2026-06-03, FIX 3): devuelve el PublicId del BC mas reciente de la reserva
    /// (post-resolucion: a esta altura del flujo ya es Aborted, porque (a) reuso y
    /// retorno, (b)/(c) lo dejaron en Aborted). Se usa para registrar el linaje de
    /// intentos en la auditoria del BC nuevo. Devuelve null si la reserva no tenia
    /// ningun BC previo (primer intento).
    /// </summary>
    private async Task<Guid?> ResolvePreviousBcPublicIdAsync(int reservaId, CancellationToken ct)
    {
        return await _db.BookingCancellations
            .Where(b => b.ReservaId == reservaId)
            .OrderByDescending(b => b.Id)
            .Select(b => (Guid?)b.PublicId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// B1 (2026-06-03, FIX 2): detecta unique_violation (SQLSTATE 23505) de PostgreSQL
    /// dentro de un SaveChanges* de EF Core. EF envuelve el error de Npgsql en
    /// DbUpdateException. Mismo patron que <c>InvoiceService.IsUniqueConstraintViolation</c>
    /// (el interceptor de invariantes solo traduce 23514/CHECK, no 23505/UNIQUE, asi
    /// que cada service maneja sus propias colisiones de unique).
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation;
    }

    /// <summary>
    /// ADR-042 §3.4 (2026-07-01): true si el BC tiene AL MENOS UNA hija con NC viva. "NC viva" = hija
    /// <c>Succeeded</c> (CAE aprobado) o <c>Pending</c> con una NC ya creada (<c>CreditNoteInvoiceId</c>
    /// seteado). Se usa para NUNCA liberar un BC que dejo una NC viva (blindaje fiscal contra doble NC).
    /// </summary>
    private async Task<bool> BcHasLiveCreditNoteChildAsync(int bookingCancellationId, CancellationToken ct)
    {
        return await _db.BookingCancellationCreditNotes
            .AnyAsync(c => c.BookingCancellationId == bookingCancellationId
                        && (c.Status == BookingCancellationCreditNoteStatus.Succeeded
                            || c.CreditNoteInvoiceId != null), ct);
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
                "Esta cancelación ya no se puede confirmar porque cambió de estado. Actualizá la página.",
                invariantCode: "INV-093");

        // 3) MIG2: la Invoice original ya esta anulada → bloquear.
        if (bc.OriginatingInvoice.AnnulmentStatus == AnnulmentStatus.Succeeded)
            throw new BusinessInvariantViolationException(
                "La factura original ya fue anulada (NC aprobada). No se puede confirmar la cancelacion.",
                invariantCode: "INV-100");

        // 4) Override admin: si IsAdminOverride=true, normalmente exige un InvariantOverride aprobado.
        ApprovalRequest? approvalRequest = null;
        // 2026-06-24: marca de que el Admin se auto-autorizo el override (saltea la doble firma). La usamos
        // mas abajo para que el bypass del approval de la NC (EnqueueAnnulmentAsync) tambien aplique, ya que
        // el override del BC quedo cubierto por el rol Admin en vez de por un approval formal.
        bool adminSelfAuthorizedOverride = false;
        if (request.IsAdminOverride)
        {
            if (string.IsNullOrWhiteSpace(request.OverrideReason) || request.OverrideReason.Trim().Length < 20)
                throw new BusinessInvariantViolationException(
                    "Para forzar la cancelación tenés que indicar un motivo de al menos 20 caracteres.");

            if (requesterIsAdmin)
            {
                // BYPASS Admin (2026-06-24): el Admin fuerza el override DIRECTO, sin EXIGIR un InvariantOverride
                // aprobado. Hoy el dueno es el unico Admin y pedirse doble firma a si mismo es teatro (se
                // auto-aprobaba). Exigimos el motivo (ya validado >= 20 chars arriba) y dejamos el audit
                // AdminSelfAuthorized para el contador. Condicionado SOLO al rol Admin: el dia que haya varios
                // admins se puede volver a exigir 4-eyes por policy.
                adminSelfAuthorizedOverride = true;

                // IMPORTANTE: si el Admin IGUAL trae un approval valido, lo cargamos (best-effort, sin throw).
                // Razon: la rama FC1.3 (servicios no-Hotel, INV-FC1.3-007) reutiliza este MISMO approvalRequest
                // para su propio override (Reason >= 50 chars). Ese es un gate fiscal DISTINTO que el task NO
                // pidio bypassear: si el Admin lo cubrio con un approval, debe seguir funcionando. Si no lo
                // trae, approvalRequest queda null y el bypass admin aplica al override base (multi-invoice);
                // la rama FC1.3 decidira por su cuenta si ese caso necesita approval.
                if (request.ApprovalRequestPublicId is not null)
                {
                    var candidate = await _db.ApprovalRequests
                        .FirstOrDefaultAsync(a => a.PublicId == request.ApprovalRequestPublicId, ct);
                    var candidateValid = candidate is not null
                        && candidate.RequestType == ApprovalRequestType.InvariantOverride
                        && candidate.EntityType == "BookingCancellation"
                        && candidate.EntityId == bc.Id
                        && candidate.Status == ApprovalStatus.Approved
                        && candidate.RequestedByUserId == userId
                        && candidate.ExpiresAt > DateTime.UtcNow;
                    if (candidateValid)
                        approvalRequest = candidate;
                }

                await LogAdminSelfAuthorizedAsync(
                    bypassedGate: "ConfirmCancellationInvariantOverride",
                    entityName: AuditActions.BookingCancellationEntityName,
                    entityId: bc.Id.ToString(),
                    reason: request.OverrideReason!.Trim(),
                    amount: bc.OriginatingInvoice?.ImporteTotal,
                    // El snapshot fiscal todavia no se completa en este punto (step 7); tomamos la moneda del
                    // request, que es la fuente de la que se construye CurrencyAtEvent mas abajo.
                    currency: request.SnapshotData?.CurrencyAtEvent,
                    userId: userId,
                    userName: userName,
                    ct: ct);
            }
            else
            {
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
                "No pudimos determinar la condición fiscal de la agencia, del operador o del cliente. " +
                "Revisá esos datos antes de confirmar la cancelación.",
                invariantCode: "INV-118");
        }

        // 6) Validar Source / ManualJustification:
        //    - Si Source=Manual, ManualJustification es obligatorio (INV-120).
        //    - Si Source=Unset, rechazar (no se puede confirmar sin TC explicito).
        if (request.SnapshotData.Source == ExchangeRateSource.Unset)
            throw new BusinessInvariantViolationException(
                "Falta indicar el tipo de cambio para poder confirmar la cancelación.",
                invariantCode: "INV-118");
        if (request.SnapshotData.Source == ExchangeRateSource.Manual &&
            string.IsNullOrWhiteSpace(request.SnapshotData.ManualJustification))
            throw new BusinessInvariantViolationException(
                "Cuando usás un tipo de cambio manual tenés que indicar una justificación.",
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

        // FASE 0 (2026-06-28): si en el Dia 0 ya se CONFIRMA una multa con monto, bajar el reembolso esperado del
        // operador igual que en el path diferido (mismo metodo). Guarda triple: flag ON + estado Confirmed + monto
        // > 0. Con el flag OFF, CaptureDebitNoteClassification hace short-circuit y PenaltyStatus queda en su default
        // (Estimated), asi que esta rama NO corre -> byte-identidad con el comportamiento previo a ADR-013/014.
        // ConfirmCancellationRequest no trae PenaltyCurrency, asi que la moneda se infiere de las lineas del operador.
        if (settings.EnableCancellationDebitNote
            && bc.PenaltyStatus == PenaltyStatus.Confirmed
            && request.ConfirmedPenaltyAmount is > 0m)
        {
            // El path Dia-0 SIEMPRE pasa requestedPenaltyCurrency: null (ConfirmCancellationRequest no trae moneda).
            // Por eso, un operador principal MULTIMONEDA en el Dia 0 es un no-op DELIBERADO (no se puede elegir moneda
            // sin ambiguedad -> el metodo no netea y lo loguea via metric:operator_refund_penalty_currency_ambiguous).
            // La sobreestimacion de ese caso raro se corrige recien con la confirmacion diferida, que SI puede traer
            // PenaltyCurrency explicita.
            await AllocateConfirmedPenaltyToLinesAsync(
                bc, request.ConfirmedPenaltyAmount.Value, requestedPenaltyCurrency: null, ct,
                userId: userId, userName: userName);
        }

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
                        "Por ahora la cancelación automática solo está disponible para reservas " +
                        "compuestas únicamente por hotelería. Esta reserva incluye otros servicios.",
                        invariantCode: "INV-FC1.3-007");
                }
                // Si llega aca el override cubre el caso, seguimos adelante.
            }

            // (b) Cargar items + supplier necesarios para el calculator.
            var invoiceItems = await _db.Set<InvoiceItem>()
                .Where(i => i.InvoiceId == bc.OriginatingInvoiceId)
                .ToListAsync(ct);
            // FUGA B3 data-exposure (2026-07-03): mensaje al usuario SIN ids/GUIDs internos; detalle al log.
            var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == bc.SupplierId, ct);
            if (supplier is null)
            {
                _logger.LogError("Confirm: no se encontro el Supplier {SupplierId} del BC {BcPublicId}.",
                    bc.SupplierId, bc.PublicId);
                throw new InvalidOperationException(
                    "No se encontró el operador de esta anulación. Consultá con administración.");
            }

            // (c) Armar input. OriginalInvoiceAmount queda en ImporteTotal (es la base fiscal del
            // comprobante original, no cambia). CancellationAmount, en cambio, ADR-044 T5 Addendum fix
            // B1(a) (2026-07-11): se acota al REMANENTE acreditable de la factura, no al ImporteTotal a
            // secas. Antes, si esta reserva ya tenia una NC PARCIAL previa contra la misma factura (T5,
            // cancelar 1 servicio), este camino legacy ("Anular el resto") seguia clasificando/acreditando
            // el TOTAL completo -> la suma de NCs (la parcial previa + esta) superaba lo que la factura vale
            // (descuadre fiscal). Cuando NO hubo ninguna NC parcial previa (el 100% de los casos de hoy),
            // remanente == ImporteTotal -> comportamiento byte-identico al actual, cero regresion.
            //
            // excludeBookingCancellationId: bc.Id (FRENTE E, anti-doble-cap): este es el camino de anulacion
            // TOTAL. Si este mismo BC absorbio una linea PARCIAL previa (el anular-total completa el draft
            // parcial), esa reserva por-linea NO debe restarse contra la anulacion de SU PROPIO BC — la
            // parcial nunca emitio su NC, y el total acredita la factura ENTERA. Excluir el propio BC hace que
            // el remanente que ve este camino sea el ImporteTotal pleno.
            //
            // Sobre la concurrencia del cap en ESTE camino legacy (M1/C2 del re-review): el lock envuelve solo
            // la LECTURA del remanente, no la escritura de la NC. Es seguro por los DOS indices unicos parciales
            // (IX_BookingCancellations_ReservaId e IX_..._OriginatingInvoiceId, filtro Status NOT IN (4,6)):
            // garantizan como maximo UN BC vivo por reserva Y por factura, asi que no puede haber DOS eventos
            // concurrentes leyendo el mismo remanente "libre" de la misma factura — el segundo INSERT de BC lo
            // rechaza el unico. Ver el test de integracion que fija esta imposibilidad. Fallback (si el
            // argumento del unico no bastara): envolver leer-decidir-emitir en el lock (mas caro).
            var remainingCreditableAmount = await RunUnderInvoiceLockAsync(
                bc.OriginatingInvoiceId,
                () => ComputeInvoiceRemainingCreditableAmountAsync(bc.OriginatingInvoiceId, ct, excludeBookingCancellationId: bc.Id),
                ct);

            var calculatorInput = new FiscalLiquidationInput(
                OriginatingInvoice: bc.OriginatingInvoice,
                Items: invoiceItems,
                Supplier: supplier,
                InvoicingModeAtEvent: bc.FiscalSnapshot.InvoicingModeAtEvent,
                OriginalInvoiceAmount: bc.OriginatingInvoice.ImporteTotal,
                CancellationAmount: remainingCreditableAmount,
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
                // FUGA B2 data-exposure (2026-07-03): el mensaje viaja al usuario via SanitizedConflict —
                // sin flags/tipos internos (CreditNoteKind, EnablePartialCreditNotes). Detalle tecnico al log.
                _logger.LogWarning(
                    "Confirm BC {BcPublicId}: calculator devolvio CreditNoteKind=TotalPlusNewInvoice " +
                    "(case {Case}, motivos {Motivos}) — requiere FC1.3 Fase 2 / flujo legacy.",
                    bc.PublicId, liquidation.Case, liquidation.ReviewRequiredReason);
                throw new InvalidOperationException(
                    "Este caso fiscal todavía no se puede resolver automáticamente y requiere revisión manual. " +
                    "Consultá con administración.");
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

        // 7-bis) ADR-025 (DT.7 / riesgo fiscal medio): guard MonCotiz para factura USD legacy en el path
        //        de NC TOTAL. El path de NC parcial ya tiene su guard (foreign + rate <=0 o ==1 -> manual,
        //        F2.5). El path de NC total dejaba MonId/MonCotiz en su default PES/1 ("MVP: solo ARS"):
        //        una NC total sobre una factura en moneda extranjera SIN cotizacion confiable en el
        //        snapshot saldria con MonCotiz=1 (error fiscal grave). En ese caso NO emitimos: rechazamos
        //        con un mensaje que rutea la operacion a gestion manual (el operador la emite a mano).
        //        Se materializa ANTES de transicionar para no dejar el BC a medio confirmar.
        if (IsForeignCurrencyInvoiceWithoutReliableRate(bc))
        {
            _logger.LogCritical(
                "ADR-025 ABORT NC total - factura USD legacy sin cotizacion confiable en el snapshot " +
                "(rate<=0 o ==1). No se emite para evitar MonCotiz=1. bcId={BcId}, invoiceId={InvoiceId}.",
                bc.Id, bc.OriginatingInvoiceId);

            throw new BusinessInvariantViolationException(
                "La factura original esta en moneda extranjera pero no tiene una cotizacion confiable " +
                "registrada. La nota de credito saldria con cotizacion 1 (error fiscal). Gestionala " +
                "manualmente: revisa/recarga la cotizacion de la factura original antes de cancelar.",
                invariantCode: "INV-156");
        }

        // 7-ter) ADR-042 §3.5 step 1 (2026-07-01): PRE-FLIGHT multi-factura, ANTES de la transaccion y de
        //         encolar nada. Se listan TODAS las facturas de venta vivas con CAE de la reserva y se valida
        //         CADA UNA (todo-o-nada al frente): si alguna extranjera tiene cotizacion sospechosa (TC<=0 o
        //         ==1) o moneda no soportada, NO se emite NINGUNA NC (INV-156). Generaliza el guard de la
        //         factura principal (step 7-bis, snapshot) a todas las facturas por su propio MonId/MonCotiz.
        //         En el caso mono-factura devuelve la unica factura (byte-equivalente).
        var invoicesToAnnul = await ResolveAndPreflightInvoicesToAnnulAsync(bc.ReservaId, ct);

        // ===================================================================
        // ARREGLO 3 (2026-06-24, integridad): confirmacion de anulacion "todo o nada".
        //
        //   Hasta hoy los pasos 8-12 encadenaban varios cambios (estado de la reserva + servicios a Cancelado
        //   + recalculos del ARREGLO 1 + consumir la autorizacion + auditoria) con VARIOS SaveChangesAsync; y
        //   ojo: LogBusinessEventAsync hace su PROPIO commit (flushea todo el ChangeTracker). Si el proceso se
        //   cortaba a mitad, quedaba a medias (ej. reserva anulada y servicios cancelados pero sin NC).
        //
        //   FIX: envolvemos toda la SECUENCIA DE ESCRITURA (estado + servicios + recalculos + consumir
        //   approval + auditoria) en UNA transaccion (patron EF del proyecto: IExecutionStrategy.ExecuteAsync
        //   + BeginTransactionAsync, igual que el camino FC4 de saldo a favor en ClientCreditService). Dentro
        //   de la transaccion la auditoria va por StageBusinessEvent (NO commitea) para entrar en el MISMO
        //   commit. Asi: o se anula TODO, o no se toca nada.
        //
        //   CRITICO con la Nota de Credito: EnqueueAnnulmentAsync NO va dentro de la transaccion. Internamente
        //   programa el job de AFIP en Hangfire (_backgroundJobClient.Enqueue, NO transaccional): si corriera
        //   dentro y la transaccion hiciera rollback, el job quedaria agendado y correria sobre datos
        //   revertidos. Por eso se encola DESPUES del commit exitoso. El invariante es: "o se anulo todo Y se
        //   encolo la NC, o no se toco nada". El pre-guard de moneda (step 7-bis) ya corrio ANTES de la
        //   transaccion, asi que el caso de rechazo conocido no deja la cancelacion a medias.
        //
        //   InMemory (tests unit) NO soporta transacciones, por eso ramificamos por IsRelational() — mismo
        //   criterio que ClientCreditService. En InMemory ejecutamos el mismo cuerpo sin transaccion envolvente
        //   (la atomicidad real se valida en integracion Postgres).
        // ===================================================================

        // Datos para encolar la NC, resueltos ANTES de la transaccion (no dependen de la persistencia).
        //     Bypass del approval del InvoiceAnnulment (requesterIsAdmin del InvoiceService, NO confundir con
        //     el parametro homonimo de ConfirmAsync): SOLO cuando el override del BC ya cubre la NC fiscal
        //     (approvalRequest != null) o el Admin se auto-autorizo (adminSelfAuthorizedOverride). Coherente
        //     con InvoiceService.EnqueueAnnulmentAsync (saltea su propio approval cuando el caller es Admin).
        //     Un caller NO-admin sin override sigue necesitando approval -> nunca emite NC sin control fiscal
        //     (OPS-FISCAL-001 plan v3 §13).
        bool bypassNcApproval = approvalRequest != null || adminSelfAuthorizedOverride;
        var crossRefReason = approvalRequest != null
            ? $"BC override {approvalRequest.PublicId}: {request.OverrideReason!.Trim()}"
            : adminSelfAuthorizedOverride
                ? $"BC admin self-authorized override: {request.OverrideReason!.Trim()}"
                : $"BC cancellation: {bc.Reason}";

        // N10 (ADR-042, 2026-07-02): SOLO para el caso MULTI-FACTURA, resolver el approval del InvoiceAnnulment
        // UNA sola vez ANTES de la transaccion. Motivo: el loop de EnqueueAnnulmentAsync es POST-commit; si una
        // factura secundaria tirara ApprovalRequiredException a mitad del loop, el BC ya quedo committeado con
        // hijas Pending sin job (feed de B1). La regla de negocio (sancionada 2026-07-02): la autorizacion para
        // anular la RESERVA cubre las N facturas de esa anulacion — no se pide un approval por cada comprobante.
        //  - Si el requester ya puede bypassear (override del BC o Admin), nada que hacer.
        //  - Si NO puede y el annulment requiere approval, EXIGIMOS aca (pre-commit) UNA autorizacion sobre la
        //    factura principal; si existe, bypasseamos el re-check por-factura del loop y la usamos como
        //    cross-reference fiscal de todas; si no existe, tiramos ApprovalRequiredException ANTES de tocar
        //    nada (el front pide la aprobacion y re-confirma). Nunca una excepcion de aprobacion post-commit.
        //
        // El caso MONO-factura NO pasa por aca: hay un unico enqueue, y su gate de approval lo resuelve
        // EnqueueAnnulmentAsync como siempre (comportamiento byte-identico al previo a ADR-042; no se toca).
        int? annulmentApprovalId = approvalRequest?.Id;
        if (invoicesToAnnul.Count > 1 && !bypassNcApproval)
        {
            // BUG FIX (2026-07-02): resolver "¿requiere approval?" IGUAL que InvoiceService.EnqueueAnnulmentAsync
            // (:1144-1155): via la ApprovalPolicy configurable por Admin (B1.15 Fase B''), con el setting global
            // como MERO fallback. Usar el setting CRUDO rompia el dogfood: en prod la policy dice "no requiere"
            // (por eso el mono-factura siempre funciono) pero el setting legacy quedo en true, y el pre-check
            // multi exigia un approval que la politica real no pide -> 409 a un Admin que nunca lo necesito.
            bool requiresApproval;
            if (_approvalPolicyService is not null)
            {
                requiresApproval = await _approvalPolicyService.RequiresApprovalAsync(
                    ApprovalRequestType.InvoiceAnnulment,
                    fallback: settings.RequireApprovalForInvoiceAnnulment,
                    ct);
            }
            else
            {
                // Sin policy inyectada (unit tests que construyen el service a mano): fallback al setting.
                requiresApproval = settings.RequireApprovalForInvoiceAnnulment;
            }

            if (requiresApproval)
            {
                var principalInvoiceId = bc.OriginatingInvoiceId;
                var annulmentApproval = await _approvalService.FindActiveApprovedAsync(
                    ApprovalRequestType.InvoiceAnnulment, "Invoice", principalInvoiceId, userId, ct);
                if (annulmentApproval is null)
                    throw new ApprovalRequiredException(
                        ApprovalRequestType.InvoiceAnnulment, "Invoice", principalInvoiceId);

                // Autorizacion encontrada: cubre la anulacion de la reserva completa (sus N facturas).
                annulmentApprovalId = annulmentApproval.Id;
                bypassNcApproval = true;
            }
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                await PersistConfirmationCoreAsync();
                await transaction.CommitAsync(ct);
            });
        }
        else
        {
            // InMemory: sin transaccion envolvente (los tests de atomicidad real viven en integracion Postgres).
            await PersistConfirmationCoreAsync();
        }

        // 10) BR-V2-03 cross-reference: encolar la anulacion en AFIP. DESPUES del commit (ver bloque ARREGLO 3
        //     arriba): el job de Hangfire NO debe quedar agendado si la transaccion revierte.
        //
        //     ADR-042 §3.5 step 3: un EnqueueAnnulmentAsync POR CADA factura (una NC por factura). Cada job
        //     POSTea a ARCA con su idempotency key por-comprobante. Para el caso mono-factura es un unico
        //     enqueue = byte-equivalente.
        //
        //     B1 (2026-07-02): CADA iteracion va en try/catch. El BC ya esta committeado con sus hijas Pending;
        //     si un enqueue falla (red, Hangfire, etc.) NO debe abortar el loop (dejando las demas facturas sin
        //     encolar) ni escapar como excepcion (dejaria la reserva trabada con la NC parcial ya emitida). La
        //     hija fallida queda Pending SIN job: el endpoint retry-credit-notes la re-encola (idempotente). El
        //     approval ya se resolvio pre-commit (N10), asi que aca NO puede saltar ApprovalRequiredException.
        foreach (var invoice in invoicesToAnnul)
        {
            try
            {
                await _invoiceService.EnqueueAnnulmentAsync(
                    id: invoice.Id,
                    userId: userId,
                    userName: userName,
                    reason: crossRefReason,
                    requesterIsAdmin: bypassNcApproval,
                    ct: ct,
                    approvalRequestId: annulmentApprovalId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // La hija quedo Pending sin job -> recuperable por retry-credit-notes. Log + seguir con las demas.
                _logger.LogError(ex,
                    "ADR-042: fallo al encolar la anulacion de la factura {InvoiceId} para BC {BcPublicId} " +
                    "(la hija queda Pending, recuperable con retry-credit-notes).",
                    invoice.Id, bc.PublicId);
            }
        }

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

        // ===================================================================
        // ARREGLO 3: cuerpo comun de la secuencia de escritura de la confirmacion. Definido como local
        // function para reusarlo dentro y fuera de la transaccion envolvente sin duplicar codigo. TODAS las
        // SaveChanges de aca dentro (la propia, las de los persisters del ARREGLO 1, la de MarkConsumedAsync)
        // participan de la transaccion ambiente cuando existe, asi un fallo en cualquier paso revierte TODO.
        // NO incluye EnqueueAnnulmentAsync a proposito (el job de AFIP se agenda DESPUES del commit).
        // ===================================================================
        async Task PersistConfirmationCoreAsync()
        {
            // 8) Transicionar BC + Reserva (HC2 plan v3: bypass UpdateStatusAsync — el state machine general
            //    no contempla la transicion lateral a PendingOperatorRefund, lo hacemos directo).
            bc.Status = BookingCancellationStatus.AwaitingFiscalConfirmation;
            bc.ConfirmedWithClientAt = DateTime.UtcNow;
            bc.ConfirmedByUserId = userId;
            bc.ConfirmedByUserName = userName;
            bc.OperatorRefundDueBy = DateTime.UtcNow.AddDays(settings.OperatorRefundTimeoutDays);

            // 8-pre) ADR-042 §3.5 step 2: persistir UNA fila hija Pending por factura a anular, en el mismo
            //        commit que la transicion. La completitud (todas OK / parcial / todas fallan) se decide
            //        contando estas hijas en los callbacks de ARCA. Defensivo: no duplicar si ya existe una
            //        hija para esa factura (un draft reusado no tiene hijas, pero el guard es barato).
            foreach (var invoice in invoicesToAnnul)
            {
                bool alreadyHasChild = bc.CreditNotes.Any(c => c.OriginatingInvoiceId == invoice.Id);
                if (alreadyHasChild) continue;

                bc.CreditNotes.Add(new BookingCancellationCreditNote
                {
                    OriginatingInvoiceId = invoice.Id,
                    ArcaCurrency = string.IsNullOrWhiteSpace(invoice.MonId) ? "PES" : invoice.MonId,
                    Status = BookingCancellationCreditNoteStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            // HC2 plan v3 §6.1 step 5: bypass UpdateStatusAsync porque AllowedRevertTransitions no contempla
            // esta salida. La transicion queda visible en el audit log + la query de Reservas filtra por status.
            // Transición de estado + rastro auditable + descarte de la marca "confirmada con cambios" por el PUNTO
            // ÚNICO de transición. Antes: set a mano + LogReservaStatusChange + Discard; ahora unificado (la regla de
            // limpieza para PendingOperatorRefund apaga la marca + borra el detalle de cambios). Atómico con la
            // transición (el caller cierra el SaveChanges).
            await ReservaStatusTransitioner.ApplyAsync(
                _db, bc.Reserva, EstadoReserva.PendingOperatorRefund, "Forward",
                userId, userName, "Cancelacion (ADR-002): confirmada con el cliente, a la espera del reembolso del operador.", ct);

            // 8-bis) CAMBIO 2 (2026-06-24): anulacion TOTAL -> marcar TODOS los servicios de la reserva como
            //        Cancelado, en la MISMA transaccion que la transicion de estado (atomico). Idempotente.
            await CancelAllReservaServicesAsync(bc.ReservaId, userId, userName, ct);

            // 8-bis.5) Persistir el estado de la reserva + los servicios cancelados ANTES de recalcular. Los
            //          persisters (SupplierDebtPersister/ReservaMoneyPersister) leen con AsNoTracking, es decir
            //          desde la BASE, no desde el ChangeTracker; si no guardamos primero, recalcularian sobre los
            //          servicios todavia "Confirmados" y la deuda no bajaria (mismo orden que CancelServiceAsync,
            //          que guarda el servicio cancelado antes de recalcular). Dentro de la transaccion -> atomico.
            await _db.SaveChangesAsync(ct);

            // 8-ter) ARREGLO 1 (2026-06-24): recalcular deuda de los operadores + plata del cliente + comision
            //        EN EL MISMO request, ahora que los servicios quedaron Cancelado. Antes esto quedaba para
            //        el job de AFIP (que solo tocaba la plata del cliente) -> la deuda con el operador quedaba
            //        inflada y la comision colgada. Reusa los persisters existentes (no inventa calculo).
            await RecalculateMoneyAfterTotalCancellationAsync(bc.ReservaId, userId, userName, ct);

            // 9) Sellar approval consumido + audit stageado. (El estado BC/Reserva + servicios ya se guardaron
            //    en 8-bis.5; los persisters del ARREGLO 1 ya guardaron lo suyo.)
            //
            // 11) Marcar el InvariantOverride como Consumed si hubo override. Dentro de la transaccion: si el
            //     commit falla, el approval NO queda consumido (se puede reintentar). MarkConsumedAsync hace su
            //     propio SaveChanges sobre el mismo DbContext -> participa de la transaccion ambiente.
            if (approvalRequest != null)
            {
                await _approvalService.MarkConsumedAsync(approvalRequest.Id, ct);
            }

            // 12) Audit. STAGEADO (no commitea) para entrar en el MISMO commit que la mutacion (ARREGLO 3).
            //     Antes era LogBusinessEventAsync, que hacia su propio commit y rompia la atomicidad.
            _auditService.StageBusinessEvent(
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
                userName: userName);

            // SaveChanges final del estado BC/Reserva + el audit stageado, todo en la misma transaccion.
            await _db.SaveChangesAsync(ct);
        }
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
                "Esta cancelación ya no se puede abortar en este momento.",
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

    // =========================================================================
    // ADR-041 TANDA 4 (2026-06-28) + FIX A (2026-07-04): reembolso TARDIO del operador.
    //
    // AbandonedByOperator era TERMINAL: si el operador devolvia plata despues del plazo, no habia forma de
    // registrarla por sistema. Este metodo es la transicion CONTROLADA que reabre el circuito: vuelve la
    // cancelacion a AwaitingOperatorRefund (con plazo nuevo) para que el cashier registre + impute el ingreso
    // con el circuito normal de allocation (que genera saldo a favor del CLIENTE). La reserva sigue Cancelled.
    //
    // FIX A (2026-07-04): ademas de AbandonedByOperator, ahora tambien reabre una cancelacion CERRADA CON RESIDUO
    // real (el operador reembolso de menos, el cliente consumio su saldo y la BC se cerro, pero el operador
    // TODAVIA debe plata — el extracto lo muestra como "me tiene que devolver" para siempre). Antes no habia forma
    // de registrar ese ingreso tardio (INV-093 rechaza Closed). Ahora se reabre igual que una abandonada, con la
    // MISMA fuente unica del receivable para decidir que hay residuo real. La reserva sigue Cancelled en ambos casos.
    // =========================================================================

    /// <inheritdoc />
    public async Task<BookingCancellationDto> ReopenAbandonedForLateRefundAsync(
        Guid publicId,
        string reason,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
        {
            throw new ArgumentException(
                "El motivo de la reapertura por reembolso tardio es obligatorio (minimo 10 caracteres).",
                nameof(reason));
        }

        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // Idempotencia: si ya esta abierta (esperando o recibiendo refund) NO hay nada que reabrir. Devolvemos el
        // estado actual sin re-auditar — un doble click o un retry no debe generar otro evento de reapertura.
        if (bc.Status == BookingCancellationStatus.AwaitingOperatorRefund
            || bc.Status == BookingCancellationStatus.ClientCreditApplied)
        {
            _logger.LogInformation(
                "ReopenAbandonedForLateRefundAsync no-op: BC {BcPublicId} ya esta abierta ({Status}).",
                bc.PublicId, bc.Status);
            return (await MapToDtoAsync(bc.Id, ct))!;
        }

        // Se reabre desde DOS estados terminales del circuito del operador:
        //   1) AbandonedByOperator: el job de timeout la dio por perdida. Siempre reabrible (caso ADR-041 T4).
        //   2) Closed CON RESIDUO real (FIX A): el operador reembolso de menos y la BC ya se cerro, pero todavia
        //      debe plata (receivable vivo > 0 con la MISMA formula del extracto). Sin residuo NO se reabre: no
        //      hay nada que el operador deba devolver, cerrada esta bien cerrada.
        // Cualquier otro estado (Drafted, Aborted, AwaitingFiscalConfirmation, ArcaRejected, ManualReview*) NO es
        // un caso de reembolso tardio.
        bool reopenableFromAbandoned = bc.Status == BookingCancellationStatus.AbandonedByOperator;
        bool reopenableFromClosedWithResidue = false;
        if (bc.Status == BookingCancellationStatus.Closed)
        {
            // El residuo se calcula con la fuente unica del extracto (bc.Reserva ya viene incluida arriba).
            var liveReceivable = await ComputeLiveOperatorReceivableAsync(bc, ct);
            reopenableFromClosedWithResidue = liveReceivable > 0m;
        }

        if (!reopenableFromAbandoned && !reopenableFromClosedWithResidue)
        {
            // Mensaje distinto para "cerrada sin nada pendiente" (mas claro para el usuario) vs el resto.
            if (bc.Status == BookingCancellationStatus.Closed)
            {
                throw new BusinessInvariantViolationException(
                    "Esta cancelación ya está cerrada y el operador no tiene nada pendiente de devolver.",
                    invariantCode: "INV-093");
            }

            throw new BusinessInvariantViolationException(
                "Esta cancelación solo se puede reabrir por un reembolso tardío cuando quedó sin reembolso del operador.",
                invariantCode: "INV-093");
        }

        var settings = await _settings.GetEntityAsync(ct);
        var previousStatus = bc.Status;

        // Reapertura: vuelve a esperar el reintegro del operador. Plazo NUEVO (ahora + timeout) para que el job de
        // timeout (ProcessExpiredOperatorRefunds) no la re-abandone esta misma noche por el plazo viejo ya vencido.
        bc.Status = BookingCancellationStatus.AwaitingOperatorRefund;
        bc.ClosedAt = null;
        bc.OperatorRefundDueBy = DateTime.UtcNow.AddDays(settings.OperatorRefundTimeoutDays);

        // La RESERVA NO se toca: el viaje sigue cancelado (Cancelled). Vale para AMBOS caminos (una abandonada
        // tenia la reserva Cancelled; una cerrada tambien). El reembolso tardio se vuelve saldo a favor del cliente
        // recien cuando el cashier lo imputa (AllocateAsync), no por reabrir la cancelacion. Cuando el cliente
        // consuma ese saldo, la BC vuelve a Closed por la via normal (OnAllCreditConsumedAsync).

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationReopenedForLateRefund,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                reservaPublicId = bc.Reserva?.PublicId,
                previousStatus = previousStatus.ToString(),
                newOperatorRefundDueBy = bc.OperatorRefundDueBy,
                reason = reason.Trim(),
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "BC {BcPublicId}: REABIERTA por reembolso tardio ({PreviousStatus} -> AwaitingOperatorRefund). " +
            "Nuevo plazo {DueBy:o}. La reserva sigue Cancelled. ActorUserId={UserId}",
            bc.PublicId, previousStatus, bc.OperatorRefundDueBy, userId);

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
                "Esta acción no está disponible para el estado actual de la cancelación.",
                invariantCode: "INV-093");

        // 4) Validar la NC referenciada.
        var creditNote = await _db.Invoices
            .FirstOrDefaultAsync(i => i.PublicId == request.CreditNoteInvoicePublicId, ct)
            ?? throw new InvalidOperationException(
                $"La Invoice {request.CreditNoteInvoicePublicId} no existe.");

        // ADR-042 §3.5.3 (2026-07-01): Force opera POR-HIJA. Cargamos las hijas del BC y localizamos la que
        // corresponde a la factura origen de la NC forzada. Si el BC no tiene hijas (legacy pre-backfill),
        // la NC debe corresponder al puntero singular (comportamiento historico).
        var children = await _db.BookingCancellationCreditNotes
            .Where(c => c.BookingCancellationId == bc.Id)
            .ToListAsync(ct);

        // NC tipos: 3 (NC A), 8 (NC B), 13 (NC C).
        var ncTipos = new[] { 3, 8, 13 };
        bool ncBasicsValid = creditNote.OriginalInvoiceId != null
                     && ncTipos.Contains(creditNote.TipoComprobante)
                     && creditNote.Resultado == "A"
                     && !string.IsNullOrWhiteSpace(creditNote.CAE);

        // La NC tiene que anular una factura de ESTA cancelacion: una hija con esa OriginatingInvoiceId,
        // o (legacy sin hijas) el puntero singular del BC.
        var matchingChild = children.FirstOrDefault(c => c.OriginatingInvoiceId == creditNote.OriginalInvoiceId);
        bool ncTargetsThisBc = children.Count > 0
            ? matchingChild is not null
            : creditNote.OriginalInvoiceId == bc.OriginatingInvoiceId;

        if (!ncBasicsValid || !ncTargetsThisBc)
            throw new InvalidOperationException(
                "La Invoice referenciada no es una NC valida de una factura de esta cancelacion " +
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

        // 5-bis) ADR-042 §3.5.3 (B2, 2026-07-02): Force POR-HIJA BAJO EL LOCK PESIMISTA del padre, con recuento
        //        FRESCO de BD. Antes se mutaba la hija y se contaba sobre la lista cargada en memoria (STALE):
        //        un callback intercalado podia dejar A y B Succeeded en BD pero el BC atascado en
        //        AwaitingFiscalConfirmation (el lost-update que el lock previene; la rama StillPending del
        //        callback no toca el xmin del padre, asi que la concurrencia optimista no salvaba). Ahora la
        //        mutacion de la hija + la reevaluacion corren serializadas por el mismo FOR UPDATE que los
        //        callbacks, guardado sobre Status == AwaitingFiscalConfirmation dentro del lock.
        if (matchingChild is not null)
        {
            var forceOutcome = await RunUnderParentLockAsync(bc.Id, async () =>
            {
                // Re-cargar la hija FRESH dentro del lock, exigiendo que el BC siga AwaitingFiscalConfirmation.
                // Si un callback ya lo avanzo entre la validacion y el lock, no re-transicionamos (NoOp).
                var lockedChild = await _db.BookingCancellationCreditNotes
                    .Include(c => c.BookingCancellation)
                        .ThenInclude(b => b.Reserva)
                    .FirstOrDefaultAsync(c => c.Id == matchingChild.Id
                        && c.BookingCancellation.Status == BookingCancellationStatus.AwaitingFiscalConfirmation, ct);
                if (lockedChild is null)
                    return MultiNcOutcome.NoOp;

                var lockedBc = lockedChild.BookingCancellation;
                if (lockedChild.Status != BookingCancellationCreditNoteStatus.Succeeded)
                {
                    lockedChild.Status = BookingCancellationCreditNoteStatus.Succeeded;
                    lockedChild.CreditNoteInvoiceId = creditNote.Id;
                }
                lockedBc.ArcaConfirmedManuallyAt = DateTime.UtcNow;
                lockedBc.ArcaConfirmedManuallyByUserId = userId;
                await _db.SaveChangesAsync(ct);

                // Reevaluacion con conteo FRESCO (mismo core que los callbacks): StillPending, AllSucceeded o
                // PartialFailed. Setea status/principal/reserva segun corresponda.
                return await ReevaluateBcCompletenessAndTransitionAsync(lockedBc, lockedChild.OriginatingInvoiceId, ct);
            }, ct);

            switch (forceOutcome)
            {
                case MultiNcOutcome.NoOp:
                    _logger.LogWarning(
                        "ForceArcaConfirmationAsync: el flujo automatico ya cerro el BC {BcPublicId} bajo el lock. No-op.",
                        bc.PublicId);
                    return (await MapToDtoAsync(bc.Id, ct))!;

                case MultiNcOutcome.StillPending:
                    // Se forzo UNA NC pero faltan otras: la anulacion NO se cierra (todo-o-nada). Rastro + return.
                    await _auditService.LogBusinessEventAsync(
                        action: AuditActions.BookingCancellationArcaConfirmedManually,
                        entityName: AuditActions.BookingCancellationEntityName,
                        entityId: bc.Id.ToString(),
                        details: JsonSerializer.Serialize(new
                        {
                            bc.PublicId,
                            forcedCreditNoteInvoiceId = creditNote.Id,
                            forcedOriginatingInvoiceId = creditNote.OriginalInvoiceId,
                            request.Reason,
                            manuallyConfirmedByUserId = userId,
                            stillPending = true,
                        }),
                        userId: userId,
                        userName: userName,
                        ct: ct);
                    _logger.LogInformation(
                        "metric:cancellation_force_arca_partial | BcPublicId={BcPublicId} AdminUserId={AdminUserId}",
                        bc.PublicId, userId);
                    return (await MapToDtoAsync(bc.Id, ct))!;

                case MultiNcOutcome.PartialFailed:
                    // Alguna hija quedo Failed: forzar una sola no cierra la anulacion (el reevaluate dejo
                    // ArcaRejected). El admin debe reintentar/forzar las falladas.
                    throw new BusinessInvariantViolationException(
                        "No se puede completar la anulacion: hay notas de credito rechazadas por AFIP. " +
                        "Reintentá las notas de credito faltantes antes de forzar el cierre.",
                        invariantCode: "INV-093");

                case MultiNcOutcome.AllSucceeded:
                    // El reevaluate YA seteo Status=AwaitingOperatorRefund + puntero principal + reserva. Falta
                    // el cierre pesado (servicios cancelados + recalculo + consumir approval + audit dedicado).
                    await FinalizeForceCloseAsync(bc, approval, creditNote, request.Reason, userId, userName, ct);
                    _logger.LogInformation(
                        "metric:cancellation_force_arca_executed | BcPublicId={BcPublicId} AdminUserId={AdminUserId}",
                        bc.PublicId, userId);
                    return (await MapToDtoAsync(bc.Id, ct))!;
            }
        }

        // 6) LEGACY (sin hijas): transicion fiscal manual DIRECTA (comportamiento historico, single-factura). El
        //    puntero principal es la NC forzada. Sin lock: es el mismo camino sin hijas que el callback legacy.
        bc.Status = BookingCancellationStatus.AwaitingOperatorRefund;
        bc.CreditNoteInvoiceId = creditNote.Id;
        bc.ArcaConfirmedManuallyAt = DateTime.UtcNow;
        bc.ArcaConfirmedManuallyByUserId = userId;

        // (2026-07-03) ¿Cerrar directo por no haber reembolso pendiente del operador? (mismo criterio que los
        // callbacks automaticos). Usamos la guarda anti-timing por RefundCap (no depende de que el servicio ya este
        // Cancelado), asi es correcto aunque FinalizeForceCloseAsync recien cancele los servicios mas abajo.
        // FinalizeForceCloseAsync corre igual (cancela servicios + recalcula plata + consume el approval); no lee
        // bc.Status, asi que dejar el BC en Closed es compatible.
        if (await ShouldAutoCloseWithoutOperatorRefundAsync(bc, ct))
        {
            await ApplyAutoCloseWithoutOperatorRefundAsync(bc, origin: "transicion", ct);
        }
        else
        {
            // Transición + rastro + descarte de la marca "confirmada con cambios" por el PUNTO ÚNICO de transición
            // (escape hatch fiscal manual). La regla de limpieza para PendingOperatorRefund apaga la marca.
            await ReservaStatusTransitioner.ApplyAsync(
                _db, bc.Reserva, EstadoReserva.PendingOperatorRefund, "Forward",
                userId, userName, "Cancelacion (ADR-002): confirmacion fiscal forzada por admin, a la espera del reembolso del operador.", ct);
        }

        await FinalizeForceCloseAsync(bc, approval, creditNote, request.Reason, userId, userName, ct);

        _logger.LogInformation(
            "metric:cancellation_force_arca_executed | BcPublicId={BcPublicId} AdminUserId={AdminUserId}",
            bc.PublicId, userId);

        return (await MapToDtoAsync(bc.Id, ct))!;
    }

    /// <summary>
    /// ADR-042 §3.5.3 (2026-07-02): pasos PESADOS del cierre de una confirmacion forzada (Force), compartidos
    /// por el camino multi-factura (tras el lock) y el legacy. Cancela todos los servicios, recalcula la plata,
    /// consume el approval y deja el audit dedicado <c>ArcaConfirmedManually</c>. Corre FUERA del lock (I/O +
    /// SaveChanges propios). Precondicion: el BC ya quedo en <c>AwaitingOperatorRefund</c> con su puntero principal.
    /// </summary>
    private async Task FinalizeForceCloseAsync(
        BookingCancellation bc, ApprovalRequest approval, Invoice creditNote,
        string? reason, string userId, string? userName, CancellationToken ct)
    {
        // Servicios -> Cancelado (idempotente; ademas cubre BCs legacy confirmados antes de CAMBIO 2).
        await CancelAllReservaServicesAsync(bc.ReservaId, userId, userName, ct);
        // Persistir estado + servicios ANTES de recalcular (los persisters leen AsNoTracking desde la base).
        await _db.SaveChangesAsync(ct);
        // Recalcular deuda de operadores + plata del cliente + comision (determinista/idempotente).
        await RecalculateMoneyAfterTotalCancellationAsync(bc.ReservaId, userId, userName, ct);
        // Consumir el approval del override.
        await _approvalService.MarkConsumedAsync(approval.Id, ct);
        // Audit dedicado para discriminar manual vs automatico.
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
                reason,
                manuallyConfirmedByUserId = userId,
            }),
            userId: userId,
            userName: userName,
            ct: ct);
        await _db.SaveChangesAsync(ct);
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
        //
        // ADR-033 (2026-06-18): ademas del Status del BC, esta via puede CERRAR
        // la reserva si el operador ya reembolso el total esperado. Por eso
        // incluimos la Reserva (la necesitamos para flipear su Status) — igual
        // que OnAllocationVoidedAsync.
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
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

        // ADR-033: DESPUES de la transicion del BC, evaluar si la cancelacion
        // quedo TOTALMENTE reembolsada por el operador. Si es asi, cerramos la
        // reserva sin esperar a que el cliente consuma su saldo a favor.
        await CloseReservaIfOperatorRefundComplete(bc, ct);
    }

    /// <summary>
    /// ADR-033 (2026-06-18): cierra la RESERVA (<c>PendingOperatorRefund</c> -> <c>Cancelled</c>) cuando el
    /// OPERADOR reembolso el total esperado de la cancelacion, SIN esperar a que el cliente consuma su saldo
    /// a favor. Antes de ADR-033 la reserva solo se cerraba via <see cref="OnAllCreditConsumedAsync"/> (cuando
    /// el cliente gastaba todo su credito); si nunca lo gastaba, la reserva quedaba colgada para siempre en
    /// <c>PendingOperatorRefund</c>.
    ///
    /// <para><b>Que NO hace</b>: NO toca el <c>ClientCreditEntry</c> del cliente (su saldo a favor vive en su
    /// bolsillo, desacoplado del estado de la reserva por ADR-033) y NO cambia el Status del BC (sigue en
    /// <c>ClientCreditApplied</c> mientras el cliente tenga credito vivo). Lo unico que cierra es la reserva.</para>
    ///
    /// <para><b>Idempotencia / coexistencia con <see cref="OnAllCreditConsumedAsync"/></b>: las dos vias llevan
    /// al mismo destino (reserva <c>Cancelled</c>); la primera que ocurra gana. El guard por estado
    /// (<c>Reserva.Status == PendingOperatorRefund</c>) hace que si el cliente ya consumio el credito y la
    /// reserva ya esta <c>Cancelled</c>, esta via sea no-op.</para>
    ///
    /// <para><b>Patron HC1</b>: corre DENTRO de la tx envolvente del <c>OperatorRefundService.AllocateAsync</c>.
    /// Modifica en memoria y NO hace <c>SaveChanges</c> — el caller commitea TODO junto.</para>
    /// </summary>
    private async Task CloseReservaIfOperatorRefundComplete(BookingCancellation bc, CancellationToken ct)
    {
        // Guard por estado: solo cerramos si la reserva esta esperando el refund del operador.
        // Si ya esta Cancelled (porque el cliente consumio el credito antes, o porque otra
        // allocation ya cerro), esto es no-op -> idempotente.
        if (bc.Reserva is null || bc.Reserva.Status != EstadoReserva.PendingOperatorRefund)
            return;

        // Leemos las lineas del BC desde el ChangeTracker, NO desde una query a BD.
        //
        // Por que: el caller (OperatorRefundService.AllocateAsync) acaba de mutar
        // line.ReceivedRefundAmount y line.RefundStatus EN MEMORIA via
        // DistributeReceivedRefundToOperatorLines, y todavia NO commiteo (patron HC1).
        // Una query a BD (incluso con Include) podria devolver valores persistidos viejos.
        // El ChangeTracker es la unica fuente que refleja el estado in-memory mas reciente
        // — mismo enfoque que OnAllocationVoidedAsync usa para leer lo no persistido.
        var lines = _db.ChangeTracker
            .Entries<BookingCancellationLine>()
            .Select(entry => entry.Entity)
            .Where(line => line.BookingCancellationId == bc.Id)
            .ToList();

        // Solo cuentan las lineas que ESPERABAN reembolso del operador (RefundCap > 0).
        // Si no hay ninguna (no se esperaba refund por esta via), NO cerramos: el cierre
        // por refund total no aplica.
        var linesExpectingRefund = lines
            .Where(line => line.RefundCap > 0m)
            .ToList();

        if (linesExpectingRefund.Count == 0)
            return;

        // "Totalmente reembolsada" (criterio conservador): TODAS las lineas que esperaban
        // refund quedaron Settled. DistributeReceivedRefundToOperatorLines marca Settled
        // exactamente cuando ReceivedRefundAmount >= RefundCap, asi que mirar el RefundStatus
        // es equivalente a comparar montos, pero mas legible y menos sensible a redondeos.
        bool fullyRefunded = linesExpectingRefund
            .All(line => line.RefundStatus == BookingCancellationLineRefundStatus.Settled);

        if (!fullyRefunded)
        {
            _logger.LogDebug(
                "CloseReservaIfOperatorRefundComplete: BC {BcPublicId} aun no esta totalmente reembolsada " +
                "({SettledCount}/{Total} lineas Settled). Reserva sigue en PendingOperatorRefund.",
                bc.PublicId,
                linesExpectingRefund.Count(l => l.RefundStatus == BookingCancellationLineRefundStatus.Settled),
                linesExpectingRefund.Count);
            return;
        }

        // Cierre de la reserva. NO tocamos el Status del BC ni el ClientCreditEntry:
        // el cliente puede seguir teniendo saldo a favor vivo (ADR-033 lo desacopla del
        // estado de la reserva). Solo la reserva pasa a Cancelled.
        // Transición a Cancelled + rastro + descarte de la marca "confirmada con cambios" por el PUNTO ÚNICO de
        // transición. Disparado por un callback del sistema (sin actor humano): ByUserId/ByUserName null, la razón
        // lo documenta. La regla de limpieza para Cancelled apaga la marca + borra el detalle (idempotente).
        await ReservaStatusTransitioner.ApplyAsync(
            _db, bc.Reserva, EstadoReserva.Cancelled, "Forward",
            actorUserId: null, actorUserName: null,
            reason: "Cancelacion (ADR-002): operador reembolso el total esperado, reserva cerrada (sistema).", ct: ct);

        // Audit dedicado: via de cierre DISTINTA a BookingCancellationClosed (esa cierra
        // tambien el BC por consumo de credito; aca el BC sigue vivo). El contador puede
        // distinguir "se cerro porque el operador devolvio todo" de "se cerro porque el
        // cliente gasto su credito".
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationClosedByOperatorRefund,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                reservaPublicId = bc.Reserva.PublicId,
                receivedRefundAmount = bc.ReceivedRefundAmount,
                bcStatus = bc.Status.ToString(),
            }),
            userId: bc.ConfirmedByUserId ?? bc.DraftedByUserId,
            userName: bc.ConfirmedByUserName ?? bc.DraftedByUserName,
            ct: ct);

        _logger.LogInformation(
            "BC {BcPublicId}: reserva cerrada (PendingOperatorRefund -> Cancelled) por refund total del operador. " +
            "BC sigue en {BcStatus}. ReceivedRefundAmount={ReceivedRefundAmount}",
            bc.PublicId, bc.Status, bc.ReceivedRefundAmount);

        _logger.LogInformation(
            "metric:cancellation_reserva_closed_by_operator_refund | BcPublicId={BcPublicId} ReceivedRefundAmount={ReceivedRefundAmount}",
            bc.PublicId, bc.ReceivedRefundAmount);
    }

    // =========================================================================
    // (2026-06-26) Cierre del ciclo del reembolso del operador (timeout).
    //
    // Antes: BookingCancellationStatus.AbandonedByOperator NUNCA se asignaba (codigo muerto) y no habia job que
    // mirara OperatorRefundDueBy. Cuando el operador no devolvia el reembolso, la cancelacion quedaba colgada en
    // AwaitingOperatorRefund para siempre, sin alerta. Este metodo (lo invoca OperatorRefundTimeoutJob de noche)
    // cierra ese ciclo: las cancelaciones cuyo plazo vencio pasan a AbandonedByOperator y la reserva a Cancelled.
    // =========================================================================

    /// <inheritdoc />
    public async Task<int> ProcessExpiredOperatorRefundsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Candidatas: esperan el reembolso del operador (AwaitingOperatorRefund) y su plazo (OperatorRefundDueBy,
        // seteado en T0 = confirmacion + OperatorRefundTimeoutDays) ya vencio. Traemos solo los IDs: cada una se
        // procesa y persiste por separado (aislamiento de fila veneno), igual criterio que los jobs nocturnos.
        var expiredIds = await _db.BookingCancellations
            .AsNoTracking()
            .Where(bc => bc.Status == BookingCancellationStatus.AwaitingOperatorRefund
                      && bc.OperatorRefundDueBy != null
                      && bc.OperatorRefundDueBy < now)
            .Select(bc => bc.Id)
            .ToListAsync(ct);

        var abandoned = 0;
        foreach (var bcId in expiredIds)
        {
            try
            {
                if (await AbandonExpiredOperatorRefundAsync(bcId, now, ct))
                    abandoned++;
            }
            catch (OperationCanceledException)
            {
                throw; // shutdown del job: propagar, no es fila veneno
            }
            catch (Exception ex)
            {
                // Una cancelacion que falla (ej. DbUpdateConcurrencyException por xmin si otra transaccion la
                // toco en el medio, o una fila inconsistente) NO debe frenar al resto. Logueamos, limpiamos lo
                // que haya quedado a medias en el ChangeTracker y seguimos con la proxima. La proxima corrida la
                // reintenta (sigue en AwaitingOperatorRefund con el plazo vencido).
                _logger.LogError(ex,
                    "ProcessExpiredOperatorRefunds: fallo al abandonar la cancelacion {BcId}. Se saltea; las demas siguen.",
                    bcId);
                _db.ChangeTracker.Clear();
            }
        }

        if (abandoned > 0)
        {
            _logger.LogWarning(
                "ProcessExpiredOperatorRefunds: {Abandoned} cancelacion(es) marcadas AbandonedByOperator por plazo de reembolso vencido (de {Candidates} candidatas).",
                abandoned, expiredIds.Count);
        }

        // (2026-07-03) MISMA corrida: cerrar las anulaciones trabadas sin reembolso pendiente del operador
        // (receivable $0). Se corre DESPUES del abandono: una Awaiting vencida con $0 pasa a Abandoned arriba y aca
        // se cierra en el mismo barrido. Es independiente y defensivo (no aborta si el paso anterior no cerro nada).
        await CloseZeroReceivableCancellationsAsync(ct);

        return abandoned;
    }

    /// <summary>
    /// (2026-06-26) Transiciona UNA cancelacion vencida a <see cref="BookingCancellationStatus.AbandonedByOperator"/>
    /// y cierra su reserva (<c>PendingOperatorRefund</c> -> <c>Cancelled</c>), persistiendo en su propio
    /// SaveChanges (asi una falla no arrastra a las demas). Devuelve false si ya no aplica (idempotencia / carrera:
    /// el operador pago o ya fue procesada). El efecto sobre la reserva es el documentado para este estado
    /// (la reserva queda Cancelled); el saldo a favor del cliente NO se toca. <b>AbandonedByOperator es terminal
    /// por ahora</b>: registrar un reembolso tardio sobre esta BC NO esta implementado (follow-up futuro).
    /// </summary>
    private async Task<bool> AbandonExpiredOperatorRefundAsync(int bcId, DateTime now, CancellationToken ct)
    {
        var bc = await _db.BookingCancellations
            .Include(x => x.Reserva)
            .FirstOrDefaultAsync(x => x.Id == bcId, ct);

        // Idempotencia / carrera: si entre la lista de candidatas y este momento cambio de estado (el operador
        // reembolso, otra corrida la proceso), es no-op. El plazo tambien se re-chequea por si lo extendieron.
        if (bc is null
            || bc.Status != BookingCancellationStatus.AwaitingOperatorRefund
            || bc.OperatorRefundDueBy is null
            || bc.OperatorRefundDueBy >= now)
        {
            return false;
        }

        bc.Status = BookingCancellationStatus.AbandonedByOperator;
        bc.ClosedAt = now;

        // Cierre de la reserva (efecto documentado del estado): PendingOperatorRefund -> Cancelled. Guard por
        // estado para idempotencia: si la reserva ya quedo Cancelled por otra via (ej. el cliente consumio su
        // credito antes), NO la volvemos a mover ni re-logueamos. El saldo a favor del cliente queda intacto.
        if (bc.Reserva is not null && bc.Reserva.Status == EstadoReserva.PendingOperatorRefund)
        {
            // Transición a Cancelled + rastro + descarte de la marca por el PUNTO ÚNICO de transición (callback del
            // sistema, sin actor humano). El guard por estado de arriba ya garantizó que es un cambio real.
            await ReservaStatusTransitioner.ApplyAsync(
                _db, bc.Reserva, EstadoReserva.Cancelled, "Forward",
                actorUserId: null, actorUserName: null,
                reason: "Cancelacion (ADR-002): el operador no reembolso dentro del plazo (timeout), reserva cerrada (sistema).", ct: ct);
        }

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationAbandonedByOperator,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                reservaPublicId = bc.Reserva?.PublicId,
                operatorRefundDueBy = bc.OperatorRefundDueBy,
                receivedRefundAmount = bc.ReceivedRefundAmount,
                estimatedRefundAmount = bc.EstimatedRefundAmount,
            }),
            userId: bc.ConfirmedByUserId ?? bc.DraftedByUserId,
            userName: bc.ConfirmedByUserName ?? bc.DraftedByUserName,
            ct: ct);

        // Commit explicito de la transicion (BC + reserva + ReservaStatusChangeLog) en su propia transaccion.
        // NO es redundante con el SaveChanges interno de LogBusinessEventAsync: NO dependemos de que la auditoria
        // flushee nuestras mutaciones de dominio (es un detalle de implementacion del AuditService real, y en los
        // tests el IAuditService esta mockeado y NO guarda). Este SaveChanges garantiza que el abandono se persista
        // siempre, independiente de la implementacion de auditoria. En prod, si la auditoria ya flusheo, este queda
        // como no-op (no hay cambios pendientes).
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "BC {BcPublicId}: marcada AbandonedByOperator (plazo de reembolso {DueBy:o} vencido). Reserva cerrada (Cancelled).",
            bc.PublicId, bc.OperatorRefundDueBy);

        return true;
    }

    // =========================================================================
    // (2026-07-03) Cierre AUTOMATICO de anulaciones SIN reembolso pendiente del operador.
    //
    // Problema (caso real prod #F-2026-1025): cuando una anulacion queda firme (la NC total obtiene CAE) el BC
    // pasaba SIEMPRE a AwaitingOperatorRefund y la reserva a PendingOperatorRefund, sin chequear si el operador
    // realmente debe devolver algo. Si la agencia NUNCA le pago nada al operador por ese viaje (receivable $0), la
    // anulacion quedaba en un LIMBO permanente: no se puede registrar reembolso (no hay plata a recibir), no se puede
    // pagar al operador (la reserva ya esta anulada), y la reserva mostraba el chip "Esperando reembolso" para
    // siempre. La decision del dueño (2026-07-03): en ese caso cerrar DIRECTO (BC -> Closed, reserva -> Cancelled),
    // tanto en la transicion post-CAE como con un barrido nocturno que cierra las que ya quedaron trabadas.
    //
    // NO se toca el circuito fiscal: el cierre NO emite ni anula NC/ND. Solo cambia estados + auditoria.
    // =========================================================================

    /// <summary>
    /// (2026-07-03) ¿Esta anulacion, ya firme, quedaria en un limbo "esperando reembolso" eterno porque el operador
    /// NO tiene NADA que devolver? true cuando el receivable vivo es $0 en TODAS las monedas (tipico si la agencia
    /// nunca le pago nada al operador por ese viaje) Y no hay una multa del operador pendiente de gestion.
    ///
    /// <para><b>Guard de la multa</b>: devuelve false (NO cerrar) si todavia hay una multa del operador pendiente de
    /// confirmar (la confirmacion diferida que emite la Nota de Debito) o una ND a medio emitir esperando reintento.
    /// Aunque no haya plata que recuperar, cerrar antes sacaria esa multa del radar de las bandejas que filtran por
    /// estado. Primero se resuelve la multa (bandeja de NDs); recien despues el barrido cierra la anulacion.</para>
    ///
    /// <para>Usa la MISMA fuente unica del receivable que el extracto y la solapa "Reembolsos"
    /// (<see cref="SupplierCancellationCircuitReader.LiveReceivableForLine"/>), asi los tres numeros no pueden
    /// divergir. Requiere <c>bc.Reserva</c> cargada; las lineas se leen frescas (los caminos de transicion no las
    /// incluyen). READ-ONLY: no persiste nada.</para>
    /// </summary>
    private async Task<bool> ShouldAutoCloseWithoutOperatorRefundAsync(BookingCancellation bc, CancellationToken ct)
    {
        // 1) Guard de la multa: si sigue pendiente de gestion, NO cerramos (ver el resumen).
        if (await HasPendingOperatorPenaltyManagementAsync(bc, ct))
            return false;

        // 2) ¿Queda algo que el operador deba devolver? El receivable "me tiene que devolver" se DERIVA de las
        //    lineas (fuente unica del extracto). En los caminos de transicion las lineas no vienen cargadas: las
        //    leemos frescas por Id (AsNoTracking, solo lectura, no interfiere con el ChangeTracker del flujo).
        var lines = await _db.BookingCancellationLines
            .AsNoTracking()
            .Where(l => l.BookingCancellationId == bc.Id)
            .ToListAsync(ct);

        // Sin lineas: BC legacy vieja (pre-backfill) cuyo reembolso esperado se registro a nivel CABECERA
        // (<c>EstimatedRefundAmount</c>) y no en lineas -> el receivable-por-linea de arriba dio $0 por no haber
        // lineas, NO porque no haya nada que esperar. Miramos la cabecera:
        //   - Si NUNCA se espero reembolso (EstimatedRefundAmount <= 0) Y no entro nada (ReceivedRefundAmount == 0),
        //     esta anulacion legacy tampoco tenia circuito con el operador -> se cierra igual que las modernas $0.
        //   - Si la cabecera esperaba algo (> 0), hay un receivable real que la vista por-linea no ve: NO cerramos
        //     (se resuelve a mano) para no cerrar a ciegas una que quiza le tiene que devolver plata al cliente.
        //   - Guarda extra (condicion del security review 2026-07-04): la cabecera legacy puede MENTIR — si el
        //     ReceivedRefundAmount denormalizado quedo en 0 pero existe una imputacion de plata ACTIVA
        //     (OperatorRefundAllocation no anulada) apuntando a esta BC, hubo circuito real con el operador y
        //     cerrarla escondería ese ingreso. Preferimos el dato fuerte (las allocations) al cache de cabecera.
        if (lines.Count == 0)
        {
            if (bc.EstimatedRefundAmount > 0m || bc.ReceivedRefundAmount != 0m)
                return false;

            bool tieneImputacionActiva = await _db.OperatorRefundAllocations
                .AsNoTracking()
                .AnyAsync(a => a.BookingCancellationId == bc.Id && !a.IsVoided, ct);
            return !tieneImputacionActiva;
        }

        var serviceCountsAsDebt = await SupplierCancellationCircuitReader
            .LoadServiceDebtCountingAsync(_db, lines, ct);

        // Receivable vivo total con la MISMA formula del extracto / solapa "Reembolsos" (fuente unica, no divergen).
        decimal liveReceivable = lines.Sum(l =>
            SupplierCancellationCircuitReader.LiveReceivableForLine(l, bc, serviceCountsAsDebt, l.SupplierId, _logger));

        // Guarda anti-timing + alcance EXACTO del cierre (security review M1, 2026-07-03): solo cerramos cuando
        // NUNCA hubo circuito de reembolso con el operador — todas las lineas con RefundCap == 0 (no se le pago
        // nada reembolsable) y ReceivedRefundAmount == 0 (nunca entro un reembolso). Esto:
        //  (a) cubre el caso decidido por el dueño ("nunca le pagaste nada al operador" -> limbo);
        //  (b) evita el corner de timing (un cap > 0 con servicio aun no cancelado bloquea el cierre); y
        //  (c) EXCLUYE a proposito las BCs totalmente reembolsadas (cap == recibido > 0): cerrarlas dejaria sin
        //      camino de UI el void de esa allocation (OnAllocationVoidedAsync no reabre desde Closed) — si el
        //      reembolso registrado despues rebota, el receivable resucitado quedaria varado. Esas BCs se cierran
        //      por su via normal (aplicacion del credito al cliente -> OnAllCreditConsumedAsync).
        bool neverHadOperatorRefundCircuit = lines.All(l => l.RefundCap == 0m && l.ReceivedRefundAmount == 0m);

        return liveReceivable <= 0m && neverHadOperatorRefundCircuit;
    }

    /// <summary>
    /// (2026-07-03) ¿La multa del operador de esta cancelacion tiene una Nota de Debito a MEDIO EMITIR que impida
    /// cerrar la anulacion? true SOLO si hay una ND encolada o fallida (<c>DebitNoteStatus</c> Pending/Failed) que
    /// todavia se esta resolviendo (el caso #F-2026-1025 tipico). Un documento fiscal a medias NO puede quedar fuera
    /// del radar de las bandejas que filtran por estado, asi que mientras eso pase NO se cierra: se resuelve primero
    /// por la bandeja de NDs / el endpoint de reintento (que al reintentar cierra la anulacion en el momento).
    ///
    /// <para>DECISION DEL DUEÑO (2026-07-03): una multa SIN DECIDIR todavia (PenaltyStatus == Estimated, es decir
    /// "podria confirmarse pero nadie eligio aun") YA NO bloquea el cierre. Cuando la anulacion nunca tuvo circuito
    /// de reembolso con el operador (nada que devolver), se cierra IGUAL y la pregunta de la multa queda como una
    /// TAREA PENDIENTE que se responde DESPUES del cierre — la pata de la multa sigue 100% operable desde
    /// <c>Closed</c> (confirmar emite la ND, o cerrar sin multa), porque <c>Closed</c> esta en
    /// <see cref="PostCreditNoteStatuses"/>. Antes esta rama tambien bloqueaba con <c>EvaluateCanConfirmPenalty</c>
    /// (multa confirmable), lo que dejaba la reserva mostrando "esperando reembolso" para siempre aunque no hubiera
    /// nada que esperar. Ya no: solo bloquea la ND a medias.</para>
    ///
    /// <para>Requiere <c>bc.Status</c> ya en un estado post-NC (lo esta en los sitios que la llaman: transicion
    /// recien-firme y barrido sobre Awaiting/Abandoned).</para>
    /// </summary>
    /// <summary>
    /// FIX A (2026-07-04): calcula el receivable VIVO del operador ("me tiene que devolver") de una cancelacion,
    /// sumando el residuo de sus lineas con la MISMA formula unica del extracto y la solapa "Reembolsos"
    /// (<see cref="SupplierCancellationCircuitReader.LiveReceivableForLine"/>). Se usa para decidir si una
    /// cancelacion CERRADA se puede reabrir por un reembolso tardio: solo si el operador todavia debe plata de
    /// verdad. Requiere <c>bc.Reserva</c> cargada (la formula lee <c>bc.Reserva?.Status</c> en su guard R2).
    /// READ-ONLY (AsNoTracking): no toca el ChangeTracker del flujo que la llama.
    /// </summary>
    private async Task<decimal> ComputeLiveOperatorReceivableAsync(BookingCancellation bc, CancellationToken ct)
    {
        var lines = await _db.BookingCancellationLines
            .AsNoTracking()
            .Where(l => l.BookingCancellationId == bc.Id)
            .ToListAsync(ct);

        if (lines.Count == 0)
            return 0m;

        var serviceCountsAsDebt = await SupplierCancellationCircuitReader
            .LoadServiceDebtCountingAsync(_db, lines, ct);

        return lines.Sum(l =>
            SupplierCancellationCircuitReader.LiveReceivableForLine(l, bc, serviceCountsAsDebt, l.SupplierId, _logger));
    }

    private Task<bool> HasPendingOperatorPenaltyManagementAsync(BookingCancellation bc, CancellationToken ct)
    {
        // ND ya en juego pero incompleta (encolada o fallida esperando reintento): el caso #F-2026-1025 tipico.
        // No cerrar hasta que la ND se emita o se resuelva por la bandeja / el endpoint de reintento. Una multa
        // aun SIN DECIDIR (Estimated) NO se chequea aca a proposito (ver el resumen: decision del dueño).
        bool debitNoteHalfEmitted =
            bc.DebitNoteStatus == DebitNoteStatus.Pending || bc.DebitNoteStatus == DebitNoteStatus.Failed;
        return Task.FromResult(debitNoteHalfEmitted);
    }

    /// <summary>
    /// (2026-07-03) Aplica el cierre directo de una anulacion sin reembolso pendiente: BC -> <c>Closed</c> (con
    /// <c>ClosedAt</c>) y la reserva <c>PendingOperatorRefund</c> -> <c>Cancelled</c>, con su rastro de cambio de
    /// estado (motivo en criollo, se ve en el historial de la reserva) y el audit de negocio. NO persiste (el caller
    /// cierra con su SaveChanges) NI toca comprobantes fiscales. Precondicion: el cierre corresponde
    /// (<see cref="ShouldAutoCloseWithoutOperatorRefundAsync"/> dio true) y <c>bc.Reserva</c> esta cargada.
    ///
    /// <para><paramref name="origin"/> distingue en la auditoria si el cierre lo disparo la transicion post-CAE
    /// ("transicion"), el barrido nocturno ("barrido") o la resolucion de la multa del operador
    /// ("resolucion-multa").</para>
    /// </summary>
    private async Task ApplyAutoCloseWithoutOperatorRefundAsync(BookingCancellation bc, string origin, CancellationToken ct)
    {
        bc.Status = BookingCancellationStatus.Closed;
        bc.ClosedAt = DateTime.UtcNow;

        // Cierre de la reserva. Guard por estado para idempotencia: si ya quedo Cancelled por otra via (ej. el
        // cliente consumio su saldo a favor antes), NO la re-movemos ni re-logueamos.
        if (bc.Reserva is not null && bc.Reserva.Status == EstadoReserva.PendingOperatorRefund)
        {
            // Transición a Cancelled + rastro + descarte de la marca por el PUNTO ÚNICO de transición. El guard por
            // estado de arriba garantiza que venimos de PendingOperatorRefund (cambio real). Motivo user-facing:
            // criollo, sin jerga ni IDs (gate data-exposure). OJO: hoy el Reason NO se muestra en ninguna
            // pantalla (es solo rastro de auditoría en DB); si algún día se expone, revisar TODOS los reasons.
            await ReservaStatusTransitioner.ApplyAsync(
                _db, bc.Reserva, EstadoReserva.Cancelled, "Forward",
                actorUserId: null, actorUserName: null,
                reason: "Anulación cerrada: no había pagos al operador para recuperar.", ct: ct);
        }
        else if (bc.Reserva is not null && bc.Reserva.Status != EstadoReserva.Cancelled)
        {
            // Robustez (review backend, 2026-07-03): hoy este camino es inalcanzable (todo confirm deja la reserva
            // en PendingOperatorRefund antes de llegar aca), pero si un flujo futuro llegara con la reserva en un
            // estado activo, cerrariamos el BC dejando la reserva viva. Que sea RUIDOSO, no mudo.
            _logger.LogWarning(
                "ApplyAutoCloseWithoutOperatorRefund: BC {BcPublicId} cerrado pero la reserva {ReservaPublicId} " +
                "esta en {Status} (ni PendingOperatorRefund ni Cancelled). Revisar consistencia.",
                bc.PublicId, bc.Reserva.PublicId, bc.Reserva.Status);
        }

        // Audit STAGEADO (se commitea con el SaveChanges del caller): atomico con el cambio de estado.
        _auditService.StageBusinessEvent(
            action: AuditActions.BookingCancellationClosedNoOperatorRefundDue,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                reservaPublicId = bc.Reserva?.PublicId,
                zeroReceivable = true,
                origin,
            }),
            userId: bc.ConfirmedByUserId ?? bc.DraftedByUserId,
            userName: bc.ConfirmedByUserName ?? bc.DraftedByUserName);
    }

    /// <inheritdoc />
    public async Task<int> CloseZeroReceivableCancellationsAsync(CancellationToken ct)
    {
        // Candidatas: los 2 estados que dejan el chip "esperando reembolso" en la reserva. Traemos solo los IDs y
        // procesamos una por una (aislamiento de fila veneno + idempotencia por estado), igual criterio que el
        // abandono por timeout.
        var candidateIds = await _db.BookingCancellations
            .AsNoTracking()
            .Where(bc => bc.Status == BookingCancellationStatus.AwaitingOperatorRefund
                      || bc.Status == BookingCancellationStatus.AbandonedByOperator)
            .Select(bc => bc.Id)
            .ToListAsync(ct);

        if (candidateIds.Count == 0)
            return 0;

        var closed = 0;
        foreach (var bcId in candidateIds)
        {
            try
            {
                if (await CloseOneZeroReceivableCancellationAsync(bcId, ct))
                    closed++;
            }
            catch (OperationCanceledException)
            {
                throw; // shutdown del job: propagar, no es fila veneno
            }
            catch (Exception ex)
            {
                // Una cancelacion que falla (fila inconsistente, xmin, etc.) NO debe frenar al resto. Logueamos,
                // limpiamos lo que haya quedado a medias en el ChangeTracker y seguimos. La proxima corrida reintenta.
                _logger.LogError(ex,
                    "CloseZeroReceivableCancellations: fallo al cerrar la cancelacion {BcId}. Se saltea; las demas siguen.",
                    bcId);
                _db.ChangeTracker.Clear();
            }
        }

        if (closed > 0)
        {
            _logger.LogWarning(
                "CloseZeroReceivableCancellations: {Closed} anulacion(es) cerradas por no tener reembolso pendiente del operador (de {Candidates} candidatas).",
                closed, candidateIds.Count);
        }

        return closed;
    }

    /// <summary>
    /// (2026-07-03) Cierra UNA anulacion trabada sin reembolso pendiente, persistiendo en su propio SaveChanges (asi
    /// una falla no arrastra a las demas). Devuelve false si ya no aplica (idempotencia / carrera: cambio de estado,
    /// aparecio receivable, o hay multa pendiente). Comparte el guard y el cierre con la transicion post-CAE.
    /// </summary>
    private async Task<bool> CloseOneZeroReceivableCancellationAsync(int bcId, CancellationToken ct)
    {
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .FirstOrDefaultAsync(b => b.Id == bcId, ct);

        // Idempotencia / carrera: re-chequeo bajo la instancia trackeada. Solo cerramos desde los 2 estados limbo.
        if (bc is null
            || (bc.Status != BookingCancellationStatus.AwaitingOperatorRefund
                && bc.Status != BookingCancellationStatus.AbandonedByOperator))
        {
            return false;
        }

        if (!await ShouldAutoCloseWithoutOperatorRefundAsync(bc, ct))
            return false;

        await ApplyAutoCloseWithoutOperatorRefundAsync(bc, origin: "barrido", ct);

        // Commit explicito de la transicion (BC + reserva + ReservaStatusChangeLog + audit staged) en su propia
        // transaccion, independiente de la implementacion de la auditoria (en tests el IAuditService esta mockeado).
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "BC {BcPublicId}: cerrada por barrido (sin reembolso pendiente del operador). Reserva cerrada (Cancelled).",
            bc.PublicId);

        return true;
    }

    // =========================================================================
    // FIX B (2026-07-04): red de seguridad para el aviso de AFIP perdido en la NC TOTAL.
    //
    // La transicion AwaitingFiscalConfirmation -> AwaitingOperatorRefund depende 100% del callback de Hangfire.
    // Si la NC obtiene resultado final (CAE "A" o rechazo "R") pero el callback muere de forma permanente, la BC
    // queda trabada en AwaitingFiscalConfirmation, invisible a todos los barridos. Este metodo re-invoca el MISMO
    // callback del bridge (idempotente) cuando detecta que AFIP ya respondio. Analogo total del
    // PartialCreditNoteBridgeReconciliationJob.
    // =========================================================================

    /// <inheritdoc />
    public async Task<int> ReconcileStuckFiscalConfirmationsAsync(CancellationToken ct)
    {
        var settings = await _settings.GetEntityAsync(ct);

        // Modulo de anulacion apagado: no hay BC nuevas entrando a AwaitingFiscalConfirmation. No-op.
        if (!settings.EnableNewCancellationFlow)
        {
            _logger.LogDebug("ReconcileStuckFiscalConfirmations: EnableNewCancellationFlow=false, skip.");
            return 0;
        }

        // Umbral de antiguedad: reusamos el mismo setting que el job parcial (default 30 min). El ancla es
        // ConfirmedWithClientAt (cuando la BC entro a AwaitingFiscalConfirmation). Solo consideramos las que
        // llevan MAS que el umbral trabadas ahi — asi no corremos contra un callback recien disparado que esta
        // por llegar. Aun asi el gate REAL es "AFIP ya dio resultado final" (mas abajo): sin eso no tocamos nada.
        var stalenessMinutes = Math.Max(settings.BridgeReconciliationStalenessMinutes, 1);
        var threshold = DateTime.UtcNow.AddMinutes(-stalenessMinutes);

        // Solo IDs (aislamiento de fila veneno + no trackeamos un set grande). Cota de 200 por pasada.
        var candidateIds = await _db.BookingCancellations
            .AsNoTracking()
            .Where(bc => bc.Status == BookingCancellationStatus.AwaitingFiscalConfirmation
                      && bc.ConfirmedWithClientAt != null
                      && bc.ConfirmedWithClientAt < threshold)
            .OrderBy(bc => bc.ConfirmedWithClientAt)
            .Select(bc => bc.Id)
            .Take(200)
            .ToListAsync(ct);

        if (candidateIds.Count == 0)
        {
            _logger.LogDebug(
                "ReconcileStuckFiscalConfirmations: ningun candidato (threshold={Threshold:o}, stalenessMinutes={Stale}).",
                threshold, stalenessMinutes);
            return 0;
        }

        var reconciled = 0;
        foreach (var bcId in candidateIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await ReconcileOneStuckFiscalConfirmationAsync(bcId, ct))
                    reconciled++;
            }
            catch (OperationCanceledException)
            {
                throw; // shutdown del job: propagar, no es fila veneno
            }
            catch (Exception ex)
            {
                // Una BC que falla (callback que tira, fila inconsistente) NO debe frenar al resto. Logueamos con
                // metric: para que el alerting lo capture (mismo espiritu que los metric: del flujo parcial) y
                // seguimos. La proxima corrida reintenta; el estado no cambio.
                _logger.LogError(ex,
                    "ReconcileStuckFiscalConfirmations: fallo al reconciliar la cancelacion {BcId}. Se saltea; las demas siguen.",
                    bcId);
                _logger.LogError(
                    "metric:total_nc_bridge_reconciliation_failed | BcId={BcId} errorType={ErrorType}",
                    bcId, ex.GetType().Name);
                _db.ChangeTracker.Clear();
            }
        }

        if (reconciled > 0)
        {
            _logger.LogWarning(
                "ReconcileStuckFiscalConfirmations: {Reconciled} anulacion(es) destrabadas (AFIP ya habia respondido pero el aviso se perdio) de {Candidates} candidatas.",
                reconciled, candidateIds.Count);
        }

        return reconciled;
    }

    /// <summary>
    /// FIX B (2026-07-04): reconcilia UNA cancelacion trabada en <c>AwaitingFiscalConfirmation</c>. Localiza, para
    /// cada factura cuya NC todavia no fue aplicada al BC, si AFIP ya dio resultado final; si es asi, RE-INVOCA el
    /// MISMO callback del bridge (<see cref="OnArcaSucceededAsync"/>/<see cref="OnArcaFailedAsync"/>), que hace la
    /// transicion identica a la que habria hecho el aviso perdido (idempotente). Devuelve true si la BC salio de
    /// <c>AwaitingFiscalConfirmation</c> tras la reconciliacion.
    /// </summary>
    private async Task<bool> ReconcileOneStuckFiscalConfirmationAsync(int bcId, CancellationToken ct)
    {
        var bc = await _db.BookingCancellations
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bcId, ct);

        // Carrera / idempotencia: si entre la query de candidatos y ahora ya se destrabo, nada que hacer.
        if (bc is null || bc.Status != BookingCancellationStatus.AwaitingFiscalConfirmation)
            return false;

        // Facturas de origen cuya NC todavia no se aplico al BC:
        //   - Multi-factura (ADR-042): las hijas Pending (su NC aun no se conto en la completitud del BC).
        //   - Legacy (sin hijas): el puntero singular OriginatingInvoiceId.
        var pendingChildOriginatingInvoiceIds = await _db.BookingCancellationCreditNotes
            .AsNoTracking()
            .Where(c => c.BookingCancellationId == bcId
                     && c.Status == BookingCancellationCreditNoteStatus.Pending)
            .Select(c => c.OriginatingInvoiceId)
            .ToListAsync(ct);

        bool hasChildren = await _db.BookingCancellationCreditNotes
            .AsNoTracking()
            .AnyAsync(c => c.BookingCancellationId == bcId, ct);

        var originatingInvoiceIds = hasChildren
            ? pendingChildOriginatingInvoiceIds
            : new List<int> { bc.OriginatingInvoiceId };

        // NC types (NC A/B/C): 3, 8, 13. Solo estas cuentan como Nota de Credito de anulacion.
        var ncTipos = new[] { 3, 8, 13 };

        foreach (var originatingInvoiceId in originatingInvoiceIds)
        {
            ct.ThrowIfCancellationRequested();

            // ¿AFIP ya dio resultado FINAL para la NC de esta factura? Buscamos la NC (Invoice cuya
            // OriginalInvoiceId apunta a la factura de origen) con resultado terminal. Preferimos la aprobada.
            var nc = await _db.Invoices
                .AsNoTracking()
                .Where(i => i.OriginalInvoiceId == originatingInvoiceId
                         && ncTipos.Contains(i.TipoComprobante)
                         && (i.Resultado == "A" || i.Resultado == "R"))
                .OrderByDescending(i => i.Resultado == "A") // "A" primero: si salio aprobada, esa manda
                .ThenByDescending(i => i.Id)
                .FirstOrDefaultAsync(ct);

            if (nc is null)
                continue; // AFIP todavia no dio resultado final para esta NC: no es nuestro caso, no tocamos.

            if (nc.Resultado == "A" && !string.IsNullOrWhiteSpace(nc.CAE))
            {
                // Aprobada: re-aplicar EXACTAMENTE el callback de exito que se perdio. Idempotente.
                await OnArcaSucceededAsync(originatingInvoiceId, nc.Id, ct);
            }
            else if (nc.Resultado == "R")
            {
                // Rechazada: re-aplicar el callback de fallo (deja la BC en ArcaRejected para revision/retry).
                await OnArcaFailedAsync(originatingInvoiceId, nc.Observaciones, ct);
            }
        }

        // ¿Se destrabo? Releemos el estado final (el callback pudo transicionar a AwaitingOperatorRefund, cerrarla
        // directo si no habia plata al operador, o dejarla en ArcaRejected).
        var newStatus = await _db.BookingCancellations
            .AsNoTracking()
            .Where(b => b.Id == bcId)
            .Select(b => b.Status)
            .FirstOrDefaultAsync(ct);

        bool movedOn = newStatus != BookingCancellationStatus.AwaitingFiscalConfirmation;
        if (movedOn)
        {
            _logger.LogInformation(
                "ReconcileStuckFiscalConfirmations: BC {BcId} destrabada por reconciliacion (AwaitingFiscalConfirmation -> {NewStatus}).",
                bcId, newStatus);
        }

        return movedOn;
    }

    /// <summary>
    /// (2026-07-03) CIERRE INMEDIATO al resolver la pata de la multa del operador. Usado en los casos donde el
    /// cierre NO pudo dispararse antes porque habia una Nota de Debito a medio emitir bloqueando el guard
    /// (<see cref="HasPendingOperatorPenaltyManagementAsync"/>): apenas esa ND se RESUELVE (queda emitida, o se
    /// cierra sin multa) hay que re-evaluar el cierre EN EL MOMENTO, sino la reserva se queda mostrando "esperando
    /// reembolso" hasta el barrido nocturno. Este helper corre esa re-evaluacion desde los puntos de resolucion de
    /// la multa. (Nota 2026-07-04: una multa aun SIN DECIDIR ya NO bloquea el cierre — ese caso se cierra directo en
    /// la transicion post-CAE; aca solo importa la ND a medio emitir.)
    ///
    /// <para>Idempotente y defensivo: si el cierre NO corresponde (hay reembolso vivo del operador, todavia queda algo
    /// de la multa por resolver, o la BC ya cerro), es un no-op. NO cambia el predicado de cierre — solo lo dispara
    /// antes. El barrido nocturno queda igual como red de seguridad. Persiste en su propio <c>SaveChanges</c>, DESPUES
    /// de que el caller ya commiteo la resolucion de la multa (asi la resolucion nunca queda a medias si el cierre
    /// fallara). Precondicion: <c>bc.Reserva</c> cargada.</para>
    /// </summary>
    private async Task TryAutoCloseAfterOperatorPenaltyResolvedAsync(BookingCancellation bc, CancellationToken ct)
    {
        // Idempotencia dura: si ya cerro (o quedo en otro estado terminal), no hay nada que cerrar.
        if (bc.Status == BookingCancellationStatus.Closed)
            return;

        // El guard de la multa (dentro de ShouldAutoClose) ya paso a false al resolverse la pata; si ademas no hay
        // reembolso pendiente del operador (receivable $0 y nunca hubo circuito), cerramos directo.
        if (!await ShouldAutoCloseWithoutOperatorRefundAsync(bc, ct))
            return;

        try
        {
            await ApplyAutoCloseWithoutOperatorRefundAsync(bc, origin: "resolucion-multa", ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Carrera benigna (review 2026-07-04): el barrido nocturno (u otro camino) cerro la MISMA BC entre
            // nuestro re-chequeo y este SaveChanges. El estado final deseado (Closed) ya lo logro el otro escritor,
            // y la resolucion de la multa del caller YA esta commiteada — propagar el choque de xmin solo le
            // mostraria un error al usuario por algo que quedo bien. Lo tragamos y dejamos rastro en el log.
            _logger.LogInformation(
                "BC {BcPublicId}: el cierre post-resolucion de multa choco por concurrencia (otro proceso ya la cerro). No-op.",
                bc.PublicId);
            return;
        }

        _logger.LogInformation(
            "BC {BcPublicId}: cerrada al resolver la multa del operador (sin reembolso pendiente). Reserva cerrada (Cancelled).",
            bc.PublicId);
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
        // Transición a Cancelled + rastro + descarte de la marca por el PUNTO ÚNICO de transición. Lo dispara el
        // consumo total del crédito (callback del sistema), sin actor humano -> ByUserId null, documentado en la razón.
        await ReservaStatusTransitioner.ApplyAsync(
            _db, bc.Reserva, EstadoReserva.Cancelled, "Forward",
            actorUserId: null, actorUserName: null,
            reason: "Cancelacion (ADR-002): credito del cliente consumido en su totalidad, cancelacion cerrada (sistema).", ct: ct);

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
    public async Task<BookingCancellationDto?> GetByReservaAsync(Guid reservaPublicId, CancellationToken ct)
    {
        // Una reserva puede tener varios BC a lo largo del tiempo (borradores abortados, etc.).
        // Para el panel de "confirmar multa del operador" interesa la cancelacion vigente: la mas
        // reciente que NO fue abortada. INV-081 garantiza una sola cancelacion ACTIVA por reserva,
        // asi que en la practica esto devuelve la unica cancelacion real. El filtro por
        // Reserva.PublicId lo traduce EF a un join (no necesita Include; MapToDtoAsync re-consulta
        // con sus propios Includes por bc.Id).
        var bc = await _db.BookingCancellations
            .AsNoTracking()
            .Where(b => b.Reserva.PublicId == reservaPublicId
                     && b.Status != BookingCancellationStatus.Aborted)
            .OrderByDescending(b => b.DraftedAt)
            .FirstOrDefaultAsync(ct);
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

            // Regla UNICA de reconciliacion (Pending -> Issued si "A"+CAE / Pending -> Failed si "R").
            // Compartida con AfipService.ProcessInvoiceJob (bug 2026-07-13): el mismo criterio corre ni
            // bien llega el CAE async, no solo al abrir esta bandeja. Ver CancellationDebitNoteReconciliation.
            if (CancellationDebitNoteReconciliation.TryApplyResolvedDebitNote(bc, nd))
            {
                changed = true;
                // DECISION (2026-07-03, cierre inmediato al resolver la multa): aca la ND puede acabar de pasar a
                // Issued, o sea la pata de la multa quedo resuelta -> tecnicamente ya se podria auto-cerrar la
                // anulacion sin reembolso pendiente. NO lo hacemos en este punto A PROPOSITO: esto es un GET (bandeja
                // de NDs) y cerrar una reserva es una transicion de negocio que no debe dispararse como efecto lateral
                // de una LECTURA (dos usuarios abriendo la bandeja competirian por el cierre, y sorprende a quien solo
                // mira). El cierre lo hacen (a) las acciones EXPLICITAS de resolucion (cerrar sin multa / reintentar la
                // ND -> TryAutoCloseAfterOperatorPenaltyResolvedAsync) y (b) el barrido nocturno como red de seguridad.
                // Peor caso: la reserva queda un ciclo de barrido en "esperando reembolso" tras llegar el CAE async.
            }
            // Resultado == "PENDING" o null: sigue en vuelo, TryApplyResolvedDebitNote devuelve false y lo dejamos.
        }

        // (2-bis-0) ADR-044 (fix choque con ADR-014, 2026-07-14): AUTO-REPARACION de un estado corrupto real de
        //     produccion (reserva F-2026-1043 / BC 13). Corre ANTES del re-vinculador de mas abajo, a proposito:
        //     si un BC quedo re-enganchado por error a una ND que YA fue anulada por su propia Nota de Credito
        //     (ver el porque en el bloque (2-bis)), hay que desenganchar ESE enganche fantasma primero. Si no,
        //     el BC seguiria mostrando "multa cobrada" con una ND muerta y el boton "Deshacer" rebotaria de
        //     nuevo con "ya se deshizo" (el limbo exacto que disparo este fix).
        //
        //     SaveChanges INMEDIATO (no esperamos al SaveChanges de mas abajo, a proposito): el bloque (2-bis)
        //     que sigue vuelve a CONSULTAR la base para armar orphanCandidates, y una consulta nueva NO ve
        //     mutaciones todavia sin persistir del mismo DbContext (ni en Postgres ni en el proveedor InMemory
        //     de los tests). Sin este commit intermedio, un BC recien reparado quedaria invisible en ESTA misma
        //     pasada de la bandeja (habria que abrirla una segunda vez para verlo abierto).
        var orphanRepair = await AutoRepairOrphanRelinksToAnnulledDebitNotesAsync(ct);
        if (orphanRepair is not null)
        {
            changed = true;
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException conflict)
            {
                // Mismo criterio de carrera que el resto de este metodo (ver el catch de mas abajo): otro
                // proceso (otro pedido concurrente a esta bandeja, o el job del CAE) ya toco esta fila. No es
                // un error: otro proceso ya reparo el re-enganche fantasma. Aca la reparacion de ESTA lectura
                // NO se aplico, asi que hay que descartar TODO su rastro pendiente en el ChangeTracker.
                //
                // Por que un cleanup DIRIGIDO y no un ChangeTracker.Clear() global: mas arriba (bloque (1)) este
                // metodo pudo dejar mutaciones legitimas Modified de BCs que SI se tienen que persistir en el
                // SaveChanges de mas abajo; un Clear las tiraria. Solo descartamos lo que produjo la reparacion.
                //
                // REGLA (seguridad, N3): la auditoria "reparado" JAMAS puede sobrevivir desacoplada de la
                // mutacion que la origina. Si el BC no se desvinculo (se recarga al valor persistido), su
                // AuditLog "reparado" y sus resets de linea tampoco deben quedar para flushearse despues, o
                // quedaria un rastro de una reparacion que esta lectura no hizo (auditoria fantasma / duplicada).

                // (a) Los BC que tocamos: recargar al valor persistido (revierte la desvinculacion pendiente y
                //     deja la fila Unchanged con el estado real de la base).
                foreach (var bc in orphanRepair.BookingCancellations)
                {
                    await _db.Entry(bc).ReloadAsync(ct);
                }

                // (b) Los resets de linea: recargar tambien (revierte el DebitNoteStatus/error que pusimos).
                foreach (var line in orphanRepair.ResetLines)
                {
                    await _db.Entry(line).ReloadAsync(ct);
                }

                // (c) La auditoria staged de esta reparacion: DETACH (todavia esta Added, sin fila en la base;
                //     un Reload no aplica). Asi no se inserta en el SaveChanges de mas abajo.
                foreach (var audit in orphanRepair.StagedAudits)
                {
                    _db.Entry(audit).State = EntityState.Detached;
                }

                _logger.LogInformation(
                    "Bandeja de NDs: otro proceso ya reparo el re-enganche fantasma (ADR-044) antes que esta " +
                    "lectura. Se descarto el rastro pendiente de esta reparacion (BC recargados, lineas " +
                    "recargadas, auditoria staged desvinculada) y se continua sin romper.");
            }
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
        //
        //     OJO (fix 2026-07-14): este mismo perfil "Confirmed + DebitNoteInvoiceId=null" es AMBIGUO desde que
        //     existe ADR-044 "Deshacer una multa ya emitida" (2026-07-14, posterior a esta pieza): un BC recien
        //     DESHECHO A PROPOSITO por DebitNoteAnnulmentReconciliation.ApplyAsync cae EXACTO en el mismo perfil
        //     (queda Confirmed, sin ND, y sigue existiendo una ND para la factura original — la que se acaba de
        //     anular). La consulta de mas abajo desambigua consultando la tabla hija
        //     BookingCancellationDebitNoteAnnulments: si la ND candidata tiene una fila Pending o Succeeded, NO
        //     es huerfana valida (es la ND que se esta/ya se deshizo) y se descarta.
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
                            i.ReservaId == bc.ReservaId &&
                            // ADR-044: descartar cualquier ND que este siendo (o ya haya sido) anulada por una
                            // NC-anula-ND. Re-vincularla seria re-enganchar una ND MUERTA (ver el comentario
                            // de arriba).
                            !_db.BookingCancellationDebitNoteAnnulments.Any(a =>
                                a.AnnulledDebitNoteInvoiceId == i.Id &&
                                a.Status != DebitNoteAnnulmentStatus.Failed))
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
        {
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException conflict)
            {
                // CARRERA job-vs-bandeja (bug 2026-07-13): desde el fix de reconciliacion, el job del CAE
                // (AfipService.ProcessInvoiceJob) es un SEGUNDO escritor del mismo BookingCancellation ni bien
                // ARCA resuelve la ND. Como el BC usa xmin de Postgres como token de concurrencia
                // (AppDbContext.cs), si el job le gana la carrera a esta LECTURA, el xmin que trajimos ya
                // quedo viejo y este SaveChanges tira DbUpdateConcurrencyException. NO es un error: el job
                // aplico EXACTAMENTE la misma transicion (Pending -> Issued/Failed, misma regla compartida
                // CancellationDebitNoteReconciliation). Antes esta excepcion sin capturar rompia la bandeja
                // con un 500. La tratamos como "otro ya reconcilio": descartamos nuestro write recargando las
                // filas en conflicto con lo persistido, para que la proyeccion de mas abajo muestre la verdad
                // de la BD. NO reintentamos el SaveChanges (el job ya escribio).
                foreach (var conflictedEntry in conflict.Entries)
                {
                    await conflictedEntry.ReloadAsync(ct);
                }
                _logger.LogInformation(
                    "Bandeja de NDs: el job del CAE reconcilio {Count} anulacion(es) antes que esta lectura. " +
                    "Se recargaron los valores persistidos y se continua sin romper.",
                    conflict.Entries.Count);
            }
        }

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

    /// <summary>
    /// ADR-044 (fix choque con ADR-014, 2026-07-14): AUTO-REPARACION del estado corrupto real de produccion
    /// (reserva F-2026-1043 / BC 13). Busca BCs cuya ND VINCULADA (<c>DebitNoteInvoiceId</c>) en realidad ya fue
    /// anulada por una NC con CAE aprobado (fila <c>Succeeded</c> en <c>BookingCancellationDebitNoteAnnulments</c>)
    /// — es decir, el re-vinculador de ND huerfana de ADR-014 la re-engancho por error DESPUES de que
    /// <see cref="TravelApi.Infrastructure.Services.DebitNoteAnnulmentReconciliation"/> ya la habia desvinculado a
    /// proposito. Deshace ese re-enganche fantasma con el MISMO trio de campos que usa la reconciliacion original
    /// (<c>DebitNoteInvoiceId=null</c>, <c>DebitNoteStatus=NotApplicable</c>, sin mensaje de error) y resetea las
    /// lineas que alimentaron esa ND, reusando el helper del reconciliador.
    ///
    /// <para><b>NO vuelve a acuñar saldo a favor</b>: el credito por la porcion cobrada de la multa ya se acuño
    /// UNA vez, en la reconciliacion original que proceso la NC-anula-ND. Acuñar de nuevo aca duplicaria ese
    /// credito (y ademas el indice unico parcial de <c>ClientCreditEntry</c> lo rechazaria si se intentara).</para>
    ///
    /// <para>Solo mira <c>Status == Succeeded</c> (no <c>Pending</c>): mientras la NC-anula-ND sigue en vuelo, la
    /// ND original TODAVIA esta viva (Issued) — no hay nada que reparar hasta que consiga su propio CAE.</para>
    /// </summary>
    /// <summary>
    /// Entidades que la auto-reparacion dejo pendientes en el <c>ChangeTracker</c> (sin persistir todavia). El
    /// caller las necesita para poder DESHACER LIMPIO su propio trabajo si el <c>SaveChanges</c> inmediato choca
    /// con otra transaccion: hay que descartar la auditoria staged Y los resets de linea junto con el BC, para
    /// que ningun huerfano de una reparacion FALLIDA sobreviva y se flushee mas tarde desacoplado.
    /// </summary>
    private sealed record OrphanRelinkRepairTouched(
        List<BookingCancellation> BookingCancellations,
        List<BookingCancellationLine> ResetLines,
        List<AuditLog> StagedAudits);

    /// <returns>
    /// Las entidades tocadas si reparo al menos un BC (para que el caller marque <c>changed</c>, persista y —si
    /// el commit choca— sepa EXACTAMENTE que descartar); <c>null</c> si no habia nada que reparar.
    /// </returns>
    private async Task<OrphanRelinkRepairTouched?> AutoRepairOrphanRelinksToAnnulledDebitNotesAsync(CancellationToken ct)
    {
        var brokenLinks = await _db.BookingCancellations
            .Where(b => b.DebitNoteInvoiceId != null &&
                        _db.BookingCancellationDebitNoteAnnulments.Any(a =>
                            a.AnnulledDebitNoteInvoiceId == b.DebitNoteInvoiceId!.Value &&
                            a.Status == DebitNoteAnnulmentStatus.Succeeded))
            .ToListAsync(ct);

        if (brokenLinks.Count == 0)
        {
            return null;
        }

        foreach (var bc in brokenLinks)
        {
            var wronglyRelinkedDebitNoteInvoiceId = bc.DebitNoteInvoiceId!.Value;

            bc.DebitNoteInvoiceId = null;
            bc.DebitNoteStatus = DebitNoteStatus.NotApplicable;
            bc.DebitNoteArcaErrorMessage = null;

            // Mismo reseteo de lineas que la reconciliacion original (B2): solo las que alimentaron ESTA ND,
            // jamas las que estan en ManualReview (multa pendiente de OTRO operador).
            await DebitNoteAnnulmentReconciliation.ResetLinesFedByDebitNoteAsync(
                _db, bc.Id, wronglyRelinkedDebitNoteInvoiceId, ct);

            _logger.LogWarning(
                "metric:debit_note_orphan_relink_repaired | BcPublicId={BcPublicId} " +
                "WronglyRelinkedDebitNoteInvoiceId={NdId} | El re-vinculador de ND huerfana (ADR-014) habia " +
                "re-enganchado una ND que YA estaba anulada por su propia NC. Se desvinculo de nuevo; el paso " +
                "vuelve a quedar abierto. NO se acuño saldo a favor otra vez (ya se acuño en la reconciliacion " +
                "original).",
                bc.PublicId, wronglyRelinkedDebitNoteInvoiceId);

            StageOrphanRelinkRepairAudit(bc, wronglyRelinkedDebitNoteInvoiceId);
        }

        // Recolectamos lo que quedo pendiente en el ChangeTracker POR esta reparacion, para que el caller pueda
        // descartarlo en bloque si el commit choca. Es seguro leer el estado del tracker aca: esta reparacion
        // corre ANTES del re-vinculador (2-bis) y el bloque (1) previo solo toca escalares del BC —nunca lineas
        // ni auditoria—, asi que en este punto las UNICAS lineas Modified y los UNICOS AuditLog Added con esta
        // accion salieron de este metodo.
        var resetLines = _db.ChangeTracker.Entries<BookingCancellationLine>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();

        var stagedAudits = _db.ChangeTracker.Entries<AuditLog>()
            .Where(e => e.State == EntityState.Added &&
                        e.Entity.Action == AuditActions.OperatorPenaltyDebitNoteOrphanLinkRepaired)
            .Select(e => e.Entity)
            .ToList();

        return new OrphanRelinkRepairTouched(brokenLinks, resetLines, stagedAudits);
    }

    /// <summary>Auditoria dedicada de la auto-reparacion de arriba (accion propia para que el contador pueda filtrarla).</summary>
    private void StageOrphanRelinkRepairAudit(BookingCancellation bc, int wronglyRelinkedDebitNoteInvoiceId)
    {
        var details = JsonSerializer.Serialize(new
        {
            bc.PublicId,
            action = "operator-penalty-debit-note-orphan-relink-repaired",
            wronglyRelinkedDebitNoteInvoiceId,
            note = "El re-vinculador de ND huerfana (ADR-014) re-engancho por error una ND ya anulada por su " +
                   "propia NC. Se desvinculo de nuevo automaticamente al abrir la bandeja de NDs.",
        });

        _auditService.StageBusinessEvent(
            action: AuditActions.OperatorPenaltyDebitNoteOrphanLinkRepaired,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: details,
            userId: "system:orphan-relink-guard",
            userName: "Sistema (bandeja de Notas de Débito)");
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
            // (A5, 2026-07-08) PublicId de la reserva para que la bandeja linkee a la ficha (donde vive el paso de multa).
            ReservaPublicId = b.Reserva?.PublicId,
            ReservaNumero = b.Reserva?.NumeroReserva ?? string.Empty,
            DebitNoteStatus = overrideStatus ?? b.DebitNoteStatus.ToString(),
            PenaltyAmount = b.PenaltyAmountAtEvent,
            // Proyectamos la moneda a ISO ("ARS"/"USD") para el front (data-exposure 2026-07-08). Hoy el confirm
            // ya graba ISO, pero una fila legacy podria traer el codigo ARCA ("DOL") o algo no reconocido: la
            // conversion normaliza ARCA->ISO y devuelve null si no se reconoce (asi el front muestra solo el monto
            // sin romper Intl.NumberFormat con un codigo invalido).
            PenaltyCurrency = ProjectPenaltyCurrencyToIsoOrNull(b.PenaltyCurrencyAtEvent),
            DebitNoteCbteTipo = b.DebitNoteCbteTipoAtEvent,
            // FUGA 1 data-exposure (2026-07-03): el motivo de rechazo de la ND (DebitNoteArcaErrorMessage) puede
            // traer XML/tecnico de ARCA. Se SANEA antes de exponerlo en la bandeja (el crudo queda en la entidad).
            ArcaErrorMessage = SanitizeArcaErrorForUser(b.DebitNoteArcaErrorMessage),
            ConfirmedAt = b.ConfirmedWithClientAt,
        };

    /// <summary>
    /// Data-exposure (2026-07-08): proyecta la moneda de la multa a ISO ("ARS"/"USD") para el front. Acepta
    /// tanto ISO (lo que graba el confirm hoy) como un codigo ARCA legacy ("DOL"/"PES"). Devuelve <c>null</c> si
    /// esta vacia o no se reconoce, para que el front muestre solo el monto (sin romper Intl.NumberFormat con un
    /// codigo de moneda invalido).
    /// </summary>
    private static string? ProjectPenaltyCurrencyToIsoOrNull(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        if (Monedas.EsSoportada(stored))        // ya es ISO soportada ("ARS"/"USD")
            return stored.Trim().ToUpperInvariant();
        return ArcaCurrencyMapper.ToIso(stored); // ARCA ("DOL"/"PES") -> ISO, o null si no reconoce
    }

    /// <summary>
    /// ADR-009/ADR-025 (read-model, 2026-06-13): estados del BC que representan "NC parcial esperando
    /// revision/emision manual". <c>ManualReviewPending</c> (9) es el que se persiste bajo el flujo
    /// normal (SubmitForReviewAsync); <c>RequiresManualReview</c> (8) es un marker transitorio del enum
    /// que no se persiste, pero lo incluimos por completitud para que la bandeja no dependa de ese
    /// detalle de implementacion.
    /// </summary>
    private static readonly BookingCancellationStatus[] PendingCreditNoteReviewStates =
    {
        BookingCancellationStatus.RequiresManualReview,
        BookingCancellationStatus.ManualReviewPending,
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingCreditNoteReviewDto>> GetCancellationsPendingCreditNoteReviewAsync(
        CancellationToken ct)
    {
        // Solo lectura: NO reconciliamos ni mutamos nada (a diferencia de la bandeja de notas de debito).
        // Esta bandeja solo lista lo que espera revision/emision manual; NO dispara ninguna maquinaria fiscal.
        //
        // Materializamos las entidades (con Reserva + Payer + Lines) y proyectamos en memoria, mismo patron que
        // GetCancellationsWithMissingDebitNoteAsync. Lo hacemos asi porque el monto sale de dos fuentes segun el
        // caso (el owned VO OPCIONAL FiscalLiquidation en el flujo legacy, o la suma de las lineas Partial en un
        // pendiente T5) y el Status se traduce a una etiqueta de negocio: resolverlo en memoria evita depender
        // de como cada provider traduce esos casos. El volumen de esta bandeja es chico.
        //
        // FRENTE D (ADR-044 T5): ademas de los estados de revision manual (RequiresManualReview /
        // ManualReviewPending), la bandeja incluye los BC en Drafted que sean PURAMENTE parciales — al menos una
        // linea Scope=Partial y NINGUNA Scope=Full. Son las cancelaciones parciales de UN servicio cuya Nota de
        // Credito quedo pendiente de emision (el BC permanece Drafted porque no se inventa el snapshot fiscal;
        // ver CancelServiceAsync).
        //
        // FIX N1 (security reviewer): se EXCLUYE explicitamente un Drafted que ya tiene alguna linea Scope=Full.
        // Ese es el caso del anular-total que ABSORBIO un draft parcial (FRENTE E): pasa a ser una anulacion
        // TOTAL en curso con su propio circuito, y mostrarlo como "Pendiente de emisión" con el monto de las
        // Partial seria engañoso. Un Drafted de anulacion total normal (solo Full) tampoco entra por lo mismo.
        var candidates = await _db.BookingCancellations
            .AsNoTracking()
            .Include(b => b.Reserva)
                .ThenInclude(r => r.Payer)
            .Include(b => b.Lines)
            .Where(b => PendingCreditNoteReviewStates.Contains(b.Status)
                     || (b.Status == BookingCancellationStatus.Drafted
                         && b.Lines.Any(l => l.Scope == BookingCancellationLineScope.Partial)
                         && !b.Lines.Any(l => l.Scope == BookingCancellationLineScope.Full)))
            // Mas antiguo primero (prioridad de revision). Los T5 no tienen ConfirmedWithClientAt (quedan en
            // Drafted), asi que caemos a DraftedAt.
            .OrderBy(b => b.ConfirmedWithClientAt ?? b.DraftedAt)
            .ToListAsync(ct);

        var rows = candidates
            .Select(b =>
            {
                // Monto/moneda del pendiente T5 (Drafted con lineas Partial): el FiscalLiquidation VO es null,
                // asi que el monto sale de la suma de las lineas Partial RESUELTAS (con monto confirmado). Si
                // ninguna esta resuelta (caso ambiguo/mismatch), queda sin monto -> "pendiente de resolucion".
                var partialLines = b.Lines
                    .Where(l => l.Scope == BookingCancellationLineScope.Partial)
                    .ToList();
                var resolvedPartialLines = partialLines
                    .Where(l => l.ConfirmedGrossCreditAmount.HasValue)
                    .ToList();
                decimal? partialAmount = resolvedPartialLines.Count > 0
                    ? resolvedPartialLines.Sum(l => l.ConfirmedGrossCreditAmount!.Value)
                    : (decimal?)null;
                // Currency de las lineas ya viene en ISO ("ARS"/"USD"). Tomamos la de la primera linea Partial.
                string? partialCurrency = partialLines.Count > 0 ? partialLines[0].Currency : null;

                return new PendingCreditNoteReviewDto
                {
                    BookingCancellationPublicId = b.PublicId,
                    ReservaPublicId = b.Reserva?.PublicId ?? Guid.Empty,
                    ReservaNumero = b.Reserva?.NumeroReserva ?? string.Empty,
                    // Preferimos el nombre del cliente pagador; si no hay, el nombre de la reserva.
                    ClienteNombre = b.Reserva?.Payer?.FullName ?? b.Reserva?.Name ?? string.Empty,
                    // Data-exposure: NUNCA el nombre crudo del enum ("Drafted"/"ManualReviewPending"). Etiqueta
                    // de negocio en español para el operador de la bandeja.
                    Status = ProjectPendingReviewStatusLabel(b.Status),
                    // Los T5 no tienen ConfirmedWithClientAt (quedan Drafted): caemos a DraftedAt.
                    EnteredReviewAt = b.ConfirmedWithClientAt ?? b.DraftedAt,
                    CreditNoteAmount = b.FiscalLiquidation?.FiscalAmountToCredit ?? partialAmount,
                    CreditNoteCurrency = b.FiscalLiquidation?.Currency ?? partialCurrency,
                };
            })
            .ToList();

        return rows;
    }

    /// <summary>
    /// Data-exposure (FRENTE D, ADR-044 T5): traduce el estado interno del BC a una etiqueta de negocio en
    /// español para la bandeja "Comprobantes por resolver". NUNCA se expone el nombre crudo del enum al usuario.
    /// Un <c>Drafted</c> con lineas Partial es una cancelacion parcial cuya NC quedo pendiente de emision; los
    /// estados de revision manual son liquidaciones que el back-office tiene que aprobar/emitir.
    /// </summary>
    private static string ProjectPendingReviewStatusLabel(BookingCancellationStatus status)
        => status switch
        {
            BookingCancellationStatus.Drafted => "Pendiente de emisión",
            BookingCancellationStatus.ManualReviewPending => "En revisión",
            BookingCancellationStatus.RequiresManualReview => "En revisión",
            _ => "En revisión",
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
                "Esta liquidación ya no se puede editar porque cambió de estado. Actualizá la página.",
                invariantCode: "INV-093");
        }

        if (bc.PartialCreditNoteApprovalRequest is null)
        {
            // Defensive: el CHECK chk_BookingCancellations_manualreview_approvalref
            // ya garantiza esto. Si llegamos aca, hubo corrupcion o bypass.
            throw new BusinessInvariantViolationException(
                "Esta cancelación quedó en un estado inconsistente y no se puede editar. " +
                "Contactá al administrador del sistema.",
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
                    "Por control interno, quien edita esta liquidación no puede ser la misma persona " +
                    "que solicitó la cancelación. Tiene que revisarla otro administrador.",
                    invariantCode: "INV-FC1.3-004");
            }
        }
        // Si !isSelfEdit, 4-eyes esta cumplido naturalmente. No hace falta bypass.

        // 4) Cargar inputs para recorrer calculator. Items + supplier ya estan en BD.
        var invoiceItems = await _db.Set<InvoiceItem>()
            .Where(i => i.InvoiceId == bc.OriginatingInvoiceId)
            .ToListAsync(ct);
        // FUGA B3 data-exposure (2026-07-03): mensaje al usuario SIN ids internos; detalle al log.
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == bc.SupplierId, ct);
        if (supplier is null)
        {
            _logger.LogError("EditLiquidation: no se encontro el Supplier {SupplierId} del BC {BcPublicId}.",
                bc.SupplierId, bc.PublicId);
            throw new InvalidOperationException(
                "No se encontró el operador de esta anulación. Consultá con administración.");
        }

        // 5) Aplicar overrides del admin sobre el input.
        // ADR-044 T5 Addendum fix B1(a) (2026-07-11): mismo capeo que ConfirmAsync — CancellationAmount se
        // acota al remanente acreditable real de la factura, no al ImporteTotal a secas (ver el comentario
        // detallado en ConfirmAsync). excludeBookingCancellationId: bc.Id (FRENTE E anti-doble-cap) +
        // seguridad de concurrencia por los indices unicos parciales (M1/C2): ver el comentario detallado en
        // ConfirmAsync — vale identico aca.
        var penaltyOverride = req.OperatorPenaltyAmountOverride ?? 0m;
        var remainingCreditableAmount = await RunUnderInvoiceLockAsync(
            bc.OriginatingInvoiceId,
            () => ComputeInvoiceRemainingCreditableAmountAsync(bc.OriginatingInvoiceId, ct, excludeBookingCancellationId: bc.Id),
            ct);
        var calculatorInput = new FiscalLiquidationInput(
            OriginatingInvoice: bc.OriginatingInvoice,
            Items: invoiceItems,
            Supplier: supplier,
            InvoicingModeAtEvent: bc.FiscalSnapshot.InvoicingModeAtEvent,
            OriginalInvoiceAmount: bc.OriginatingInvoice.ImporteTotal,
            CancellationAmount: remainingCreditableAmount,
            OperatorPenaltyAmount: penaltyOverride,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false,
            Currency: bc.FiscalSnapshot.CurrencyAtEvent ?? "ARS");

        var newLiquidation = _calculator.Calculate(calculatorInput, settings);

        // 6) Re-validacion GR-001: la nueva clasificacion puede haber pasado a
        //    TotalPlusNewInvoice (cambio de inputs). Misma politica que Confirm.
        if (newLiquidation.Kind == CreditNoteKind.TotalPlusNewInvoice)
        {
            // FUGA B4 data-exposure (2026-07-03): sin jerga interna (CreditNoteKind/Fase 1/Reject/BC)
            // en el mensaje al usuario; el detalle tecnico va al log.
            _logger.LogWarning(
                "EditLiquidation BC {BcPublicId}: re-clasificacion dio CreditNoteKind=TotalPlusNewInvoice " +
                "(no soportado en Fase 1; corresponde rechazo del admin y abortar).",
                bc.PublicId);
            throw new InvalidOperationException(
                "Con estos cambios el caso fiscal ya no se puede resolver automáticamente: hay que " +
                "rechazar la edición y anular por el circuito manual. Consultá con administración.");
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
        // ADR-042 §3.5.1 (2026-07-01): el callback puede ser uno de VARIOS (una NC por factura). Ya no cierra
        // la anulacion en el primer exito: actualiza SU fila hija y solo transiciona el BC cuando NO quedan
        // hijas Pending (todas OK -> AwaitingOperatorRefund; alguna Failed -> ArcaRejected). Ver el core.
        await HandleArcaAnnulmentCallbackAsync(
            originatingInvoiceId, succeeded: true, creditNoteInvoiceId: creditNoteInvoiceId,
            afipErrorMessage: null, ct: ct);
    }

    /// <summary>
    /// ADR-042 §3.5.1 (2026-07-01): resultado de reevaluar la completitud de las NCs de una cancelacion tras
    /// un callback de ARCA.
    /// </summary>
    private enum MultiNcOutcome
    {
        /// <summary>Quedan hijas Pending: el BC no se mueve (sigue AwaitingFiscalConfirmation).</summary>
        StillPending,
        /// <summary>Todas las hijas OK: anulacion COMPLETA -> AwaitingOperatorRefund + puntero principal + ND.</summary>
        AllSucceeded,
        /// <summary>Todas resueltas y al menos una fallo: ArcaRejected (revision + retry).</summary>
        PartialFailed,
        /// <summary>Redelivery/carrera: la hija ya estaba en el estado destino o el BC ya avanzo. No-op.</summary>
        NoOp,
    }

    /// <summary>
    /// ADR-042 §3.5.1 (2026-07-01): nucleo comun de los callbacks de ARCA (exito/fallo) para el caso
    /// multi-factura. Localiza la fila hija de esta factura, la actualiza y REEVALUA la completitud del BC
    /// bajo un LOCK PESIMISTA del padre (serializa callbacks concurrentes del mismo BC — mitiga el lost-update
    /// B1). La ND (si la anulacion queda completa) se dispara POST-commit, nunca sosteniendo el lock.
    ///
    /// <para><b>Fallback legacy</b>: un BC sin filas hijas (pre-backfill / post-rollback §6) cae al
    /// comportamiento historico single-factura via <see cref="LegacyHandleArcaCallbackAsync"/>.</para>
    /// </summary>
    private async Task HandleArcaAnnulmentCallbackAsync(
        int originatingInvoiceId, bool succeeded, int creditNoteInvoiceId, string? afipErrorMessage, CancellationToken ct)
    {
        // 1) Localizar la fila hija de esta factura cuya cancelacion espera confirmacion fiscal. Esta lectura
        //    es fuera del lock: solo decide QUE BC lockear. El FOR UPDATE serializa la lectura-decision real.
        var childRef = await _db.BookingCancellationCreditNotes
            .AsNoTracking()
            .Where(c => c.OriginatingInvoiceId == originatingInvoiceId
                     && c.BookingCancellation.Status == BookingCancellationStatus.AwaitingFiscalConfirmation)
            .Select(c => new { c.BookingCancellationId })
            .FirstOrDefaultAsync(ct);

        if (childRef is null)
        {
            // Sin hija para esta factura en un BC AwaitingFiscalConfirmation: puede ser un BC legacy sin hijas
            // (pre-backfill), o que ya transiciono (Force manual). El fallback legacy cubre ambos: replica el
            // comportamiento historico (busca el BC por su puntero singular).
            await LegacyHandleArcaCallbackAsync(originatingInvoiceId, succeeded, creditNoteInvoiceId, afipErrorMessage, ct);
            return;
        }

        var bcId = childRef.BookingCancellationId;

        // 2) Reevaluar bajo lock pesimista del padre (relacional) o directo (InMemory, sin lock).
        var outcome = await RunUnderParentLockAsync(bcId, () =>
            ApplyChildResultAndReevaluateAsync(originatingInvoiceId, succeeded, creditNoteInvoiceId, afipErrorMessage, ct), ct);

        // 3) POST-commit, ya sin lock: si la anulacion quedo COMPLETA, disparar la ND una sola vez (ORDEN NO
        //    NEGOCIABLE ADR-013: la ND sale despues de que TODAS las NCs tienen CAE). Nunca dentro del lock.
        if (outcome == MultiNcOutcome.AllSucceeded)
        {
            await TryEmitDebitNotePostCompletionAsync(bcId, ct);
        }
    }

    /// <summary>
    /// ADR-042 §3.5.1.b (2026-07-01): ejecuta <paramref name="body"/> bajo un lock pesimista del padre
    /// (<c>SELECT ... FOR UPDATE</c> por Id, con <c>lock_timeout</c> acotado). Un solo lock, por Id, sin I/O
    /// externa adentro, liberado al commit. En InMemory (tests unit) corre el cuerpo SIN lock ni transaccion
    /// (la serializacion real se valida en integracion Postgres). Patron NUEVO en el repo (0 usos previos de
    /// FOR UPDATE): mantener acotado y simple.
    /// </summary>
    private async Task<T> RunUnderParentLockAsync<T>(
        int bcId, Func<Task<T>> body, CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
        {
            // InMemory: no soporta FOR UPDATE ni transacciones. Corremos el cuerpo directo.
            return await body();
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // lock_timeout acotado: si otro worker retiene el lock del padre > 5s, el FOR UPDATE tira una
            // excepcion; el job falla limpio y Hangfire lo reintenta re-leyendo fresco (flujo idempotente).
            await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", ct);

            // FOR UPDATE del padre por Id: serializa la lectura-decision de completitud de callbacks
            // concurrentes del MISMO BC. Es un unico lock, por Id, sin segundo recurso -> sin deadlock.
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"BookingCancellations\" WHERE \"Id\" = {0} FOR UPDATE",
                new object[] { bcId }, ct);

            var result = await body();

            await tx.CommitAsync(ct); // libera el lock al commitear
            return result;
        });
    }

    /// <summary>
    /// ADR-044 T5 Addendum (2026-07-11): mismo patron que <see cref="RunUnderParentLockAsync{T}"/>
    /// (<c>SELECT ... FOR UPDATE</c>, <c>lock_timeout</c> acotado), pero lockeando la FACTURA en vez del BC
    /// padre. Existe porque, con la Decision C (varios <see cref="BookingCancellation"/> no-abortados
    /// conviviendo por reserva), DOS eventos de cancelacion distintos — uno parcial (T5) y uno legacy total,
    /// o dos parciales sucesivas — pueden calcular el remanente ACREDITABLE de la MISMA factura casi al mismo
    /// tiempo. Sin este lock, ambos verian el mismo remanente "libre" y emitirian NCs cuya suma supera el
    /// importe de la factura (el riesgo que el propio ADR ya habia anotado como "cerrar antes de T5").
    ///
    /// <para>Se lockea la fila de <c>"Invoices"</c> (siempre existe, no hace falta una tabla nueva ni
    /// <c>pg_advisory_xact_lock</c>). Usado por los TRES puntos de escritura de NC de este modulo: el gate de
    /// <see cref="CancelServiceAsync"/> (T5), y el camino LEGACY de anulacion total dentro de
    /// <c>ConfirmAsync</c>/<c>EditLiquidationAsync</c> (fix B1(a)/C2) — ninguno de los tres puede leer el
    /// remanente de una factura sin este candado.</para>
    /// </summary>
    private async Task<T> RunUnderInvoiceLockAsync<T>(
        int invoiceId, Func<Task<T>> body, CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
        {
            // InMemory: no soporta FOR UPDATE ni transacciones. Corremos el cuerpo directo (la
            // serializacion real se valida en integracion Postgres, mismo criterio que el lock del BC).
            return await body();
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", ct);

            // FOR UPDATE de la factura por Id: serializa la lectura-decision del remanente ACREDITABLE de
            // cualquier evento de cancelacion (parcial o total) que la involucre.
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"Invoices\" WHERE \"Id\" = {0} FOR UPDATE",
                new object[] { invoiceId }, ct);

            var result = await body();

            await tx.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>
    /// ADR-044 T5 Addendum, Decision B (2026-07-11): remanente ACREDITABLE de una factura de venta — cuanto de
    /// su <c>ImporteTotal</c> todavia NO tiene una Nota de Credito viva (<c>Succeeded</c>) o en camino
    /// (<c>Pending</c>) en su contra. Formula UNICA reusada por el gate de <see cref="CancelServiceAsync"/>
    /// (decidir NC-total-de-la-factura vs NC-parcial), el camino LEGACY de anulacion total (fix B1(a),
    /// capear <c>CancellationAmount</c>) y el limite acumulativo de NC parciales sucesivas sobre el MISMO
    /// comprobante (punto 5 de la spec fiscal). SIEMPRE debe leerse con la fila de la factura lockeada
    /// (<c>FOR UPDATE</c>): el camino legacy usa <see cref="RunUnderInvoiceLockAsync{T}"/>; el gate de T5
    /// (<see cref="CancelServiceAsync"/>) ya corre dentro de su transaccion externa que tomo ese lock.
    ///
    /// <para><b>Por que Pending cuenta</b> (no solo Succeeded): si no se reservara el monto apenas se encola
    /// la NC, dos cancelaciones casi simultaneas sobre la misma factura verian el mismo remanente "libre" y
    /// ambas se aprobarian, sumando mas que la factura vale.</para>
    ///
    /// <para><b>Como se descuenta el credito T5 (parcial) que todavia NO emitio su NC</b> (fix B2-backend): en
    /// T5 el cap se reserva por la <see cref="BookingCancellationLine"/> (<see cref="BookingCancellationLine.TargetInvoiceId"/>
    /// + <see cref="BookingCancellationLine.ConfirmedGrossCreditAmount"/>), NO por una hija Pending fantasma.
    /// Por eso el remanente descuenta DOS cosas: (a) las hijas <c>BookingCancellationCreditNote</c> emitidas /
    /// en camino del circuito de anulacion TOTAL, y (b) las reservas por-linea de eventos T5 cuya NC aun no se
    /// emitio. Cuando la NC de una linea se emite (tanda de la pantalla), nace su hija y el monto pasa a
    /// contarse por la hija: por eso (b) excluye las lineas cuyo BC ya tiene una hija EMITIDA para esta factura
    /// (evita el doble conteo linea-vs-hija).</para>
    ///
    /// <para><b><paramref name="excludeBookingCancellationId"/></b>: cuando el camino de anulacion TOTAL
    /// (<see cref="ConfirmAsync"/>/<see cref="EditLiquidationAsync"/>) computa el remanente para acreditar la
    /// factura ENTERA de SU propio BC, la reserva por-linea de una parcial previa absorbida por ese mismo BC
    /// NO debe restarse (la parcial nunca emitio su NC; el total acredita la factura completa). Ese caller pasa
    /// <c>bc.Id</c> para excluir las lineas de su propio BC. El gate de T5 pasa <c>null</c> (dos lineas T5 de la
    /// MISMA reserva sobre la misma factura SI se topean entre si: 60 + 60 sobre 100 -> la segunda se capa a 40).</para>
    ///
    /// <para><b>Por que se libera solo, sin codigo de "liberacion"</b>: cuando ARCA rechaza una NC,
    /// <see cref="ApplyChildResultAndReevaluateAsync"/> (ADR-042) pasa la hija a <c>Failed</c> — que esta
    /// formula NO cuenta (solo <c>Succeeded</c>/<c>Pending</c>) — asi que su monto vuelve a estar disponible
    /// automaticamente para la siguiente NC parcial de esa factura.</para>
    /// </summary>
    // internal (no private, InternalsVisibleTo("TravelApi.Tests") ya configurado): permite a los tests unit
    // ejercitar la formula del remanente directamente, sin pasar por el lock relacional (que es no-op en
    // InMemory de todos modos).
    internal async Task<decimal> ComputeInvoiceRemainingCreditableAmountAsync(
        int invoiceId, CancellationToken ct, int? excludeBookingCancellationId = null)
    {
        var importeTotal = await _db.Invoices.AsNoTracking()
            .Where(i => i.Id == invoiceId)
            .Select(i => (decimal?)i.ImporteTotal)
            .FirstOrDefaultAsync(ct) ?? 0m;

        if (importeTotal <= 0m)
            return 0m;

        // (A) Hijas del circuito de anulacion TOTAL (ADR-042): una con NC vinculada cuenta por el monto real
        //     de esa NC; una Pending legacy sin NC vinculada representa una NC TOTAL en camino -> ImporteTotal.
        var liveChildren = await _db.BookingCancellationCreditNotes.AsNoTracking()
            .Where(c => c.OriginatingInvoiceId == invoiceId
                     && (c.Status == BookingCancellationCreditNoteStatus.Succeeded
                         || c.Status == BookingCancellationCreditNoteStatus.Pending))
            .Select(c => new { c.CreditNoteInvoiceId, c.BookingCancellationId })
            .ToListAsync(ct);

        var creditNoteInvoiceIds = liveChildren
            .Where(c => c.CreditNoteInvoiceId.HasValue)
            .Select(c => c.CreditNoteInvoiceId!.Value)
            .Distinct()
            .ToList();
        var creditNoteAmountsById = creditNoteInvoiceIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.Invoices.AsNoTracking()
                .Where(i => creditNoteInvoiceIds.Contains(i.Id))
                .Select(i => new { i.Id, i.ImporteTotal })
                .ToDictionaryAsync(i => i.Id, i => i.ImporteTotal, ct);

        decimal alreadyCommitted = 0m;
        // BCs que YA tienen una hija EMITIDA (NC vinculada) para esta factura: sus reservas por-linea NO se
        // vuelven a contar en (B) — su monto ya se conto aca por la hija. Evita el doble conteo linea-vs-hija.
        var bcIdsWithEmittedChild = new HashSet<int>();
        foreach (var child in liveChildren)
        {
            if (child.CreditNoteInvoiceId.HasValue
                && creditNoteAmountsById.TryGetValue(child.CreditNoteInvoiceId.Value, out var emittedAmount))
            {
                alreadyCommitted += emittedAmount;
                bcIdsWithEmittedChild.Add(child.BookingCancellationId);
            }
            else
            {
                // Hija Pending legacy sin NC vinculada: NC TOTAL en camino (circuito de anulacion total).
                alreadyCommitted += importeTotal;
            }
        }

        // (B) Reservas por LINEA de eventos de cancelacion PARCIAL (T5) cuya NC todavia no se emitio: cada
        //     linea Scope=Partial con monto confirmado apuntando a esta factura reserva ese monto. Se excluyen
        //     las de un BC ya Aborted/Closed (evento muerto/terminado), las de un BC que ya emitio su hija (ya
        //     contadas en (A)), y las del BC que se esta confirmando ahora (excludeBookingCancellationId).
        var partialLineReservations = await _db.BookingCancellationLines.AsNoTracking()
            .Where(l => l.TargetInvoiceId == invoiceId
                     && l.Scope == BookingCancellationLineScope.Partial
                     && l.ConfirmedGrossCreditAmount != null
                     && l.BookingCancellation.Status != BookingCancellationStatus.Aborted
                     && l.BookingCancellation.Status != BookingCancellationStatus.Closed
                     && (excludeBookingCancellationId == null || l.BookingCancellationId != excludeBookingCancellationId))
            .Select(l => new { l.BookingCancellationId, Amount = l.ConfirmedGrossCreditAmount!.Value })
            .ToListAsync(ct);

        foreach (var reservation in partialLineReservations)
        {
            if (bcIdsWithEmittedChild.Contains(reservation.BookingCancellationId))
                continue; // ya contada por su hija emitida en (A) -> no doble contar
            alreadyCommitted += reservation.Amount;
        }

        var remaining = importeTotal - alreadyCommitted;
        return remaining < 0m ? 0m : remaining;
    }

    /// <summary>
    /// ADR-042 §3.5.1 pasos 2-4 (2026-07-01): dentro del lock, actualiza la fila hija de esta factura y
    /// reevalua la completitud del BC con una lectura FRESCA de BD (no del ChangeTracker). Un solo cuerpo para
    /// exito y fallo. NO dispara la ND (eso es post-commit). Devuelve el <see cref="MultiNcOutcome"/>.
    /// </summary>
    private async Task<MultiNcOutcome> ApplyChildResultAndReevaluateAsync(
        int originatingInvoiceId, bool succeeded, int creditNoteInvoiceId, string? afipErrorMessage, CancellationToken ct)
    {
        // Cargar la hija fresca (dentro del lock) + su BC + la Reserva (para transicionar/loguear estado).
        var child = await _db.BookingCancellationCreditNotes
            .Include(c => c.BookingCancellation)
                .ThenInclude(b => b.Reserva)
            .FirstOrDefaultAsync(c => c.OriginatingInvoiceId == originatingInvoiceId
                     && c.BookingCancellation.Status == BookingCancellationStatus.AwaitingFiscalConfirmation, ct);
        if (child is null)
        {
            // Entre la primera lectura y el lock el BC ya avanzo (Force manual, otro callback). No-op.
            _logger.LogWarning(
                "OnArca*: no hay hija AwaitingFiscalConfirmation para Invoice {InvoiceId} bajo el lock. No-op.",
                originatingInvoiceId);
            return MultiNcOutcome.NoOp;
        }

        var bc = child.BookingCancellation;

        // Idempotencia de callback (redelivery de Hangfire): si la hija ya esta en el estado destino, no-op.
        if (succeeded && child.Status == BookingCancellationCreditNoteStatus.Succeeded)
        {
            _logger.LogInformation("OnArcaSucceeded: hija de Invoice {InvoiceId} ya Succeeded. No-op.", originatingInvoiceId);
            return MultiNcOutcome.NoOp;
        }
        if (!succeeded && child.Status == BookingCancellationCreditNoteStatus.Failed)
        {
            _logger.LogInformation("OnArcaFailed: hija de Invoice {InvoiceId} ya Failed. No-op.", originatingInvoiceId);
            return MultiNcOutcome.NoOp;
        }

        // Actualizar la fila hija segun el resultado de ARCA.
        if (succeeded)
        {
            child.Status = BookingCancellationCreditNoteStatus.Succeeded;
            child.CreditNoteInvoiceId = creditNoteInvoiceId;
        }
        else
        {
            child.Status = BookingCancellationCreditNoteStatus.Failed;
            var err = afipErrorMessage ?? "AFIP rechazo la NC sin mensaje.";
            child.ArcaErrorMessage = err.Length > 1000 ? err[..1000] : err;
        }
        // Persistir la hija DENTRO de la tx antes de contar: asi el conteo fresco ve este resultado.
        await _db.SaveChangesAsync(ct);

        // Reevaluar la completitud del BC (conteo fresco) y transicionar. Compartido con el retry.
        return await ReevaluateBcCompletenessAndTransitionAsync(bc, originatingInvoiceId, ct);
    }

    /// <summary>
    /// ADR-042 §3.5.1 pasos 3-4 (2026-07-01): con una lectura FRESCA de las hijas (ya serializada por el lock),
    /// decide y aplica la transicion del BC: quedan Pending -> AwaitingFiscalConfirmation (resume una anulacion
    /// que estaba en ArcaRejected si se reintento); todas OK -> AwaitingOperatorRefund + puntero principal +
    /// reserva; alguna Failed -> ArcaRejected. Persiste (SaveChanges). Lo comparten el callback y el retry.
    /// </summary>
    private async Task<MultiNcOutcome> ReevaluateBcCompletenessAndTransitionAsync(
        BookingCancellation bc, int triggeringOriginatingInvoiceId, CancellationToken ct)
    {
        var pending = await _db.BookingCancellationCreditNotes
            .CountAsync(c => c.BookingCancellationId == bc.Id
                          && c.Status == BookingCancellationCreditNoteStatus.Pending, ct);
        var failed = await _db.BookingCancellationCreditNotes
            .CountAsync(c => c.BookingCancellationId == bc.Id
                          && c.Status == BookingCancellationCreditNoteStatus.Failed, ct);

        if (pending > 0)
        {
            // Faltan NCs por confirmar: la anulacion esta EN CURSO. Aseguramos AwaitingFiscalConfirmation
            // (importante cuando venimos de un retry que reabrio un ArcaRejected: sin esto el callback de la
            // NC reintentada no encontraria el BC). Limpiamos el error observable.
            if (bc.Status != BookingCancellationStatus.AwaitingFiscalConfirmation)
            {
                bc.Status = BookingCancellationStatus.AwaitingFiscalConfirmation;
                bc.ArcaErrorMessage = null;
                await _db.SaveChangesAsync(ct);
            }
            _logger.LogInformation(
                "ADR-042: BC {BcPublicId} en AwaitingFiscalConfirmation ({Pending} NC pendientes).",
                bc.PublicId, pending);
            return MultiNcOutcome.StillPending;
        }

        if (failed == 0)
        {
            // TODAS las hijas OK -> anulacion COMPLETA. Recien aca se setea el puntero principal, se
            // transiciona la reserva y (post-commit) se dispara la ND. Idempotente si ya estaba completa.
            bc.Status = BookingCancellationStatus.AwaitingOperatorRefund;
            bc.ArcaErrorMessage = null;

            // Puntero principal = NC de la factura principal (bc.OriginatingInvoiceId). Byte-equivalente al
            // caso mono-factura (una sola hija que ES la principal).
            var principalNc = await _db.BookingCancellationCreditNotes
                .Where(c => c.BookingCancellationId == bc.Id && c.OriginatingInvoiceId == bc.OriginatingInvoiceId)
                .Select(c => c.CreditNoteInvoiceId)
                .FirstOrDefaultAsync(ct);
            // Defensa: si la principal (por lo que sea) no tuviera NC, tomar la de cualquier hija OK. Con
            // failed==0 y pending==0 todas tienen NC, asi que esto casi nunca aplica.
            bc.CreditNoteInvoiceId = principalNc ?? await _db.BookingCancellationCreditNotes
                .Where(c => c.BookingCancellationId == bc.Id && c.CreditNoteInvoiceId != null)
                .Select(c => c.CreditNoteInvoiceId)
                .FirstOrDefaultAsync(ct);

            // (2026-07-03) ¿Cerrar DIRECTO por no haber reembolso pendiente del operador? Si la agencia nunca le
            // pago nada al operador por este viaje (receivable $0) y no hay multa pendiente, dejar la anulacion en
            // "esperando reembolso" la manda a un limbo eterno -> cerramos directo. Sino, camino normal a
            // AwaitingOperatorRefund. bc.Status ya quedo en AwaitingOperatorRefund (post-NC) y el puntero seteado,
            // que es lo que el guard de la multa necesita para evaluar bien.
            if (await ShouldAutoCloseWithoutOperatorRefundAsync(bc, ct))
            {
                await ApplyAutoCloseWithoutOperatorRefundAsync(bc, origin: "transicion", ct);
            }
            else
            {
                // Transición + rastro + descarte de la marca por el PUNTO ÚNICO de transición (callback ARCA, sistema).
                await ReservaStatusTransitioner.ApplyAsync(
                    _db, bc.Reserva, EstadoReserva.PendingOperatorRefund, "Forward",
                    actorUserId: null, actorUserName: null,
                    reason: "Cancelacion (ADR-002/042): ARCA confirmo todas las NC, a la espera del reembolso del operador (sistema).", ct: ct);
            }

            _auditService.StageBusinessEvent(
                action: AuditActions.BookingCancellationArcaSucceeded,
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    bc.PublicId,
                    triggeringOriginatingInvoiceId,
                    creditNoteInvoiceId = bc.CreditNoteInvoiceId,
                    multiInvoice = true,
                }),
                userId: bc.ConfirmedByUserId ?? bc.DraftedByUserId,
                userName: bc.ConfirmedByUserName ?? bc.DraftedByUserName);

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "metric:cancellation_arca_succeeded | BcPublicId={BcPublicId} OriginatingInvoiceId={OriginatingInvoiceId} CreditNoteInvoiceId={CreditNoteInvoiceId}",
                bc.PublicId, triggeringOriginatingInvoiceId, bc.CreditNoteInvoiceId);
            return MultiNcOutcome.AllSucceeded;
        }

        // 0 Pending y >= 1 Failed -> ArcaRejected (revision + retry). La(s) NC que salieron NO se revierten.
        bc.Status = BookingCancellationStatus.ArcaRejected;
        // Mensaje observable del BC: el error de una hija fallada (para la bandeja / observabilidad).
        var failedChildError = await _db.BookingCancellationCreditNotes
            .Where(c => c.BookingCancellationId == bc.Id
                     && c.Status == BookingCancellationCreditNoteStatus.Failed
                     && c.ArcaErrorMessage != null)
            .Select(c => c.ArcaErrorMessage)
            .FirstOrDefaultAsync(ct);
        bc.ArcaErrorMessage = failedChildError ?? "AFIP rechazó una o más devoluciones de esta cancelación. Volvé a intentar desde la reserva.";

        _auditService.StageBusinessEvent(
            action: AuditActions.BookingCancellationArcaRejected,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                triggeringOriginatingInvoiceId,
                afipErrorMessage = bc.ArcaErrorMessage,
                partialFailure = true,
            }),
            userId: bc.ConfirmedByUserId ?? bc.DraftedByUserId,
            userName: bc.ConfirmedByUserName ?? bc.DraftedByUserName);

        await _db.SaveChangesAsync(ct);

        _logger.LogError(
            "metric:cancellation_arca_failed | BcPublicId={BcPublicId} OriginatingInvoiceId={OriginatingInvoiceId} FailedChildren={Failed}",
            bc.PublicId, triggeringOriginatingInvoiceId, failed);
        return MultiNcOutcome.PartialFailed;
    }

    /// <summary>
    /// ADR-042 §3.5.1.b minor 1 (2026-07-01): dispara la ND POST-commit (fuera del lock), tras completarse
    /// TODAS las NCs. Recarga el BC con los includes que el gating de la ND necesita. Blindado: si la ND falla,
    /// NO re-lanza (la cancelacion ya esta correcta con las NC; la bandeja "NC sin su ND" la recupera).
    /// </summary>
    private async Task TryEmitDebitNotePostCompletionAsync(int bcId, CancellationToken ct)
    {
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.OriginatingInvoice)
                .ThenInclude(i => i.Tributes)
            .Include(b => b.Supplier)
            .FirstOrDefaultAsync(b => b.Id == bcId, ct);
        if (bc is null) return;

        try
        {
            await TryEmitCancellationDebitNoteAsync(bc, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ADR-013/042: fallo al emitir la ND para BC {BcPublicId} tras completarse las NC. " +
                "La cancelacion queda correcta; la ND queda pendiente de revision.",
                bc.PublicId);
        }
    }

    /// <summary>
    /// ADR-042 §6 (2026-07-01): comportamiento HISTORICO single-factura para BCs SIN filas hijas (legacy
    /// pre-backfill o post-rollback). Byte-identico al OnArcaSucceeded/Failed previo a ADR-042: busca el BC por
    /// su puntero singular <c>OriginatingInvoiceId</c> y transiciona directo en el primer (y unico) callback.
    /// </summary>
    private async Task LegacyHandleArcaCallbackAsync(
        int originatingInvoiceId, bool succeeded, int creditNoteInvoiceId, string? afipErrorMessage, CancellationToken ct)
    {
        if (succeeded)
        {
            var bc = await _db.BookingCancellations
                .Include(b => b.Reserva)
                .Include(b => b.OriginatingInvoice)
                    .ThenInclude(i => i.Tributes)
                .Include(b => b.Supplier)
                .FirstOrDefaultAsync(b =>
                    b.OriginatingInvoiceId == originatingInvoiceId &&
                    b.Status == BookingCancellationStatus.AwaitingFiscalConfirmation, ct);

            if (bc is null)
            {
                _logger.LogWarning(
                    "OnArcaSucceededAsync (legacy): no se encontro BC AwaitingFiscalConfirmation para Invoice {InvoiceId}. No-op.",
                    originatingInvoiceId);
                return;
            }

            bc.Status = BookingCancellationStatus.AwaitingOperatorRefund;
            bc.CreditNoteInvoiceId = creditNoteInvoiceId;

            // (2026-07-03) ¿Cerrar directo por no haber reembolso pendiente del operador? (mismo criterio que el
            // camino multi-factura). bc.Status ya quedo post-NC y el puntero seteado, que es lo que el guard necesita.
            if (await ShouldAutoCloseWithoutOperatorRefundAsync(bc, ct))
            {
                await ApplyAutoCloseWithoutOperatorRefundAsync(bc, origin: "transicion", ct);
            }
            else
            {
                // Transición + rastro + descarte de la marca por el PUNTO ÚNICO de transición (callback ARCA, sistema).
                await ReservaStatusTransitioner.ApplyAsync(
                    _db, bc.Reserva, EstadoReserva.PendingOperatorRefund, "Forward",
                    actorUserId: null, actorUserName: null,
                    reason: "Cancelacion (ADR-002): ARCA confirmo la NC, a la espera del reembolso del operador (sistema).", ct: ct);
            }

            await _auditService.LogBusinessEventAsync(
                action: AuditActions.BookingCancellationArcaSucceeded,
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: JsonSerializer.Serialize(new { bc.PublicId, originatingInvoiceId, creditNoteInvoiceId }),
                userId: bc.ConfirmedByUserId ?? bc.DraftedByUserId,
                userName: bc.ConfirmedByUserName ?? bc.DraftedByUserName,
                ct: ct);

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "metric:cancellation_arca_succeeded | BcPublicId={BcPublicId} OriginatingInvoiceId={OriginatingInvoiceId} CreditNoteInvoiceId={CreditNoteInvoiceId}",
                bc.PublicId, originatingInvoiceId, creditNoteInvoiceId);

            try
            {
                await TryEmitCancellationDebitNoteAsync(bc, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ADR-013: fallo al emitir la ND para BC {BcPublicId} tras NC exitosa (legacy). " +
                    "La cancelacion queda correcta; la ND queda pendiente de revision.",
                    bc.PublicId);
            }
            return;
        }

        // Fallo (legacy): idem OnArcaFailedAsync historico.
        var failedBc = await _db.BookingCancellations
            .FirstOrDefaultAsync(b =>
                b.OriginatingInvoiceId == originatingInvoiceId &&
                b.Status == BookingCancellationStatus.AwaitingFiscalConfirmation, ct);

        if (failedBc is null)
        {
            _logger.LogWarning(
                "OnArcaFailedAsync (legacy): no se encontro BC AwaitingFiscalConfirmation para Invoice {InvoiceId}. No-op.",
                originatingInvoiceId);
            return;
        }

        failedBc.Status = BookingCancellationStatus.ArcaRejected;
        var errorMessage = afipErrorMessage ?? "AFIP rechazo la NC sin mensaje.";
        failedBc.ArcaErrorMessage = errorMessage.Length > 1000 ? errorMessage[..1000] : errorMessage;

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationArcaRejected,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: failedBc.Id.ToString(),
            details: JsonSerializer.Serialize(new { failedBc.PublicId, originatingInvoiceId, afipErrorMessage = failedBc.ArcaErrorMessage }),
            userId: failedBc.ConfirmedByUserId ?? failedBc.DraftedByUserId,
            userName: failedBc.ConfirmedByUserName ?? failedBc.DraftedByUserName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogError("BC {BcPublicId} marked as ArcaRejected (legacy). AFIP error: {Error}",
            failedBc.PublicId, failedBc.ArcaErrorMessage);
        _logger.LogInformation(
            "metric:cancellation_arca_failed | BcPublicId={BcPublicId} OriginatingInvoiceId={OriginatingInvoiceId} ErrorTruncated={ErrorPreview}",
            failedBc.PublicId, originatingInvoiceId,
            failedBc.ArcaErrorMessage.Length > 80 ? failedBc.ArcaErrorMessage[..80] : failedBc.ArcaErrorMessage);
    }

    /// <inheritdoc />
    public async Task<BookingCancellationDto> RetryCreditNotesAsync(
        Guid publicId, string userId, string? userName, CancellationToken ct)
    {
        // ADR-042 §3.6 (2026-07-01): reintenta SOLO las NC faltantes de una anulacion multi-factura que
        // quedo a medias (ArcaRejected, o AwaitingFiscalConfirmation con hijas Failed/atascadas). Idempotente:
        // no re-emite las NC que ya salieron. Serializado por el MISMO lock del padre que los callbacks (B1/M1):
        // un segundo retry concurrente ve la hija ya Pending -> no-op. Molde: RetryDebitNoteEmissionAsync.
        //
        // Permiso: el endpoint exige ReservasCancel (mismo permiso que anular) server-side. No es "deshacer",
        // es COMPLETAR lo ya autorizado en el confirm; por eso cualquier vendedor que podia anular puede reintentar.
        await EnsureFeatureFlagOnAsync(ct);

        var bc = await _db.BookingCancellations
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // Solo aplica a una anulacion a medias. En cualquier otro estado rebota claro (idempotente si ya cerro).
        if (bc.Status != BookingCancellationStatus.ArcaRejected
            && bc.Status != BookingCancellationStatus.AwaitingFiscalConfirmation)
        {
            throw new BusinessInvariantViolationException(
                "Esta anulacion no esta en un estado que se pueda reintentar.",
                invariantCode: "INV-042-RETRY-001");
        }

        // S1 (2026-07-02): AUDITORIA del actor humano. El retry es una operacion fiscal iniciada por un
        // usuario; aunque solo re-encole (la NC sigue en emision), debe dejar rastro auditable de quien lo
        // disparo, cuando y sobre que BC (mismo criterio que el Force). NO commitea el estado de las hijas
        // (eso pasa bajo el lock); LogBusinessEventAsync hace su propio commit del audit.
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationCreditNotesRetried,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                statusAtRetry = bc.Status.ToString(),
                retriedByUserId = userId,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        // Bajo el lock del padre: por cada hija NO Succeeded, re-vincular una NC huerfana ya creada (anti
        // doble-emision) o dejarla Pending BAJO el lock antes de encolar. Reevalua la completitud (puede
        // reabrir el BC a AwaitingFiscalConfirmation para que los callbacks reintentados lo encuentren, o
        // completarlo si todas las faltantes ya tenian NC con CAE). Devuelve las facturas a encolar (fuera
        // del lock: nada de I/O de Hangfire sosteniendo el FOR UPDATE) + si quedo completo por re-vinculacion.
        var (invoicesToEnqueue, completedAllSucceeded) = await RunUnderParentLockAsync(bc.Id,
            () => PrepareCreditNoteRetryUnderLockAsync(bc.Id, ct), ct);

        // Si la anulacion quedo COMPLETA por re-vinculacion de NC huerfanas (todas las faltantes ya tenian
        // CAE), disparar la ND POST-commit (fuera del lock), una sola vez. Igual que el callback.
        if (completedAllSucceeded)
        {
            await TryEmitDebitNotePostCompletionAsync(bc.Id, ct);
        }

        // Fuera del lock: encolar las NC faltantes. requesterIsAdmin: true para que EnqueueAnnulmentAsync NO
        // re-exija el approval del InvoiceAnnulment: la anulacion YA fue autorizada al confirmar; reintentar
        // una NC fallida es COMPLETAR esa accion autorizada, no una nueva. El gate de permiso del endpoint
        // (ReservasCancel) ya corrio server-side.
        //
        // F1 (2026-07-02): la factura YA quedo AnnulmentStatus=Pending DENTRO del lock
        // (PrepareCreditNoteRetryUnderLockAsync), asi que un segundo retry concurrente, al tomar el lock, ve
        // Pending y no re-encola (ventana de doble-CAE cerrada). El enqueue de Hangfire queda afuera del lock.
        // Usamos EnqueueAnnulmentRetryAsync (no EnqueueAnnulmentAsync): no re-aplica el guard "Pending -> throw"
        // (la marca es propia del retry) y no re-exige approval (la anulacion ya se autorizo al confirmar).
        foreach (var invoiceId in invoicesToEnqueue)
        {
            try
            {
                await _invoiceService.EnqueueAnnulmentRetryAsync(
                    id: invoiceId,
                    userId: userId,
                    userName: userName,
                    reason: $"BC retry-credit-notes: {bc.Reason}",
                    approvalRequestId: null,
                    ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nunca un 500 que trabe la reserva: si el enqueue de una NC falla, la hija queda Pending y
                // el proximo retry la vuelve a tomar. Log + seguir con las demas.
                _logger.LogError(ex,
                    "ADR-042 retry-credit-notes: fallo al encolar la anulacion de la factura {InvoiceId} " +
                    "para BC {BcPublicId}. Queda pendiente para el proximo reintento.",
                    invoiceId, bc.PublicId);
            }
        }

        _logger.LogInformation(
            "metric:cancellation_credit_notes_retry | BcPublicId={BcPublicId} Enqueued={Count} By={UserId}",
            bc.PublicId, invoicesToEnqueue.Count, userId);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException("No se pudo completar la operacion. Volve a intentar.");
    }

    /// <summary>
    /// ADR-042 §3.6 (2026-07-01): dentro del lock del padre, prepara el reintento de las NC faltantes. Por
    /// cada hija NO Succeeded: si ya existe una NC creada para su factura (huerfana de un intento previo) la
    /// RE-VINCULA (no re-emite); si no, la deja Pending BAJO el lock. Devuelve las facturas cuyas NC hay que
    /// encolar (fuera del lock). Un segundo retry concurrente, al tomar el lock, ve la hija ya Pending -> no
    /// la re-encola (no-op).
    /// </summary>
    private async Task<(List<int> ToEnqueue, bool CompletedAllSucceeded)> PrepareCreditNoteRetryUnderLockAsync(
        int bcId, CancellationToken ct)
    {
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .FirstAsync(b => b.Id == bcId, ct);

        // Concurrencia (2026-07-02): el caller (RetryCreditNotesAsync) cargo el BC ANTES de tomar el lock, asi
        // que la instancia trackeada tiene un xmin PRE-lock. Bajo el FOR UPDATE ya estamos serializados; si otro
        // retry commiteo mientras esperabamos el lock, esta instancia quedo stale (Status + xmin viejos) y el
        // SaveChanges tiraria DbUpdateConcurrencyException espuria. La RE-CARGAMOS FRESCA (dentro del lock) para
        // que refleje el estado actual (el segundo retry ve el BC ya AwaitingFiscalConfirmation -> StillPending
        // sin re-escribir) y el xmin coincida. La Reserva incluida se refresca por separado si hiciera falta.
        await _db.Entry(bc).ReloadAsync(ct);
        await _db.Entry(bc).Reference(b => b.Reserva).LoadAsync(ct);
        var reservaId = bc.ReservaId;

        var children = await _db.BookingCancellationCreditNotes
            .Where(c => c.BookingCancellationId == bcId
                     && c.Status != BookingCancellationCreditNoteStatus.Succeeded)
            .ToListAsync(ct);

        // B1b (2026-07-02): AnnulmentStatus de las facturas origen para distinguir "job en vuelo" (Pending)
        // de "atascada" (None/Failed). Una hija Pending SIN job vivo hay que re-encolarla; con job vivo NO
        // (seria un doble job). Es la senal que EnqueueAnnulmentAsync ya usa (rechaza re-anular si Pending).
        var originatingIds = children.Select(c => c.OriginatingInvoiceId).Distinct().ToList();
        var annulmentStatusByInvoice = originatingIds.Count == 0
            ? new Dictionary<int, AnnulmentStatus>()
            : await _db.Invoices
                .Where(i => originatingIds.Contains(i.Id))
                .Select(i => new { i.Id, i.AnnulmentStatus })
                .ToDictionaryAsync(x => x.Id, x => x.AnnulmentStatus, ct);

        var toEnqueue = new List<int>();

        foreach (var child in children)
        {
            // (a) ANTI DOBLE-EMISION: buscar una NC ya creada (tipos 3/8/13/53) sobre la MISMA factura origen
            //     de esta reserva. Si existe, RE-VINCULAR (no re-emitir). Misma deteccion que la ND huerfana.
            var orphanCreditNote = await _db.Invoices
                .Where(i => LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante)
                         && i.OriginalInvoiceId == child.OriginatingInvoiceId
                         && i.ReservaId == reservaId)
                .OrderByDescending(i => i.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (orphanCreditNote is not null)
            {
                // S3 (2026-07-02): derivar el estado real del Invoice NC:
                //  - CAE aprobado (Resultado="A" + CAE) -> Succeeded.
                //  - RECHAZADA por ARCA (Resultado="R") -> Failed (con motivo).
                //  - EN VUELO (Resultado null/"PENDING", sin CAE) -> la dejamos Pending (NO Failed): la NC
                //    todavia se esta procesando; marcarla Failed seria prematuro. La re-vinculamos igual (para
                //    no emitir otra) y esperamos su callback. NO la re-encolamos (hay un job/comprobante vivo).
                child.CreditNoteInvoiceId = orphanCreditNote.Id;
                if (orphanCreditNote.Resultado == "A" && !string.IsNullOrWhiteSpace(orphanCreditNote.CAE))
                {
                    child.Status = BookingCancellationCreditNoteStatus.Succeeded;
                    child.ArcaErrorMessage = null;
                }
                else if (orphanCreditNote.Resultado == "R")
                {
                    child.Status = BookingCancellationCreditNoteStatus.Failed;
                    var obs = orphanCreditNote.Observaciones ?? "ARCA rechazo la NC sin mensaje.";
                    child.ArcaErrorMessage = obs.Length > 1000 ? obs[..1000] : obs;
                }
                else
                {
                    // NC en vuelo: no la tocamos mas que el vinculo. Queda Pending esperando su callback.
                    child.Status = BookingCancellationCreditNoteStatus.Pending;
                }
                _logger.LogWarning(
                    "ADR-042 retry: hija de la factura {InvoiceId} re-vinculada a una NC ya creada {NcId} (no re-emitida). Estado={Status}.",
                    child.OriginatingInvoiceId, orphanCreditNote.Id, child.Status);
                continue;
            }

            // (b) No hay NC previa. Re-encolamos las hijas que NO tienen un job de anulacion vivo:
            //     - Failed: la flipeamos a Pending y la encolamos.
            //     - Pending SIN job en vuelo (AnnulmentStatus != Pending): quedo atascada (el confirm no llego
            //       a encolar, o el enqueue fallo) -> la encolamos (sigue Pending, ya lo esta).
            //     Una hija Pending CON job en vuelo (AnnulmentStatus == Pending) NO se re-encola: hay un job
            //     vivo -> es el no-op del segundo retry concurrente / del estado "procesando".
            annulmentStatusByInvoice.TryGetValue(child.OriginatingInvoiceId, out var annulmentStatus);
            bool hasLiveJob = annulmentStatus == AnnulmentStatus.Pending;

            if (child.Status == BookingCancellationCreditNoteStatus.Failed)
            {
                child.Status = BookingCancellationCreditNoteStatus.Pending;
                child.ArcaErrorMessage = null;
                toEnqueue.Add(child.OriginatingInvoiceId);
            }
            else if (child.Status == BookingCancellationCreditNoteStatus.Pending && !hasLiveJob)
            {
                // Atascada: sigue Pending pero sin job -> re-encolar. (No cambia el estado de la hija.)
                toEnqueue.Add(child.OriginatingInvoiceId);
            }
        }

        // F1 (2026-07-02): marcar AnnulmentStatus=Pending de las facturas a encolar DENTRO del lock (write de
        // BD, permitido; el enqueue de Hangfire queda afuera). Asi un segundo retry concurrente, ya serializado
        // por el FOR UPDATE, lee Pending FRESCO y NO re-encola (cierra la ventana de doble-CAE: antes la señal
        // la escribia EnqueueAnnulmentAsync post-commit, despues de soltar el lock). Se persiste en el mismo
        // SaveChanges de abajo. EnqueueAnnulmentAsync se llama con preMarkedPending: true para no rebotar por su
        // propio guard "Pending".
        if (toEnqueue.Count > 0)
        {
            var invoicesToMark = await _db.Invoices
                .Where(i => toEnqueue.Contains(i.Id))
                .ToListAsync(ct);
            foreach (var inv in invoicesToMark)
                inv.AnnulmentStatus = AnnulmentStatus.Pending;
        }

        // Persistir los cambios de las hijas + la señal AnnulmentStatus antes de reevaluar (conteo fresco).
        await _db.SaveChangesAsync(ct);

        // Reevaluar la completitud: reabre a AwaitingFiscalConfirmation si quedan Pending (para que los
        // callbacks reintentados encuentren el BC), o completa si todas las faltantes ya tenian CAE.
        var outcome = await ReevaluateBcCompletenessAndTransitionAsync(bc, bc.OriginatingInvoiceId, ct);

        return (toEnqueue, outcome == MultiNcOutcome.AllSucceeded);
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

    /// <summary>
    /// ADR-044 T3b/T4 (2026-07-10): copy LIMPIO y fijo del unico motivo de revision manual que la ficha muestra
    /// al usuario — "falta elegir a que factura corresponde el cargo" (cancelacion con 2+ facturas activas). Es
    /// FUENTE UNICA: la usa tanto el motor de emision (<c>BuildCancellationDebitNoteItemsAsync</c>, para
    /// persistir el motivo) como el read-model del paso de multa (<c>GetOperatorPenaltySituationAsync</c>, para
    /// exponer <c>ManualReviewReason</c>). No se expone NUNCA el <c>DebitNoteArcaErrorMessage</c> crudo: puede
    /// portar texto tecnico en español que la blocklist de <see cref="SanitizeArcaErrorForUser"/> no ataja (ej.
    /// "OriginatingInvoice no cargada.", "...(M2).", "fail-safe: coleccion no cargada"). El front, para los demas
    /// motivos de revision manual, muestra su propia copy fija.
    /// </summary>
    internal const string TargetInvoiceUnchosenManualReviewMessage =
        "Todavía no se eligió a qué factura corresponde el cargo del operador (la cancelación tiene " +
        "más de una factura activa): elegila antes de emitir.";

    /// <summary>
    /// H3 (2026-06-24): FUENTE UNICA de "¿esta cancelacion tiene una multa del operador pendiente de confirmar
    /// AHORA?" (la confirmacion diferida que emite la ND). Refleja SOLO las precondiciones de ESTADO que valida
    /// <see cref="ConfirmPenaltyAsync"/> (flag maestro, NC total con CAE, idempotencia: penalidad aun Estimated y
    /// sin ND en juego). NO refleja permiso ni 4-eyes (esos los resuelve confirm-penalty al ejecutar).
    ///
    /// <para>La usan dos lectores que deben coincidir SIEMPRE: (1) el read-model <c>canConfirmPenalty</c> del
    /// <see cref="BookingCancellationDto"/> (panel de la cancelacion) y (2) <see cref="HasPendingOperatorPenaltyAsync"/>
    /// (la capacidad <c>CanConfirmOperatorPenalty</c> de la reserva). Extraerla a un metodo puro evita que las dos
    /// derivaciones diverjan.</para>
    /// </summary>
    /// <summary>
    /// H3 (2026-06-24): los CAMPOS SUELTOS de una cancelacion que deciden si su multa esta pendiente de confirmar.
    /// Existe para que <see cref="EvaluateCanConfirmPenalty"/> NO reciba la entidad <c>BookingCancellation</c>: asi
    /// el lector por-reserva puede proyectar a un tipo anonimo en la query (EF Core 8 + Npgsql PROHIBEN construir
    /// un tipo de entidad mapeado dentro de un <c>.Select</c> server-side — tira InvalidOperationException en
    /// runtime; InMemory no lo detecta porque evalua client-side). Los dos lectores arman este struct desde su
    /// fuente (entidad ya cargada en MapToDtoAsync, proyeccion anonima en HasPendingOperatorPenaltyAsync) y
    /// comparten la MISMA regla.
    /// </summary>
    private readonly record struct PenaltyConfirmabilityFields(
        BookingCancellationStatus Status,
        int? CreditNoteInvoiceId,
        PenaltyStatus PenaltyStatus,
        int? DebitNoteInvoiceId,
        DebitNoteStatus DebitNoteStatus);

    /// <returns>(canConfirm, blockedReasonCode). blockedReasonCode es null cuando canConfirm es true.</returns>
    private static (bool CanConfirm, string? BlockedReason) EvaluateCanConfirmPenalty(
        PenaltyConfirmabilityFields fields, bool debitNoteFeatureEnabled)
    {
        if (!debitNoteFeatureEnabled)
            return (false, "DebitNoteFeatureDisabled");

        // La ND nunca sale antes que la NC total: requiere estado post-NC + CreditNoteInvoiceId seteado (CAE).
        if (!PostCreditNoteStatuses.Contains(fields.Status) || fields.CreditNoteInvoiceId is null)
            return (false, "CreditNoteNotYetIssued");

        // Fase A (2026-06-28): cierre sin multa. Si la pata del operador ya se resolvio "sin multa"
        // (Waived), no hay nada que confirmar — es un estado terminal propio, distinto de Confirmed.
        // Lo distinguimos con su propio motivo para que el front muestre "se cerro sin multa" (no
        // "ya tiene Nota de Debito", que aplicaria a una multa real ya emitida).
        if (fields.PenaltyStatus == PenaltyStatus.Waived)
            return (false, "OperatorPenaltyWaived");

        // Idempotencia: si la penalidad ya fue confirmada o la ND ya esta encolada/emitida, no se vuelve a emitir.
        if (fields.PenaltyStatus == PenaltyStatus.Confirmed
            || fields.DebitNoteInvoiceId.HasValue
            || fields.DebitNoteStatus == DebitNoteStatus.Pending
            || fields.DebitNoteStatus == DebitNoteStatus.Issued)
            return (false, "DebitNoteAlreadyInPlay");

        return (true, null);
    }

    /// <inheritdoc />
    public async Task<bool> HasPendingOperatorPenaltyAsync(Guid reservaPublicId, CancellationToken ct)
    {
        // Misma cancelacion vigente que GetByReservaAsync (la mas reciente no abortada). INV-081 garantiza una
        // sola cancelacion activa por reserva. Proyectamos SOLO los campos sueltos que necesita la decision a un
        // tipo ANONIMO (no a la entidad: ver el comentario de PenaltyConfirmabilityFields). No traemos el grafo
        // entero: esto lo llama el armado del DETALLE de la reserva (hot-ish path).
        var row = await _db.BookingCancellations
            .AsNoTracking()
            .Where(b => b.Reserva.PublicId == reservaPublicId
                     && b.Status != BookingCancellationStatus.Aborted)
            .OrderByDescending(b => b.DraftedAt)
            .Select(b => new
            {
                b.Status,
                b.CreditNoteInvoiceId,
                b.PenaltyStatus,
                b.DebitNoteInvoiceId,
                b.DebitNoteStatus,
            })
            .FirstOrDefaultAsync(ct);
        if (row is null) return false;

        var fields = new PenaltyConfirmabilityFields(
            row.Status, row.CreditNoteInvoiceId, row.PenaltyStatus, row.DebitNoteInvoiceId, row.DebitNoteStatus);

        var settings = await _settings.GetEntityAsync(ct);
        var (canConfirm, _) = EvaluateCanConfirmPenalty(fields, settings.EnableCancellationDebitNote);
        return canConfirm;
    }

    /// <inheritdoc />
    public async Task<OperatorPenaltyOutcome> GetOperatorPenaltyOutcomeAsync(Guid reservaPublicId, CancellationToken ct)
    {
        // Misma cancelacion vigente y misma proyeccion a tipo anonimo que HasPendingOperatorPenaltyAsync
        // (ver el comentario de PenaltyConfirmabilityFields sobre por que NO proyectamos a la entidad).
        var row = await _db.BookingCancellations
            .AsNoTracking()
            .Where(b => b.Reserva.PublicId == reservaPublicId
                     && b.Status != BookingCancellationStatus.Aborted)
            .OrderByDescending(b => b.DraftedAt)
            .Select(b => new
            {
                b.Status,
                b.CreditNoteInvoiceId,
                b.PenaltyStatus,
                b.DebitNoteInvoiceId,
                b.DebitNoteStatus,
            })
            .FirstOrDefaultAsync(ct);
        if (row is null) return OperatorPenaltyOutcome.None;

        // Estados TERMINALES de la pata del operador: se leen directo del PenaltyStatus persistido, sin depender
        // del flag maestro. La resolucion (confirmar con multa / cerrar sin multa) ya ocurrio; aunque manana se
        // apague el flag, el cartel "Cerrada sin multa" / "Multa confirmada" sigue siendo verdad para esa reserva.
        if (row.PenaltyStatus == PenaltyStatus.Waived) return OperatorPenaltyOutcome.Waived;
        if (row.PenaltyStatus == PenaltyStatus.Confirmed) return OperatorPenaltyOutcome.Confirmed;

        // No-terminal: ¿esta PENDIENTE de resolver ahora mismo? Reusa la MISMA regla canonica que
        // HasPendingOperatorPenaltyAsync (flag ON + NC total con CAE + penalidad aun Estimated + sin ND en juego).
        var fields = new PenaltyConfirmabilityFields(
            row.Status, row.CreditNoteInvoiceId, row.PenaltyStatus, row.DebitNoteInvoiceId, row.DebitNoteStatus);
        var settings = await _settings.GetEntityAsync(ct);
        var (canConfirm, _) = EvaluateCanConfirmPenalty(fields, settings.EnableCancellationDebitNote);

        // canConfirm => hay algo pendiente de resolver. Si no (NC sin CAE aun, feature off, etc.), no hay pata de
        // operador "en juego" para mostrar: None (el front no pinta cartel ni boton).
        return canConfirm ? OperatorPenaltyOutcome.Pending : OperatorPenaltyOutcome.None;
    }

    /// <inheritdoc />
    public async Task<OperatorPenaltySituationDto> GetOperatorPenaltySituationAsync(
        Guid reservaPublicId, bool userCanClassifyOperatorPenalty, bool isCallerAdmin, CancellationToken ct)
    {
        // Spec "el paso de multa vive en la ficha" (A2, 2026-07-08): read-model DETALLADO del paso de la multa del
        // operador, para que la ficha muestre el cartel/boton exacto sin pedir aparte el detalle de la cancelacion.
        // Misma cancelacion vigente y misma proyeccion a tipo anonimo que GetOperatorPenaltyOutcomeAsync (ver el
        // comentario de PenaltyConfirmabilityFields sobre por que NO proyectamos a la entidad), pero traemos algunos
        // campos mas (monto, moneda, fechas, quien cerro sin multa) para armar el DTO completo.
        var row = await _db.BookingCancellations
            .AsNoTracking()
            .Where(b => b.Reserva.PublicId == reservaPublicId
                     && b.Status != BookingCancellationStatus.Aborted)
            .OrderByDescending(b => b.DraftedAt)
            .Select(b => new
            {
                b.Id,
                b.ReservaId,
                b.Status,
                b.CreditNoteInvoiceId,
                b.PenaltyStatus,
                b.DebitNoteInvoiceId,
                b.DebitNoteStatus,
                b.PenaltyAmountAtEvent,
                b.PenaltyCurrencyAtEvent,
                b.PenaltyConfirmedAt,
                b.PenaltyConfirmedByUserName,
                b.OperatorPenaltyConfirmedDate,
                b.ConfirmedWithClientAt,
                // ADR-044 Fix B (2026-07-13): moneda de la factura destino (ARCA "PES"/"DOL"), para que el modal
                // sepa cuando pedir el TC. Null si el BC no tiene factura origen (no deberia con ND en juego).
                InvoiceMonId = b.OriginatingInvoice != null ? b.OriginatingInvoice.MonId : null,
                // ADR-044 "Deshacer una multa ya emitida" (2026-07-14): fecha de emision (CAE) de la ND vigente,
                // para el aviso informativo RG 4540 en el front (avisar, no bloquear).
                DebitNoteIssuedAt = b.DebitNoteInvoice != null ? b.DebitNoteInvoice.IssuedAt : null,
                // Configuracion de multas de cancelacion (2026-07-14): que tan seguido cobra multa ESTE operador,
                // para sugerir el camino en el paso de la pregunta (ver SuggestedPenaltyPath mas abajo). Se trae
                // en la MISMA query (join a Supplier, sin consulta aparte: no agrega N+1).
                SupplierPenaltyBehavior = b.Supplier.PenaltyBehavior,
            })
            .FirstOrDefaultAsync(ct);

        // Sin cancelacion vigente: paso None, sin acciones. El front no pinta nada.
        if (row is null)
            return new OperatorPenaltySituationDto { State = OperatorPenaltySituationState.None.ToString() };

        // "Pendiente de decidir ahora" reusa la MISMA regla canonica que el boton confirmar/cerrar (flag ON + NC
        // total con CAE + penalidad aun Estimated + sin ND en juego), para no divergir de esa verdad.
        var fields = new PenaltyConfirmabilityFields(
            row.Status, row.CreditNoteInvoiceId, row.PenaltyStatus, row.DebitNoteInvoiceId, row.DebitNoteStatus);
        var settings = await _settings.GetEntityAsync(ct);
        var (isPendingDecision, _) = EvaluateCanConfirmPenalty(fields, settings.EnableCancellationDebitNote);

        // ADR-044 "Deshacer una multa ya emitida" (2026-07-14): ¿hay un evento de deshacer EN VUELO o el ultimo
        // quedo Failed para la ND vinculada? Solo importa cuando la ND esta Issued (ver Derive).
        var (hasPendingAnnulment, hasFailedAnnulment) =
            await GetDebitNoteAnnulmentFlagsAsync(row.DebitNoteInvoiceId, ct);

        // Derivacion del paso: regla de dominio PURA (testeable caso por caso).
        var state = OperatorPenaltySituationRules.Derive(new OperatorPenaltySituationRules.Fields(
            HasLiveCancellation: true,
            PenaltyStatus: row.PenaltyStatus,
            DebitNoteStatus: row.DebitNoteStatus,
            HasDebitNoteInvoice: row.DebitNoteInvoiceId.HasValue,
            IsPendingDecision: isPendingDecision,
            HasPendingDebitNoteAnnulment: hasPendingAnnulment,
            HasFailedDebitNoteAnnulment: hasFailedAnnulment));

        // Monto/moneda: solo tienen sentido cuando hay una multa con monto real. En None no hay cancelacion en juego;
        // en Waived el monto quedo en 0 ("no hubo multa") -> null para que el front no muestre "$0".
        var showAmount = state is not (OperatorPenaltySituationState.None or OperatorPenaltySituationState.Waived);
        decimal? amount = showAmount ? row.PenaltyAmountAtEvent : null;
        // La moneda se muestra en ISO ("ARS"/"USD"), NUNCA el codigo ARCA interno. Solo si hay monto.
        var currency = amount.HasValue ? ProjectPenaltyCurrencyToIsoOrNull(row.PenaltyCurrencyAtEvent) : null;

        // "Desde cuando" del paso: el timestamp mas razonable disponible. Si ya se resolvio la penalidad
        // (confirmada / cerrada sin multa) usamos PenaltyConfirmedAt; si no hay, la fecha fiscal que confirmo el
        // operador; y como ultimo recurso la fecha de la anulacion (util para PendingDecision, que aun no confirmo).
        var since = row.PenaltyConfirmedAt ?? row.OperatorPenaltyConfirmedDate ?? row.ConfirmedWithClientAt;

        // Rastro del cierre sin multa: el waive SI persiste PenaltyConfirmedAt / PenaltyConfirmedByUserName (esos
        // campos se reusan para "quien resolvio la penalidad"). Solo los exponemos en el estado Waived.
        var waivedAt = state == OperatorPenaltySituationState.Waived ? row.PenaltyConfirmedAt : (DateTime?)null;
        var waivedByName = state == OperatorPenaltySituationState.Waived ? row.PenaltyConfirmedByUserName : null;

        // Acciones: combinan ESTADO + PERMISO del usuario (ya resuelto por el caller). El endpoint revalida todo
        // server-side; estos booleanos solo deciden que boton se OFRECE en la ficha.
        //   - Confirmar: solo cuando esta pendiente de decidir.
        //   - Reintentar la ND: cuando fallo el CAE o quedo confirmada sin encolar (un retry a secas puede destrabarla).
        //   - Corregir monto/moneda: cuando la ND quedo trabada por moneda (revision manual) o fallida — se re-captura
        //     monto + moneda y se re-emite (correct-penalty). Una Failed admite ambas (reintentar tal cual, o corregir).
        var canConfirm = state == OperatorPenaltySituationState.PendingDecision && userCanClassifyOperatorPenalty;
        var canRetryDebitNote = userCanClassifyOperatorPenalty && state is
            OperatorPenaltySituationState.DebitNoteFailed or OperatorPenaltySituationState.ConfirmedNoDebitNote;
        var canCorrectAmountCurrency = userCanClassifyOperatorPenalty && state is
            OperatorPenaltySituationState.DebitNoteNeedsAmountCurrency or OperatorPenaltySituationState.DebitNoteFailed;
        //   - Cerrar sin multa desde una multa ya confirmada (fix "multa fantasma"): componemos las DOS
        //     precondiciones que WaiveOperatorPenaltyAsync exige para el waive-desde-Confirmed:
        //       * Precondicion 6 (ND NO en juego): via IsOperatorPenaltyDebitNoteInPlay.
        //       * Precondicion 7 (INV-WAIVE-005): waivear una multa YA confirmada exige rol ADMIN, no solo el
        //         permiso classify. Por eso CanWaive se compone con isCallerAdmin y NO con
        //         userCanClassifyOperatorPenalty: un no-admin con el permiso classify NO debe ver el boton (si lo
        //         viera, al apretarlo rebotaria 409 por INV-WAIVE-005 — el anti-patron "boton que rebota").
        //     En la practica solo aplica a los sub-estados "confirmada sin ND en juego" (DebitNoteFailed con el link
        //     ya suelto, DebitNoteNeedsAmountCurrency, ConfirmedNoDebitNote): Done/DebitNoteQueued SIEMPRE tienen la
        //     ND en juego, asi que ahi da false aunque el estado no se liste explicitamente.
        var canWaive = row.PenaltyStatus == PenaltyStatus.Confirmed
            && !IsOperatorPenaltyDebitNoteInPlay(row.DebitNoteInvoiceId.HasValue, row.DebitNoteStatus)
            && isCallerAdmin;
        //   - Deshacer la ND ya emitida (ADR-044 "Deshacer una multa ya emitida"): SOLO ADMIN (spec UX firmada,
        //     gate B1 2026-07-14 — NO el permiso classify, para no ofrecerle el link ni el Reintentar a quien el
        //     endpoint va a rebotar con INV-UNDO-PERM). En "Done" (emitida con CAE) o en "DebitNoteAnnulmentFailed"
        //     (el último intento fue rechazado por ARCA y la ND sigue viva -> REINTENTAR, mismo endpoint POST
        //     undo-debit-note). El guard INV-UNDO-002 permite el reintento porque el índice único parcial excluye
        //     las filas Failed. "DebitNoteAnnulling" (deshacer en curso) NO lo ofrece: hay que esperar el CAE.
        var canUndoDebitNote = isCallerAdmin && state is
            OperatorPenaltySituationState.Done or OperatorPenaltySituationState.DebitNoteAnnulmentFailed;

        // ADR-044 T3b/T4 (2026-07-10, fix data-exposure): motivo en criollo del atasco, SOLO cuando es el caso
        // DERIVABLE "falta elegir a que factura corresponde el cargo" (2+ facturas activas + algun cargo
        // trasladable sin factura destino resuelta). En ese caso viaja un mensaje LIMPIO y FIJO
        // (TargetInvoiceUnchosenManualReviewMessage), NUNCA el DebitNoteArcaErrorMessage crudo — ese campo puede
        // portar texto tecnico en español (ej. "OriginatingInvoice no cargada.", "...(M2).") que la blocklist de
        // saneo no ataja. Para cualquier OTRO motivo de revision manual, ManualReviewReason queda null y el front
        // muestra su propia copy fija ("falta confirmar el monto y la moneda"). El mismo criterio de derivacion
        // que usa el motor de emision para rutear ESTE caso.
        string? manualReviewReason = null;
        if (state == OperatorPenaltySituationState.DebitNoteNeedsAmountCurrency
            && await IsManualReviewBecauseTargetInvoiceUnchosenAsync(row.Id, row.ReservaId, ct))
        {
            manualReviewReason = TargetInvoiceUnchosenManualReviewMessage;
        }

        // ADR-044 "Deshacer una multa ya emitida" (M4, gap 3): monto que quedaria a favor del cliente si se
        // deshace AHORA (variante "ya pagó" del modal). Se calcula SOLO cuando se puede deshacer, con la MISMA
        // formula que acuña el credito al consumarse (ver ClientCreditService.CreateEntryFromDebitNoteUndoAsync).
        var collectedPenaltyAmount = canUndoDebitNote
            ? await ComputeCollectedPenaltyForUndoAsync(
                row.ReservaId, row.PenaltyAmountAtEvent, row.PenaltyCurrencyAtEvent, ct)
            : (decimal?)null;

        // ADR-044 (M4, gap 2): rastro del ULTIMO deshacer CONSUMADO (fila hija Succeeded mas reciente de ESTE BC).
        // Se busca por BookingCancellationId, NO por la ND actual: al consumarse un deshacer la ND se desvincula
        // (DebitNoteInvoiceId=null), asi que la fila Succeeded apunta a una ND que ya no es la vigente.
        var lastDebitNoteUndo = await GetLastConsummatedDebitNoteUndoAsync(row.Id, ct);

        return new OperatorPenaltySituationDto
        {
            State = state.ToString(),
            Amount = amount,
            Currency = currency,
            Since = state == OperatorPenaltySituationState.None ? null : since,
            CanConfirm = canConfirm,
            CanRetryDebitNote = canRetryDebitNote,
            CanCorrectAmountCurrency = canCorrectAmountCurrency,
            CanWaive = canWaive,
            CanUndoDebitNote = canUndoDebitNote,
            WaivedAt = waivedAt,
            WaivedByName = waivedByName,
            // GAP conocido (2026-07-08): el revert-waive no persiste rastro en la entidad (solo audit log). Sin
            // migracion (fuera de alcance de esta noche), estos van null. El campo existe para no romper el contrato.
            RevertedAt = null,
            RevertedByName = null,
            ManualReviewReason = manualReviewReason,
            // ADR-044 Fix B (2026-07-13): moneda de la factura (ISO) + fecha sugerida del TC, para que el modal de
            // correccion sepa cuando mostrar el campo de tipo de cambio y que fecha proponer.
            InvoiceCurrency = ResolveInvoiceCurrencyIso(row.InvoiceMonId),
            SuggestedExchangeRateDate = row.OperatorPenaltyConfirmedDate,
            // Solo tiene sentido cuando se puede deshacer (o ya se esta deshaciendo/fallo el intento): en
            // cualquier otro estado no hay ND vigente que avisar por RG 4540.
            DebitNoteIssuedAt = state is OperatorPenaltySituationState.Done
                or OperatorPenaltySituationState.DebitNoteAnnulling
                or OperatorPenaltySituationState.DebitNoteAnnulmentFailed
                ? row.DebitNoteIssuedAt
                : null,
            CollectedPenaltyAmount = collectedPenaltyAmount,
            LastDebitNoteUndo = lastDebitNoteUndo,
            // Configuracion de multas de cancelacion (2026-07-14): sugerencia PURA (Dominio) a partir de que tan
            // seguido cobra multa este operador. Solo da algo distinto de null en la etapa de la pregunta
            // (PendingDecision) — ver el XML-doc de OperatorPenaltySituationRules.SuggestPenaltyPath.
            SuggestedPenaltyPath = OperatorPenaltySituationRules.SuggestPenaltyPath(state, row.SupplierPenaltyBehavior),
        };
    }

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (M4, gap 3): calcula la porcion EFECTIVAMENTE COBRADA de la multa
    /// (lo que se acuñaria como saldo a favor si se deshace ahora), en la moneda de la ND. Espejo EXACTO de la
    /// formula del reconciliador (<c>DebitNoteAnnulmentReconciliation</c> / <c>CreateEntryFromDebitNoteUndoAsync</c>):
    /// <c>max(0, gross − pendiente)</c>, con el pendiente neteado contra el saldo por moneda de la reserva via
    /// <see cref="ReservaService.ComputePendingPenaltyForDisplay"/> (fuente unica, no se duplica la cuenta).
    /// Devuelve <c>null</c> si no hay monto congelado o la moneda no se puede resolver a ISO (el front omite la linea).
    /// </summary>
    private async Task<decimal?> ComputeCollectedPenaltyForUndoAsync(
        int reservaId, decimal? grossPenalty, string? penaltyCurrencyAtEvent, CancellationToken ct)
    {
        if (!grossPenalty.HasValue)
            return null;

        var penaltyCurrencyIso = ProjectPenaltyCurrencyToIsoOrNull(penaltyCurrencyAtEvent);
        if (penaltyCurrencyIso is null)
            return null;

        // Saldo de la reserva en la moneda de la multa (0 si no hay fila). MISMA regla PURA que usa el
        // reconciliador al acuñar (OperatorPenaltyUndoRules.ComputeCollectedPenalty), así el número que muestra
        // el modal ("le va a quedar $ X a favor") coincide EXACTAMENTE con lo que se acuñaría — y nunca es un
        // crédito fantasma (ver el XML-doc de OperatorPenaltyUndoRules).
        var penaltyCurrencyBalance = await _db.ReservaMoneyByCurrency
            .AsNoTracking()
            .Where(m => m.ReservaId == reservaId && m.Currency == penaltyCurrencyIso)
            .Select(m => (decimal?)m.Balance)
            .FirstOrDefaultAsync(ct) ?? 0m;

        return OperatorPenaltyUndoRules.ComputeCollectedPenalty(grossPenalty.Value, penaltyCurrencyBalance);
    }

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (M4, gap 2): trae el rastro del ULTIMO deshacer CONSUMADO
    /// (fila hija Succeeded mas reciente por <c>RequestedAt</c>) de este BC, o <c>null</c> si nunca se deshizo.
    /// Query chica (top-1). Datos listos para el cartel, sin IDs ni jerga interna.
    /// </summary>
    private async Task<LastDebitNoteUndoDto?> GetLastConsummatedDebitNoteUndoAsync(
        int bookingCancellationId, CancellationToken ct)
    {
        return await _db.Set<BookingCancellationDebitNoteAnnulment>()
            .AsNoTracking()
            .Where(a => a.BookingCancellationId == bookingCancellationId
                     && a.Status == DebitNoteAnnulmentStatus.Succeeded)
            .OrderByDescending(a => a.RequestedAt)
            .Select(a => new LastDebitNoteUndoDto
            {
                UndoneAt = a.RequestedAt,
                UndoneByName = a.RequestedByUserName,
                Reason = a.Reason,
            })
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// ADR-044 T3b/T4 (2026-07-10, fix data-exposure): true si la Nota de Debito de este BC quedo en revision
    /// manual PORQUE falta elegir a que factura corresponde el cargo del operador (el UNICO motivo de revision
    /// manual que la ficha traduce a un mensaje limpio; para el resto, el front usa su copy fija). Deriva el mismo
    /// criterio que el motor de emision (<c>BuildCancellationDebitNoteItemsAsync</c>): hay 2+ facturas de venta
    /// ACTIVAS en la reserva, Y existe al menos un cargo TRASLADABLE al cliente (<c>Kind != Withholding</c> y no
    /// absorbido) SIN factura destino resuelta (<c>TargetInvoiceId == null</c>). No mira el
    /// <c>DebitNoteArcaErrorMessage</c> persistido (que puede portar texto tecnico): reconstruye la condicion en
    /// vivo, asi el mensaje al usuario nunca depende de un string interno.
    /// </summary>
    private async Task<bool> IsManualReviewBecauseTargetInvoiceUnchosenAsync(
        int bookingCancellationId, int reservaId, CancellationToken ct)
    {
        var activeInvoiceCount = (await LoadActiveSaleInvoicesForReservaAsync(reservaId, ct)).Count;
        if (activeInvoiceCount <= 1)
            return false; // con 1 sola factura activa el destino se autocompleta; nunca es "falta elegir".

        // ¿Hay algun cargo trasladable al cliente (no retencion fiscal, no absorbido) sin factura destino resuelta?
        return await _db.BookingCancellationLineOperatorCharges
            .AnyAsync(c => c.BookingCancellationLine.BookingCancellationId == bookingCancellationId
                        && c.Kind != OperatorChargeKind.Withholding
                        && c.ClientTransferMode != ClientTransferMode.Absorbed
                        && c.TargetInvoiceId == null, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OperatorPenaltySituationDto>> GetOperatorPenaltySituationsAsync(
        Guid reservaPublicId, bool userCanClassifyOperatorPenalty, bool isCallerAdmin, CancellationToken ct,
        bool canSeeCost = true)
    {
        // ADR-044 T1 (2026-07-10): version LISTA de GetOperatorPenaltySituationAsync, un elemento POR OPERADOR con
        // multa en juego. Reusa el metodo singular para el operador PRINCIPAL (bc.SupplierId): es la fuente de
        // verdad de hoy y garantiza que el caso mono-operador (el 100% de los BCs de hoy) da EXACTAMENTE el mismo
        // resultado que antes de esta tanda (parity — ver test dedicado). Los operadores SECUNDARIOS (si los hay,
        // ADR-025) se agregan con su propia derivacion a nivel LINEA (BookingCancellationLine), la unica fuente
        // que sabe su multa individual.
        var primarySituation = await GetOperatorPenaltySituationAsync(
            reservaPublicId, userCanClassifyOperatorPenalty, isCallerAdmin, ct);

        // Sin cancelacion vigente o sin nada que mostrar: lista vacia (la ficha no pinta ningun cartel, igual que
        // el singular con State="None").
        if (primarySituation.State == OperatorPenaltySituationState.None.ToString())
            return Array.Empty<OperatorPenaltySituationDto>();

        var bcRow = await _db.BookingCancellations
            .AsNoTracking()
            .Where(b => b.Reserva.PublicId == reservaPublicId && b.Status != BookingCancellationStatus.Aborted)
            .OrderByDescending(b => b.DraftedAt)
            .Select(b => new
            {
                b.Id,
                b.SupplierId,
                b.Status,
                b.CreditNoteInvoiceId,
                b.PenaltyStatus,
                b.DebitNoteStatus,
                b.DebitNoteInvoiceId,
            })
            .FirstOrDefaultAsync(ct);
        if (bcRow is null)
            return Array.Empty<OperatorPenaltySituationDto>(); // defensivo: no deberia pasar si primarySituation != None.

        var primarySupplier = await _db.Suppliers
            .AsNoTracking()
            .Where(s => s.Id == bcRow.SupplierId)
            .Select(s => new { s.PublicId, s.Name })
            .FirstOrDefaultAsync(ct);
        primarySituation.SupplierPublicId = primarySupplier?.PublicId;
        primarySituation.SupplierName = primarySupplier?.Name;

        // ADR-044 T2 Addendum (2026-07-10): cargos tipificados de CADA operador con multa en juego (Fee/Tax/
        // Withholding, Retenida/FacturadaAparte), para que la ficha pueda listar el detalle sin pedir aparte el
        // extracto del operador. UNA sola query para toda la cancelacion, agrupada en memoria por operador
        // (volumen chico, back-office).
        //
        // SECURITY (menor security, 2026-07-10): el MONTO de cada cargo es dato de COSTO. Sin cobranzas.see_cost
        // (canSeeCost false) NO cargamos el desglose — la lista Charges viaja VACIA, mismo criterio que enmascara
        // PenaltyRetained/PaidToOperator en OperatorRefundReadModelService. No exponemos "estructura con montos en
        // 0" porque un cargo sin su monto no aporta nada util y el Kind/DocumentRef igual filtrarian info de costo.
        var chargesRows = canSeeCost
            ? await _db.BookingCancellationLineOperatorCharges
                .AsNoTracking()
                .Where(c => c.BookingCancellationLine.BookingCancellationId == bcRow.Id)
                .Select(c => new
                {
                    SupplierId = c.BookingCancellationLine.SupplierId,
                    c.PublicId,
                    c.Kind,
                    c.CollectionMode,
                    c.Amount,
                    c.Currency,
                    c.DocumentRef,
                    c.ConfirmedAt,
                    c.ClientTransferMode,
                    c.ManagementFeeAmount,
                    // ADR-044 T4 (2026-07-10): a que factura de venta se traslada este cargo (null = sin resolver
                    // todavia, ver el XML-doc de OperatorChargeDto.TargetInvoicePublicId) + el TC estimado.
                    TargetInvoicePublicId = c.TargetInvoiceId == null ? (Guid?)null : c.TargetInvoice!.PublicId,
                    c.EstimatedExchangeRateToClientInvoiceCurrency,
                    c.EstimatedExchangeRateSource,
                    c.EstimatedExchangeRateAt,
                    c.EstimatedExchangeRateJustification,
                })
                .ToListAsync(ct)
            : null;

        List<OperatorChargeDto> ChargesForSupplier(int supplierId) => chargesRows is null
            ? new List<OperatorChargeDto>() // sin visibilidad de costo: sin desglose (enmascarado en el borde del server).
            : chargesRows
                .Where(c => c.SupplierId == supplierId)
                .Select(c => new OperatorChargeDto
                {
                    PublicId = c.PublicId,
                    Kind = c.Kind.ToString(),
                    CollectionMode = c.CollectionMode.ToString(),
                    Amount = c.Amount,
                    Currency = Monedas.Normalizar(c.Currency),
                    DocumentRef = c.DocumentRef,
                    ConfirmedAt = c.ConfirmedAt,
                    // ADR-044 T3a: como se traslada este cargo al cliente en la ND + el fee de gestion, si tiene.
                    ClientTransferMode = c.ClientTransferMode.ToString(),
                    ManagementFeeAmount = c.ManagementFeeAmount,
                    // ADR-044 T3b/T4: factura destino + TC estimado (los necesita el desplegable de "elegir/
                    // corregir factura" y el recuadro de TC cruzado del front).
                    TargetInvoicePublicId = c.TargetInvoicePublicId,
                    EstimatedExchangeRateToClientInvoiceCurrency = c.EstimatedExchangeRateToClientInvoiceCurrency,
                    EstimatedExchangeRateSource = c.EstimatedExchangeRateSource?.ToString(),
                    EstimatedExchangeRateAt = c.EstimatedExchangeRateAt,
                    EstimatedExchangeRateJustification = c.EstimatedExchangeRateJustification,
                })
                .ToList();

        primarySituation.Charges = ChargesForSupplier(bcRow.SupplierId);

        var result = new List<OperatorPenaltySituationDto> { primarySituation };

        // Operadores SECUNDARIOS: lineas cuyo SupplierId es distinto del principal del BC.
        var secondaryLines = await _db.BookingCancellationLines
            .AsNoTracking()
            .Where(l => l.BookingCancellationId == bcRow.Id && l.SupplierId != bcRow.SupplierId)
            .Select(l => new
            {
                l.SupplierId,
                SupplierPublicId = l.Supplier.PublicId,
                SupplierName = l.Supplier.Name,
                // Configuracion de multas de cancelacion (2026-07-14): mismo dato que trae el principal, para
                // sugerir el camino tambien en operadores SECUNDARIOS.
                SupplierPenaltyBehavior = l.Supplier.PenaltyBehavior,
                l.PenaltyStatus,
                l.PenaltyAmount,
                l.PenaltyCurrency,
                l.PenaltyConfirmedAt,
                // ADR-044 T3a (2026-07-10): estado de la ND POR LINEA — el flujo de confirmacion escalonada lo pone
                // en ManualReview cuando este operador confirmo despues de que la ND del principal ya salio (su cargo
                // quedo afuera -> nota de debito complementaria a mano). Es el marcador REAL que reemplaza al viejo
                // "conteo de operadores confirmados" para decidir el estado "necesita revision" por operador.
                l.DebitNoteStatus,
            })
            .ToListAsync(ct);
        if (secondaryLines.Count == 0)
            return result; // caso mono-operador (el 99.9% de hoy): lista de UN elemento, igual que el singular.

        var settings = await _settings.GetEntityAsync(ct);
        // El gate "hay algo para decidir AHORA" es a nivel de TODA la cancelacion (post-NC con CAE + flag), no
        // por operador — se calcula UNA vez y se comparte. Usamos un PenaltyStatus/DebitNoteInvoiceId/DebitNoteStatus
        // neutros (Estimated/sin ND) para que EvaluateCanConfirmPenalty no corte antes de tiempo por el estado
        // Confirmed/Waived de OTRO operador: esos campos alli solo importan para la idempotencia BC-level, que
        // aca no aplica (estamos evaluando el gate compartido, no la idempotencia de un operador puntual).
        var sharedFields = new PenaltyConfirmabilityFields(
            bcRow.Status, bcRow.CreditNoteInvoiceId, PenaltyStatus.Estimated, null, DebitNoteStatus.NotApplicable);
        var (isPendingDecisionForBc, _) = EvaluateCanConfirmPenalty(sharedFields, settings.EnableCancellationDebitNote);

        // ADR-044 "Deshacer una multa ya emitida": mismo dato compartido que el singular (la ND compartida del
        // BC padre es UNA sola, sin importar cuantos operadores tenga en juego).
        var (hasPendingAnnulmentForBc, hasFailedAnnulmentForBc) =
            await GetDebitNoteAnnulmentFlagsAsync(bcRow.DebitNoteInvoiceId, ct);

        // ADR-044 T3a (menor 3, review 2026-07-10): el estado del operador PRINCIPAL YA NO se pisa con
        // "MultiOperatorNeedsManualReview" por el solo hecho de que haya mas de un operador confirmado — ese
        // override MENTIA cuando el motor emite bien una ND multi-operador (el principal quedaba "necesita revision"
        // aunque su ND salio perfecta). El estado del principal sale del singular, que refleja el estado REAL de la
        // ND compartida del BC (Emitida/Encolada/Fallida/etc.). Los operadores SECUNDARIOS derivan su estado del
        // MARCADOR por linea (line.DebitNoteStatus), no de un conteo (ver DeriveForOperator).

        foreach (var group in secondaryLines.GroupBy(l => l.SupplierId))
        {
            // Prioridad de estado dentro del propio operador: Confirmed > Waived > Estimated. En la practica todas
            // las lineas de un mismo operador se mueven juntas (Allocate/Reverse las tocan todas parejo), pero esto
            // es defensivo ante datos parciales.
            var linePenaltyStatus =
                group.Any(l => l.PenaltyStatus == PenaltyStatus.Confirmed) ? PenaltyStatus.Confirmed :
                group.Any(l => l.PenaltyStatus == PenaltyStatus.Waived) ? PenaltyStatus.Waived :
                PenaltyStatus.Estimated;

            // ¿Este operador quedo marcado individualmente para resolucion manual (nota de debito complementaria)?
            // Se deriva del marcador REAL de sus lineas, no de un conteo de operadores.
            var isOperatorSpecificManual = group.Any(l => l.DebitNoteStatus == DebitNoteStatus.ManualReview);

            var state = OperatorPenaltySituationRules.DeriveForOperator(new OperatorPenaltySituationRules.LineFields(
                LinePenaltyStatus: linePenaltyStatus,
                IsPendingDecision: isPendingDecisionForBc,
                BcDebitNoteStatus: bcRow.DebitNoteStatus,
                IsOperatorSpecificManual: isOperatorSpecificManual,
                HasPendingDebitNoteAnnulment: hasPendingAnnulmentForBc,
                HasFailedDebitNoteAnnulment: hasFailedAnnulmentForBc));

            var showAmount = state is not (OperatorPenaltySituationState.None or OperatorPenaltySituationState.Waived);
            decimal? amount = showAmount
                ? group.Where(l => l.PenaltyStatus == PenaltyStatus.Confirmed).Sum(l => l.PenaltyAmount ?? 0m)
                : null;
            var currencyRaw = group.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.PenaltyCurrency))?.PenaltyCurrency;
            var currency = amount.HasValue ? ProjectPenaltyCurrencyToIsoOrNull(currencyRaw) : null;
            var since = group.Where(l => l.PenaltyConfirmedAt.HasValue).Max(l => (DateTime?)l.PenaltyConfirmedAt);

            // Acciones: CanConfirm/CanWaive SI son operator-aware (via ConfirmPenaltyAsync/WaiveOperatorPenaltyAsync
            // con SupplierPublicId, ya resueltos en esta tanda). CanRetryDebitNote/CanCorrectAmountCurrency quedan
            // en false para operadores SECUNDARIOS a proposito (ADR-044 T1, alcance): esos dos endpoints todavia son
            // solo del operador principal (BC-level); ofrecerlos aca seria el anti-patron "boton que rebota".
            var canConfirm = state == OperatorPenaltySituationState.PendingDecision && userCanClassifyOperatorPenalty;
            // CanWaive de un SECUNDARIO NO mira el ND-en-juego del BC padre (ese documento es de OTRO operador,
            // el principal): espeja EXACTO lo que WaiveOperatorPenaltyAsync valida para un secundario (ver su
            // Precondicion 6, ADR-044 T1) — si mirara ese campo, el boton rebotaria 409 cuando el principal ya
            // tiene su ND en curso, aunque ESTE operador nunca la haya tocado.
            var canWaive = linePenaltyStatus == PenaltyStatus.Confirmed && isCallerAdmin;

            var first = group.First();
            result.Add(new OperatorPenaltySituationDto
            {
                State = state.ToString(),
                Amount = amount,
                Currency = currency,
                Since = state == OperatorPenaltySituationState.None ? null : since,
                CanConfirm = canConfirm,
                CanRetryDebitNote = false,
                CanCorrectAmountCurrency = false,
                // Deshacer tambien es BC-level (la ND es COMPARTIDA): mismo criterio que las dos de arriba,
                // queda false para secundarios a proposito (CanUndoDebitNote default = false).
                CanWaive = canWaive,
                // GAP conocido (ADR-044 T1): el schema de linea no tiene "quien cerro sin multa" (solo el BC padre
                // lo tiene, y ese campo es del operador principal). Se puede agregar en una tanda futura si hace
                // falta mostrar el nombre; por ahora solo viaja la fecha.
                WaivedAt = state == OperatorPenaltySituationState.Waived ? since : null,
                WaivedByName = null,
                RevertedAt = null,
                RevertedByName = null,
                // GAP conocido (ADR-044 T3b/T4): el motivo de revision manual vive HOY en bc.DebitNoteArcaErrorMessage
                // (nivel BC padre, describe al operador PRINCIPAL). Un SECUNDARIO no tiene su propio campo de motivo
                // todavia, asi que se deja null a proposito (mismo criterio que WaivedByName arriba) en vez de
                // atribuirle a este operador un texto que puede ser de otro.
                ManualReviewReason = null,
                SupplierPublicId = first.SupplierPublicId,
                SupplierName = first.SupplierName,
                // Configuracion de multas de cancelacion (2026-07-14): misma regla PURA que el principal.
                SuggestedPenaltyPath = OperatorPenaltySituationRules.SuggestPenaltyPath(state, first.SupplierPenaltyBehavior),
                Charges = ChargesForSupplier(group.Key),
            });
        }

        return result;
    }

    /// <summary>
    /// ADR-044 T1 (2026-07-10): resuelve a que OPERADOR corresponde una accion sobre la multa (confirmar / cerrar
    /// sin multa) cuando la cancelacion puede tener servicios de MAS de un proveedor (ADR-025 ya modela
    /// <see cref="BookingCancellationLine.SupplierId"/> por linea). Es el punto UNICO donde se aplica la regla de
    /// la spec: "sin parametro = si hay UNA sola linea con multa, esa; si hay varias, error claro pidiendo
    /// especificar".
    ///
    /// <para><b>Retrocompatible</b>: el 100% de los BCs de hoy tienen lineas de UN solo operador (incluidos los
    /// legacy con la linea sintetica ServiceId=0 del backfill ADR-025), asi que en la practica esto SIEMPRE
    /// resuelve solo, sin que el caller tenga que mandar nada nuevo — el comportamiento es byte-identico al de
    /// antes de esta tanda.</para>
    /// </summary>
    private static int ResolveTargetSupplierId(BookingCancellation bc, Guid? requestedSupplierPublicId)
    {
        if (requestedSupplierPublicId.HasValue)
        {
            var requestedLine = bc.Lines.FirstOrDefault(l => l.Supplier?.PublicId == requestedSupplierPublicId.Value);
            if (requestedLine is not null)
                return requestedLine.SupplierId;

            // Fallback legacy (2026-07-10, non-bloqueante del review): un BC MUY anterior a ADR-025 puede no
            // tener lineas todavia (sin backfill). En ese caso el unico operador conocido es el del BC padre; si
            // el guid pedido coincide con ese operador, resolvemos a bc.SupplierId (simetrico a la rama sin
            // parametro, que ya devuelve bc.SupplierId cuando no hay lineas). Requiere bc.Supplier cargado.
            if (bc.Lines.Count == 0 && bc.Supplier?.PublicId == requestedSupplierPublicId.Value)
                return bc.SupplierId;

            throw new BusinessInvariantViolationException(
                "El operador indicado no tiene servicios cancelados en esta anulación.",
                invariantCode: "INV-ADR044-OPERATOR-NOT-FOUND");
        }

        var distinctSupplierIds = bc.Lines.Select(l => l.SupplierId).Distinct().ToList();
        // BC legacy sin lineas (muy anterior a ADR-025): el unico operador conocido es el del BC padre.
        if (distinctSupplierIds.Count == 0)
            return bc.SupplierId;
        if (distinctSupplierIds.Count == 1)
            return distinctSupplierIds[0];

        throw new BusinessInvariantViolationException(
            "Esta anulación tiene multas de más de un operador. Indicá cuál operador estás resolviendo.",
            invariantCode: "INV-ADR044-OPERATOR-REQUIRED");
    }

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

        // 2026-06-24: el gate de permiso (clasificar penalidad propia) ya viene resuelto en
        // userCanClassifyAgencyPenalty (el controller hace Admin-OR-permiso). Ademas, mas abajo,
        // requesterIsAdmin se usa para que el Admin SALTEE el 4-eyes del confirm-penalty: hoy el
        // dueno es el unico Admin y pedirse doble firma a si mismo es teatro (se auto-aprobaba).
        // El bypass deja SIEMPRE un audit AdminSelfAuthorized (rastro para el contador) y esta
        // condicionado SOLO al rol Admin: el dia que existan varios admins se puede volver a
        // exigir 4-eyes por policy (la maquinaria de approval NO se borro).

        var settings = await _settings.GetEntityAsync(ct);

        // === Precondicion 1: flag maestro. Con OFF el endpoint es INERTE (rechaza, no muta
        // nada) -> byte-identidad con el comportamiento previo a ADR-014. ===
        if (!settings.EnableCancellationDebitNote)
            throw new InvalidOperationException(
                "La emisión de notas de débito por penalidad no está disponible en este momento. " +
                "Consultá con administración.");

        // === Precondicion 2: el BC existe (404 si no). Cargamos los mismos Includes que
        // el gating necesita: factura original + sus Tributos (IIBB) + Supplier + Reserva.
        // Mismo set que OnArcaSucceededAsync para que TryEmit no se quede corto.
        // ADR-044 T1 (2026-07-10): sumamos Lines + su Supplier: es lo que necesita
        // ResolveTargetSupplierId para saber a que operador corresponde esta confirmacion
        // cuando la cancelacion tiene servicios de mas de uno (ADR-025). ===
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.OriginatingInvoice)
                .ThenInclude(i => i.Tributes)
            .Include(b => b.Supplier)
            .Include(b => b.Lines)
                .ThenInclude(l => l.Supplier)
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // === Precondicion 3: permiso elevado, ya resuelto server-side por el controller.
        // A diferencia del path sincrono (que degrada a pass-through si falta el permiso),
        // aca lo EXIGIMOS: el flujo diferido existe para disparar la ND, no tiene sentido
        // sin el permiso que la habilita. ===
        if (!userCanClassifyAgencyPenalty)
            throw new BusinessInvariantViolationException(
                "No tenés permiso para confirmar la multa del operador. Pedíselo a un administrador.",
                invariantCode: "INV-ADR014-PERM");

        // === Precondicion 4: estado post-NC con CAE. Nunca emitir la ND antes que la NC. ===
        if (!PostCreditNoteStatuses.Contains(bc.Status) || bc.CreditNoteInvoiceId is null)
            throw new BusinessInvariantViolationException(
                "Todavía no se puede confirmar la multa del operador: la nota de crédito al cliente " +
                "aún no está confirmada por la AFIP.",
                invariantCode: "INV-ADR014-001");

        // === ADR-044 T1 (2026-07-10): a que OPERADOR corresponde esta confirmacion. Retrocompatible: si la
        // cancelacion tiene lineas de UN solo operador (el 100% de los BCs de hoy), se resuelve solo — mismo
        // comportamiento byte-a-byte que antes de esta tanda. Si tiene lineas de VARIOS operadores (ADR-025) y
        // el request no trae SupplierPublicId, rechaza pidiendo que se especifique (mejor pedir que adivinar).
        // ===
        var targetSupplierId = ResolveTargetSupplierId(bc, request.SupplierPublicId);
        var isPrimaryOperator = targetSupplierId == bc.SupplierId;
        var targetLines = bc.Lines.Where(l => l.SupplierId == targetSupplierId).ToList();
        // Supplier real de ESTE operador (para el default de concepto y la auditoria): el de sus propias lineas
        // si las tiene, o el del BC padre como fallback defensivo (BC legacy sin lineas).
        var targetSupplier = targetLines.FirstOrDefault()?.Supplier ?? bc.Supplier;

        // === Precondicion 5: concepto que emite ND. Resolvemos el concepto efectivo (el
        // explicito del request, o el default por operador). Regla fiscal firmada: emiten ND
        // TANTO el cargo propio de la agencia (gravado) COMO la penalidad pass-through del
        // operador (no gravada, se le cobra al cliente replicando la multa del operador). Solo
        // los conceptos de seguro NO emiten ND aca (revision manual). Ver ConceptEmitsDebitNote.
        //
        // NOTA: cuando el concepto efectivo es pass-through, el default por operador puede dar
        // pass-through aunque el request no traiga ConceptKind -> es el caso CENTRAL del panel
        // (informar la multa del operador). ADR-044 T1: el default mira al operador RESUELTO
        // arriba (targetSupplier), no siempre al principal del BC (bc.Supplier). ===
        var effectiveConcept = request.ConceptKind
            ?? DefaultConceptFromSupplier(targetSupplier?.PenaltyOwnership);
        if (!ConceptEmitsDebitNote(effectiveConcept))
            throw new BusinessInvariantViolationException(
                "Este concepto no emite Nota de Debito automatica (es un seguro u otro caso de " +
                "revision manual). Esta acción emite la ND por penalidad/cargo de cancelacion.",
                invariantCode: "INV-ADR014-002");

        // === Precondicion 6: pre-check de idempotencia (B1, §3.4 pieza 1). Rebota con 409
        // idempotente si la ND ya esta en juego o la penalidad ya fue confirmada por una
        // corrida anterior. La condicion PenaltyStatus==Confirmed es la que cierra la ventana
        // de doble emision tras un crash entre crear-la-ND y vincularla: la marca Confirmed
        // se persiste ANTES de crear la ND (pieza 2 abajo). ===
        // Fase A (2026-06-28): Waived es un estado terminal de la pata del operador (se cerro SIN multa).
        // Confirmar una multa sobre una cancelacion ya cerrada sin multa es contradictorio -> rebota 409,
        // mismo candado que comparte con el cierre sin multa (el primero que gana fija el estado).
        //
        // ADR-044 T1 (2026-07-10): para el operador PRINCIPAL el candado sigue siendo el snapshot del BC padre
        // (byte-identico a como era antes de esta tanda: hoy el 100% de los BCs solo tienen ese operador),
        // INCLUIDO el chequeo de "ND en juego" (el UNICO slot de Nota de Debito que existe hoy a nivel BC
        // describe justamente al principal). Para un operador SECUNDARIO, ese snapshot describe a OTRO operador
        // (el principal), no a este: el candado de ESTE operador vive PURAMENTE en SUS PROPIAS lineas ("cada
        // linea confirma la suya", spec ADR-044) — NO miramos el DebitNoteInvoiceId/DebitNoteStatus del BC padre
        // para un secundario, porque son el documento (real o en curso) de OTRO operador y no dicen nada sobre
        // si ESTE ya se resolvio. Sin este split, confirmar al principal primero (que puede terminar emitiendo
        // su ND real) dejaria al secundario BLOQUEADO para siempre por un documento que no es el suyo — rompiendo
        // el requisito central de esta tanda: "multa de 2 operadores confirmable por separado".
        var debitNoteAlreadyInPlay = isPrimaryOperator
            ? bc.PenaltyStatus == PenaltyStatus.Confirmed
              || bc.PenaltyStatus == PenaltyStatus.Waived
              || bc.DebitNoteInvoiceId.HasValue
              || bc.DebitNoteStatus == DebitNoteStatus.Pending
              || bc.DebitNoteStatus == DebitNoteStatus.Issued
            : targetLines.Any(l => l.PenaltyStatus == PenaltyStatus.Confirmed || l.PenaltyStatus == PenaltyStatus.Waived);
        if (debitNoteAlreadyInPlay)
            throw new BusinessInvariantViolationException(
                "La multa del operador de esta cancelación ya fue resuelta (confirmada o cerrada sin multa). " +
                "No se vuelve a procesar.",
                invariantCode: "INV-ADR014-003");

        // === Precondicion 7: fecha de confirmacion del operador valida (400). No futura;
        // no anterior a la fecha de la cancelacion (ConfirmedWithClientAt). ===
        // FUGA B5 data-exposure (2026-07-03): el mensaje llega al usuario (400) — en criollo, sin el
        // nombre interno del campo (OperatorConfirmationDate).
        var operatorDate = request.OperatorConfirmationDate;
        if (operatorDate.Date > DateTime.UtcNow.Date)
            throw new ArgumentException(
                "La fecha de confirmación no puede ser una fecha futura.", nameof(request));
        if (bc.ConfirmedWithClientAt.HasValue &&
            operatorDate.Date < bc.ConfirmedWithClientAt.Value.Date)
            throw new ArgumentException(
                "La fecha de confirmación no puede ser anterior a la fecha de la anulación.",
                nameof(request));

        // === 4-eyes (M2, §3.6). Obligatorio si NO hay soporte documental O si el monto
        // supera el umbral configurable, aunque el caller tenga el permiso base. Mismo patron
        // que Confirm: si falta el approval valido, tiramos ApprovalRequiredException (el
        // controller la traduce a 409 requiresApproval). ===
        var requiresFourEyes =
            string.IsNullOrWhiteSpace(request.SupportingDocumentReference) ||
            request.ConfirmedPenaltyAmount > settings.CancellationDebitNoteFourEyesThreshold;
        if (requiresFourEyes)
        {
            if (requesterIsAdmin)
            {
                // BYPASS Admin (2026-06-24): el Admin confirma la penalidad DIRECTO, sin doble firma.
                // Exigimos un motivo no vacio (reusamos OverrideReason del request) y dejamos el audit
                // AdminSelfAuthorized para que el contador tenga el rastro. El mismo SaveChanges que
                // persiste la marca de no-retorno (paso c) atomiza tambien este audit (LogBusinessEvent
                // hace su propio SaveChanges, pero corre ANTES del commit del paso c).
                var bypassReason = request.OverrideReason?.Trim();
                if (string.IsNullOrWhiteSpace(bypassReason))
                    throw new BusinessInvariantViolationException(
                        "El administrador debe indicar un motivo para confirmar la penalidad sin doble firma.",
                        invariantCode: "INV-ADMIN-SELFAUTH");

                await LogAdminSelfAuthorizedAsync(
                    bypassedGate: "ConfirmPenaltyFourEyes",
                    entityName: AuditActions.BookingCancellationEntityName,
                    entityId: bc.Id.ToString(),
                    reason: bypassReason,
                    amount: request.ConfirmedPenaltyAmount,
                    // La moneda de la multa: la explicita del request, o la del primer line de ESTE operador, o ARS.
                    currency: await ResolvePenaltyCurrencyForAuditAsync(bc, request.PenaltyCurrency, ct, targetSupplierId),
                    userId: userId,
                    userName: userName,
                    ct: ct);
            }
            else
            {
                await EnsureFourEyesApprovalAsync(bc, request, userId, ct);
            }
        }

        // === ADR-044 T1 (2026-07-10, fix B1 security): las escrituras al SNAPSHOT FISCAL del BC padre
        // (ConceptKind, PenaltyStatus, PenaltyAmountAtEvent, PenaltyCurrencyAtEvent, PenaltyConfirmedBy*,
        // OperatorPenaltyConfirmedDate, SupportingDocumentReference) describen la CARA UNICA hacia el cliente,
        // que hoy corresponde al operador PRINCIPAL del BC. Confirmar la multa de un operador SECUNDARIO NO debe
        // tocar ese snapshot: pisaria los datos del principal (incluso con su Nota de Debito YA emitida con CAE)
        // = corrupcion fiscal. Para un secundario, su multa vive 100% en SUS lineas (PenaltyStatus/PenaltyAmount/
        // RefundCap/PenaltyCurrency, que se actualizan mas abajo en Allocate/Persist, scoped a targetSupplierId)
        // + el audit event (que registra supplierPublicId). El dato "fecha de confirmacion del operador" y
        // "referencia del documento" de un secundario NO tienen columna a nivel linea todavia: viajan en el audit
        // y se persistiran en columnas propias en T2 (NO agregamos columnas en esta tanda). ===
        if (isPrimaryOperator)
        {
            // Aplicar la clasificacion (B1, §3.4 pieza 2, paso a). Reusa el MISMO metodo del path sincrono via el
            // record comun: setea ConceptKind, PenaltyStatus=Confirmed, DebitNotePurpose, PenaltyAmountAtEvent + la
            // auditoria del clasificador/confirmador, y enforza las guardas (permiso elevado + anti-reclasificacion,
            // esta ultima = EnsureConceptNotLockedByDebitNote, que solo aplica al principal: la ND en juego del BC
            // describe al principal, no a un secundario).
            var classification = new PenaltyClassificationInput(
                PenaltyConceptKind: effectiveConcept,
                PenaltyStatus: PenaltyStatus.Confirmed,
                DebitNotePurpose: request.DebitNotePurpose
                    ?? Domain.Entities.DebitNotePurpose.PenaltyOrCancellationCharge,
                ConfirmedPenaltyAmount: request.ConfirmedPenaltyAmount);
            CaptureDebitNoteClassification(
                bc, classification, userId, userName,
                userCanClassifyAgencyPenalty: true, // ya validado en la precondicion 3
                debitNoteFeatureEnabled: true,
                // El confirmador diferido sella la auditoria del clasificador SOLO si el BC aun no tiene
                // clasificador (sin esto el gating B3 rutea a revision manual y la ND nunca emite). Si ya hay un
                // clasificador previo (ej. clasificado en el Dia 0), NO se pisa: el confirmador queda registrado en
                // PenaltyConfirmedBy*. Ver Precondicion 3 + nota anti-clobber en CaptureDebitNoteClassification.
                sealClassifierAuditWhenMissing: true);

            // Las dos fechas nuevas del diferido (eje fiscal del plazo + soporte) — snapshot del padre.
            bc.OperatorPenaltyConfirmedDate = operatorDate;
            bc.SupportingDocumentReference = request.SupportingDocumentReference;

            // Paso b-quater (B1 security, 2026-07-08): persistir la MONEDA DECLARADA de la multa en el snapshot del
            // BC (PenaltyCurrencyAtEvent), NO solo en las lineas. Es la moneda en la que el usuario dijo que el
            // operador retuvo la multa (ISO "USD"/"ARS"; el front la manda con default USD). El gating de la ND la
            // compara contra la moneda de la factura para NO estampar la ND por el numero equivocado: una multa
            // tipeada "en pesos" sobre una factura en dolares saldria como una ND en dolares por el mismo numero
            // (~1500x). Solo la seteamos si el request la trae: si viene vacia (confirmaciones VIEJAS, como el caso
            // real que quedo pendiente), la dejamos null a proposito y el gating rutea a revision manual cuando la
            // factura es extranjera (conservador: NO adivinamos la moneda). Nunca convertimos con TC (eso es del
            // contador). Esto tambien es lo que hace que FreezeDebitNoteSnapshot NO tenga que inventar la moneda.
            if (!string.IsNullOrWhiteSpace(request.PenaltyCurrency))
                bc.PenaltyCurrencyAtEvent = Monedas.Normalizar(request.PenaltyCurrency);
        }

        // Paso b-bis (CAMBIO 3, 2026-06-24): registrar la MONEDA en que el operador retuvo la multa, en la(s)
        // linea(s) de ESTE operador. Es SOLO captura/registro: NO cambia la moneda en la que se EMITE la ND al
        // cliente (eso sigue como hoy). Scoped a targetSupplierId (ADR-044 T1): antes de esta tanda se aplicaba a
        // TODAS las lineas del BC sin importar el operador, lo que habria pisado la moneda de otros operadores.
        // Este write es a nivel LINEA, asi que corre para principal Y secundario (cada uno registra la suya).
        await PersistPenaltyCurrencyOnLinesAsync(bc, request.PenaltyCurrency, ct, targetSupplierId);

        // Paso b-ter (FASE 0, 2026-06-28): bajar el REEMBOLSO ESPERADO del operador por la multa confirmada.
        // Imputa la multa a la(s) linea(s) del operador RESUELTO arriba (targetSupplierId — ADR-044 T1: antes
        // de esta tanda quedaba hardcodeado al operador principal del BC, bug M2 del rediseño de multas: las
        // multas de operadores secundarios se perdian) y recalcula RefundCap = capBeforePenalty − multa (por
        // moneda, nunca cruzado, nunca negativo). Asi "Reembolsos a cobrar" deja de sobreestimar. Es a nivel LINEA
        // (corre para ambos). Pasamos effectiveConcept EXPLICITO: para un secundario, bc.ConceptKind describe al
        // principal, asi que el neteo debe decidirse por el concepto de ESTE operador, no por el del padre.
        await AllocateConfirmedPenaltyToLinesAsync(
            bc, request.ConfirmedPenaltyAmount, request.PenaltyCurrency, ct, targetSupplierId, effectiveConcept,
            userId: userId, userName: userName,
            // ADR-044 T3b Decision 1 (2026-07-10): factura elegida en el MISMO paso de confirmar la multa, para
            // cuando la reserva tiene 2+ facturas activas.
            requestedTargetInvoicePublicId: request.TargetInvoicePublicId);

        // === Auditoria del "confirmado" STAGED, no guardada de una (fix atomicidad 2026-07-01). Antes esta
        // llamada era LogBusinessEventAsync, que hace su PROPIO SaveChanges y por lo tanto dejaba
        // PenaltyStatus=Confirmed DURABLE aca mismo, ANTES del reconciler. Si el reconciler (abajo) despues
        // fallaba, la cancelacion quedaba a medias: multa confirmada, sin Nota de Debito, y trabada (las guardas
        // de idempotencia impiden re-confirmar o cerrar sin multa). Al STAGEAR, la marca de no-retorno + esta
        // auditoria no se hacen durables hasta que el reconciler paso OK y corre la unica SaveChanges de abajo. ===
        _auditService.StageBusinessEvent(
            action: AuditActions.BookingCancellationArcaSucceeded,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                action = "deferred-penalty-confirmed",
                // ADR-044 T1: el concepto EFECTIVO del operador resuelto (para un secundario, bc.ConceptKind
                // describe al principal, asi que loguear ese seria enganoso). Ver fix B1.
                conceptKind = effectiveConcept.ToString(),
                confirmedAmount = request.ConfirmedPenaltyAmount,
                operatorConfirmationDate = operatorDate,
                hasSupportingDocument = !string.IsNullOrWhiteSpace(request.SupportingDocumentReference),
                fourEyesApplied = requiresFourEyes,
                // ADR-044 T1 (2026-07-10): a que operador corresponde esta confirmacion (rastro multi-operador).
                supplierPublicId = targetSupplier?.PublicId,
                isPrimaryOperator,
            }),
            userId: userId,
            userName: userName);

        // === ATOMICIDAD (fix 2026-07-01): el reconciler del POOL de saldo a favor del operador corre AHORA,
        // ANTES de persistir la marca de no-retorno. Si tira (INV-SUPCREDIT-001 = ese saldo a favor ya se aplico a
        // otra reserva y no se puede "destruir" sin revertir esa aplicacion; o un fallo de base en su SaveChanges),
        // TODAVIA no se guardo NADA de esta confirmacion: la marca Confirmed y la auditoria staged se DESCARTAN y
        // el error sale limpio (el 409 de negocio con su mensaje claro). Asi la cancelacion NO queda a medias y el
        // usuario puede corregir (revertir esa aplicacion) y reintentar.
        //
        // Por que reordenar es seguro para el pool: confirmar la multa es NET-NEUTRAL (§4.6) — Multa(+) y
        // RefundCap->Y(-) se cancelan — asi que el pool objetivo es el MISMO leyendo el estado pre-marca o
        // post-marca. Y por que da atomicidad sin transaccion explicita: la excepcion del reconciler ocurre ANTES
        // de cualquier SaveChanges (la suya y la de abajo), de modo que la marca no se persiste ni en Postgres ni
        // en InMemory; y una SaveChanges unica es atomica de por si.
        //
        // ADR-044 T1 (2026-07-10): reconciliamos el pool del operador RESUELTO (targetSupplierId), no siempre
        // el principal del BC — el RefundCap que acaba de cambiar es el de ESE operador. ===
        await ReconcileSupplierCreditPoolAsync(targetSupplierId, userId, userName, ct);

        // === Marca de no-retorno + auditoria staged + cambios de lineas, en una unica SaveChanges. Recien ACA la
        // penalidad queda Confirmed de forma durable (si el reconciler ya guardo sus cambios de pool, esta flushea
        // lo que reste; si fue no-op, guarda todo junto). ===
        await _db.SaveChangesAsync(ct);

        // === Emision de la ND (paso POSTERIOR y RECUPERABLE). TryEmit reusa el motor de ADR-013: su propio
        // gating (anti-doble-cobro re-evaluado con query fresca del Dia N, §3.8/R13), la emision async, el
        // snapshot y su propio SaveChanges que vincula DebitNoteInvoiceId. NO toca el balance ni el estado de la
        // reserva (B2).
        //
        // BLINDAJE (fix 2026-07-01): si la emision falla (ARCA rebota, hay una factura en vuelo, un fallo de base),
        // NO propagamos la excepcion como 500. Un 500 aca dejaria la reserva TRABADA: la multa ya quedo Confirmed
        // (durable, arriba) y las guardas de idempotencia impiden re-confirmar o cerrar sin multa. En su lugar
        // dejamos la ND en REVISION MANUAL — estado consistente que la bandeja "Notas de debito por revisar" ya
        // sabe recuperar (y que el endpoint retry-debit-note puede reintentar) — y devolvemos EXITO-con-aviso. La
        // combinacion "multa confirmada + ND pendiente de revision" es un estado CONSISTENTE; un 500 con estado a
        // medias NO lo es. ===
        // ADR-044 T3a (2026-07-10, fix B1 confirmacion escalonada): la ND al cliente es UNA sola por cancelacion,
        // que el motor (BuildCancellationDebitNoteItemsAsync) arma con un renglon por cargo de CUALQUIER operador
        // confirmado. Como cada operador se confirma por separado, el ORDEN de las confirmaciones importa y hay que
        // manejar los tres casos sin dejar NUNCA un cargo perdido en silencio:
        if (isPrimaryOperator)
        {
            // Operador PRINCIPAL: dispara la emision. El motor ve TODOS los cargos confirmados hasta este momento
            // (si un secundario confirmo antes, ya entra en el mismo comprobante).
            WarnIfDebitNoteLate(bc, operatorDate, settings);
            // A3 (2026-07-08): la ND se atribuye a quien CONFIRMA la multa (userId/userName), no al confirmador de la anulacion.
            try
            {
                await TryEmitCancellationDebitNoteAsync(bc, ct, actorUserId: userId, actorUserName: userName);
            }
            catch (Exception emissionError) when (emissionError is not OperationCanceledException)
            {
                _logger.LogError(emissionError,
                    "metric:cancellation_debit_note_emission_failed | BcPublicId={BcPublicId} | " +
                    "La multa quedo confirmada pero la emision de la Nota de Debito fallo; se deja en revision manual.",
                    bc.PublicId);
                await MarkDebitNoteEmissionForManualReviewAsync(bc.Id, ct);
            }
        }
        else if (bc.PenaltyStatus == PenaltyStatus.Confirmed && !bc.DebitNoteInvoiceId.HasValue)
        {
            // (a) SECUNDARIO y el PRINCIPAL ya confirmo, PERO su ND todavia no se materializo (no hay comprobante
            //     vinculado: quedo en revision manual, o a medias). Disparamos la emision AHORA: el motor arma la ND
            //     con los cargos de AMBOS operadores. El guard de idempotencia de TryEmit protege cualquier carrera
            //     (si el principal ya la vinculo entre medias, TryEmit es no-op).
            WarnIfDebitNoteLate(bc, operatorDate, settings);
            try
            {
                await TryEmitCancellationDebitNoteAsync(bc, ct, actorUserId: userId, actorUserName: userName);
            }
            catch (Exception emissionError) when (emissionError is not OperationCanceledException)
            {
                _logger.LogError(emissionError,
                    "metric:cancellation_debit_note_emission_failed | BcPublicId={BcPublicId} | " +
                    "confirmacion de operador secundario: la emision de la Nota de Debito fallo; queda en revision manual.",
                    bc.PublicId);
                await MarkDebitNoteEmissionForManualReviewAsync(bc.Id, ct);
            }
        }
        else if (bc.DebitNoteInvoiceId.HasValue)
        {
            // (b) SECUNDARIO y la ND del PRINCIPAL YA salio (o esta en vuelo): no se puede meter este cargo en un
            //     comprobante ya emitido. En vez de perderlo en silencio, lo dejamos VISIBLE en la ficha: marcamos
            //     las lineas de ESTE operador como "pendiente de resolver a mano" (nota de debito complementaria).
            //     NO tocamos el estado de la ND del BC padre: esa es del principal, esta bien y es Issued/Pending.
            await MarkSecondaryChargeAsComplementaryManualAsync(bc, targetSupplierId, userId, userName, ct);
        }
        else
        {
            // (c) SECUNDARIO y el PRINCIPAL todavia NO confirmo: este operador confirmo PRIMERO. Su cargo queda
            //     registrado a nivel linea y entrara en la ND cuando el principal confirme y dispare la emision
            //     (que ya vera ambos cargos). No hay nada mas que hacer ahora.
            _logger.LogInformation(
                "metric:cancellation_secondary_operator_penalty_confirmed | BcPublicId={BcPublicId} Supplier={SupplierId} | " +
                "Multa de operador secundario confirmada; entrara en la Nota de Debito cuando el principal la emita.",
                bc.PublicId, targetSupplierId);
        }

        _logger.LogInformation(
            "metric:cancellation_debit_note_deferred_confirmed | BcPublicId={BcPublicId} Amount={Amount}",
            bc.PublicId, request.ConfirmedPenaltyAmount);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException("No se pudo completar la operación. Volvé a intentar.");
    }

    /// <inheritdoc />
    public async Task<BookingCancellationDto> RetryDebitNoteEmissionAsync(
        Guid publicId,
        string userId,
        string? userName,
        CancellationToken ct,
        bool userCanClassifyAgencyPenalty = false)
    {
        // RECUPERACION (fix 2026-07-01): reintenta EMITIR la Nota de Debito de una cancelacion cuya multa YA quedo
        // confirmada pero cuya ND nunca se llego a emitir/vincular (quedo a medias por un fallo de emision o del
        // reconciler). Es el camino para DESTRABAR esa reserva desde la UI: confirm-penalty rebota por idempotencia
        // (la multa ya esta Confirmed) y cerrar sin multa tambien, asi que sin este endpoint la reserva se queda
        // visible en la bandeja pero sin ninguna accion posible. NO re-confirma la multa: solo re-dispara la ND.

        var settings = await _settings.GetEntityAsync(ct);
        if (!settings.EnableCancellationDebitNote)
            throw new InvalidOperationException(
                "La emisión de notas de débito por penalidad no está disponible en este momento. " +
                "Consultá con administración.");

        // Mismo gate fiscal que confirmar la multa: emite el MISMO comprobante. El controller lo resuelve
        // server-side; lo EXIGIMOS aca tambien (defensa en profundidad, no confiar en el frontend).
        if (!userCanClassifyAgencyPenalty)
            throw new BusinessInvariantViolationException(
                "No tenés permiso para emitir la Nota de Débito del operador. Pedíselo a un administrador.",
                invariantCode: "INV-ADR014-RETRY-PERM");

        // Mismos Includes que ConfirmPenaltyAsync: el gating de la ND necesita la factura origen + sus Tributos.
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.OriginatingInvoice)
                .ThenInclude(i => i.Tributes)
            .Include(b => b.Supplier)
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // Solo aplica al estado "multa confirmada pero ND sin emitir". Cualquier otro caso rebota claro.
        if (bc.PenaltyStatus != PenaltyStatus.Confirmed)
            throw new BusinessInvariantViolationException(
                "La multa del operador todavía no fue confirmada. Confirmala primero para poder emitir la Nota de Débito.",
                invariantCode: "INV-ADR014-RETRY-001");
        // Una ND ya EMITIDA (Issued, con CAE) o EN VUELO (Pending) no se reintenta -> rebota. PERO una ND FALLIDA
        // (rechazada por AFIP, sin CAE, job terminado) SI se reintenta (funcional B1, 2026-07-08): el link apunta a
        // un comprobante MUERTO que hay que reemplazar por uno nuevo. Ese caso lo destraba la seccion critica del lock
        // (suelta el link muerto y re-emite). Sin esto, el boton "Reintentar" del cartel de ND fallida devolvia un 409
        // falso ("ya fue emitida o encolada"), porque una Failed SIEMPRE tiene DebitNoteInvoiceId seteado.
        if (bc.DebitNoteInvoiceId.HasValue && bc.DebitNoteStatus != DebitNoteStatus.Failed)
            throw new BusinessInvariantViolationException(
                "La Nota de Débito de esta cancelación ya fue emitida o encolada. No hace falta reintentar.",
                invariantCode: "INV-ADR014-RETRY-002");
        if (!PostCreditNoteStatuses.Contains(bc.Status) || bc.CreditNoteInvoiceId is null)
            throw new BusinessInvariantViolationException(
                "Todavía no se puede emitir la Nota de Débito: la nota de crédito al cliente aún no está " +
                "confirmada por la AFIP.",
                invariantCode: "INV-ADR014-RETRY-003");

        // CANDADO anti doble-retry (security 2026-07-08): dos reintentos SIMULTANEOS pasan el guard
        // DebitNoteInvoiceId==null de arriba (leido FUERA de transaccion) y ambos llegarian a CreateAsync -> DOS
        // Notas de Debito con CAE por la misma multa. Serializamos la seccion critica (re-chequeo del guard +
        // emision/vinculacion) bajo el FOR UPDATE del padre — el MISMO molde que ya usan los callbacks de ARCA
        // (RunUnderParentLockAsync). En InMemory (tests unit) corre directo, sin lock ni transaccion.
        await RunUnderParentLockAsync<bool>(bc.Id, async () =>
        {
            // Re-leer el estado DURABLE del BC DENTRO del lock: un reintento concurrente que ya vinculo/emitio la
            // ND habra commiteado DebitNoteInvoiceId; sin este reload leeriamos el valor viejo (null) y
            // doble-emitiriamos. ReloadAsync refresca los escalares del BC; las navegaciones ya cargadas
            // (OriginatingInvoice/Tributes/Supplier) que usa el gating siguen disponibles.
            await _db.Entry(bc).ReloadAsync(ct);
            if (bc.DebitNoteInvoiceId.HasValue)
            {
                // Mirar el estado REAL de la ND vinculada DENTRO del lock. Una ND VIVA (con CAE, o Pending en vuelo,
                // o cualquier link no-rechazado) significa que otro reintento concurrente ya la resolvio -> NO
                // emitimos otra (idempotente, no-op). SOLO una ND RECHAZADA (Failed: Resultado="R" y sin CAE) es
                // segura de reemplazar: soltamos ese link muerto y seguimos a re-emitir una nueva (funcional B1).
                var linked = await _db.Invoices
                    .Where(i => i.Id == bc.DebitNoteInvoiceId.Value)
                    .Select(i => new { i.Resultado, i.CAE })
                    .FirstOrDefaultAsync(ct);
                var linkedHasCae = linked is not null && !string.IsNullOrWhiteSpace(linked.CAE);
                var linkedRejected = linked is not null
                    && string.Equals(linked.Resultado, "R", StringComparison.OrdinalIgnoreCase);

                if (linkedHasCae || !linkedRejected)
                {
                    _logger.LogInformation(
                        "RETRY-ND: BC {BcPublicId} ya quedo con ND viva vinculada (otro reintento concurrente). No-op.",
                        bc.PublicId);
                    return true;
                }

                // ND rechazada: soltamos el link muerto y limpiamos el error viejo de ARCA (A7). El comprobante
                // rechazado NUNCA obtuvo CAE (no existe documento fiscal), asi que emitir uno nuevo NO duplica nada.
                bc.DebitNoteInvoiceId = null;
                bc.DebitNoteStatus = DebitNoteStatus.NotApplicable;
                bc.DebitNoteArcaErrorMessage = null;
                await _db.SaveChangesAsync(ct);
                _logger.LogWarning(
                    "RETRY-ND: BC {BcPublicId} tenia una ND RECHAZADA vinculada. Se solto el link muerto para re-emitir.",
                    bc.PublicId);
            }

            // (a) ANTI DOBLE-EMISION: si un intento anterior alcanzo a CREAR la ND pero no a vincularla, la
            //     RE-VINCULAMOS (no emitimos otra). Misma deteccion que la bandeja de NDs huerfanas: buscamos una
            //     ND (tipos 2/7/12/52) sobre la MISMA factura original y reserva de esta cancelacion.
            //     EXCLUIMOS las RECHAZADAS (Resultado="R"): una ND rechazada esta muerta (nunca tuvo CAE), re-vincularla
            //     solo volveria a dejar el BC en Failed (bucle infinito del boton Reintentar). Ante una rechazada hay
            //     que emitir una NUEVA, no re-atarla. (El OR con null es defensivo: en Postgres "Resultado <> 'R'"
            //     excluiria las filas con Resultado NULL, que si son huerfanas validas a re-vincular.)
            //     ADR-044 (fix 2026-07-14): TAMBIEN excluimos cualquier ND que este siendo (o ya haya sido)
            //     anulada por una NC-anula-ND (fila en BookingCancellationDebitNoteAnnulments con Status
            //     Pending o Succeeded). Mismo choque que en la bandeja: sin este filtro, "Reintentar" podia
            //     re-atar una ND MUERTA y el paso volvia a mostrar "multa cobrada" sin salida.
            var orphanDebitNote = await _db.Invoices
                .Where(i => debitNoteTipos.Contains(i.TipoComprobante) &&
                            i.OriginalInvoiceId == bc.OriginatingInvoiceId &&
                            i.ReservaId == bc.ReservaId &&
                            (i.Resultado == null || i.Resultado != "R") &&
                            !_db.BookingCancellationDebitNoteAnnulments.Any(a =>
                                a.AnnulledDebitNoteInvoiceId == i.Id &&
                                a.Status != DebitNoteAnnulmentStatus.Failed))
                .OrderByDescending(i => i.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (orphanDebitNote is not null)
            {
                bc.DebitNoteInvoiceId = orphanDebitNote.Id;
                bc.DebitNoteStatus = ResolveDebitNoteStatusFromInvoice(orphanDebitNote);
                if (bc.DebitNoteStatus == DebitNoteStatus.Failed)
                {
                    var obs = orphanDebitNote.Observaciones ?? "ARCA rechazo la ND sin mensaje.";
                    bc.DebitNoteArcaErrorMessage = obs.Length > 1000 ? obs[..1000] : obs;
                }
                await _db.SaveChangesAsync(ct);
                _logger.LogWarning(
                    "RETRY-ND: BC {BcPublicId} tenia una ND creada sin vincular (Invoice {InvoiceId}). " +
                    "Re-vinculada, NO re-emitida. Nuevo DebitNoteStatus={Status}.",
                    bc.PublicId, orphanDebitNote.Id, bc.DebitNoteStatus);

                // (2026-07-03) Cierre INMEDIATO: si la ND re-vinculada YA estaba emitida (Issued), la pata de la
                // multa quedo resuelta en este mismo request -> re-evaluamos el cierre por no haber reembolso
                // pendiente del operador, sin esperar al barrido nocturno. Si quedo Pending/Failed sigue habiendo
                // multa por resolver (guard interno) y es no-op.
                if (bc.DebitNoteStatus == DebitNoteStatus.Issued)
                    await TryAutoCloseAfterOperatorPenaltyResolvedAsync(bc, ct);

                return true;
            }

            // (b) No hay ND previa: emitir de cero con el MISMO blindaje que confirm-penalty (si vuelve a fallar,
            //     revision manual + exito-con-aviso, nunca un 500 que la vuelva a trabar).
            // A3 (2026-07-08): pasamos el ACTOR del reintento para que la ND y su auditoria queden atribuidas a
            // quien apreto "Reintentar", no al confirmador de la anulacion.
            try
            {
                await TryEmitCancellationDebitNoteAsync(bc, ct, actorUserId: userId, actorUserName: userName);
            }
            catch (Exception emissionError) when (emissionError is not OperationCanceledException)
            {
                _logger.LogError(emissionError,
                    "metric:cancellation_debit_note_emission_failed | BcPublicId={BcPublicId} | " +
                    "reintento de emision fallido; se deja en revision manual.",
                    bc.PublicId);
                await MarkDebitNoteEmissionForManualReviewAsync(bc.Id, ct);
            }
            return true;
        }, ct);

        _logger.LogInformation(
            "metric:cancellation_debit_note_retry | BcPublicId={BcPublicId} By={UserId}",
            bc.PublicId, userId);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException("No se pudo completar la operación. Volvé a intentar.");
    }

    /// <inheritdoc />
    public async Task<BookingCancellationDto> CorrectPenaltyAsync(
        Guid publicId,
        decimal amount,
        string currency,
        string reason,
        string userId,
        string? userName,
        CancellationToken ct,
        bool userCanClassifyAgencyPenalty = false,
        // ADR-044 Fix B (2026-07-13): datos del tipo de cambio para convertir una multa declarada en una moneda
        // distinta a la de la factura (Caso A). Opcionales y con default null: una correccion en la misma moneda
        // de la factura (el caso de hoy) no los manda y sigue byte-identica. El service revalida server-side.
        decimal? exchangeRate = null,
        int? exchangeRateSource = null,
        DateTime? exchangeRateDate = null,
        string? exchangeRateJustification = null)
    {
        // Spec "el paso de multa vive en la ficha" (A4, 2026-07-08): corrige el MONTO + MONEDA de una multa YA
        // CONFIRMADA cuya Nota de Debito quedo TRABADA (revision manual por moneda distinta, o fallida) y todavia NO
        // tiene comprobante fiscal emitido con CAE. Es la version ATOMICA del circuito que hoy existe pieza por
        // pieza (cerrar sin multa -> reabrir -> volver a confirmar con la moneda nueva): deshace la imputacion vieja,
        // graba el monto/moneda nuevos, y re-encola la ND, TODO bajo el MISMO lock FOR UPDATE del padre.

        // === Validaciones de entrada (400). El monto debe ser > 0 y la moneda una ISO soportada (ARS/USD). ===
        if (amount <= 0m)
            throw new ArgumentException("El monto de la multa debe ser mayor a cero.", nameof(amount));
        if (!Monedas.EsSoportada(currency))
            throw new ArgumentException(
                "La moneda debe ser pesos (ARS) o dólares (USD).", nameof(currency));
        var normalizedCurrency = Monedas.Normalizar(currency);

        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
            throw new ArgumentException("Indicá un motivo para corregir la multa.", nameof(reason));

        var settings = await _settings.GetEntityAsync(ct);
        if (!settings.EnableCancellationDebitNote)
            // Voz de los avisos (regla del dueño 2026-07-08): quien llama a esta accion YA tiene el permiso
            // elevado de clasificar multas (userCanClassifyAgencyPenalty), asi que derivarlo a "administracion"
            // no tiene sentido — probablemente ES un administrador. El mensaje queda autocontenido.
            throw new InvalidOperationException(
                "No se pudo completar la corrección de la multa. Volvé a intentar más tarde.");

        // === El BC existe (404). Mismos Includes que ConfirmPenaltyAsync: el gating de la ND necesita la factura
        // origen + sus Tributos + Supplier. ===
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.OriginatingInvoice)
                .ThenInclude(i => i.Tributes)
            .Include(b => b.Supplier)
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // === Permiso elevado (B2 security 2026-07-08): correct-penalty re-emite el MISMO comprobante fiscal que
        // confirm-penalty/retry Y ademas cambia el monto/moneda, asi que lleva el MISMO gate en el eje de la decision
        // de plata: cancellations.classify_agency_penalty (o Admin), ya resuelto server-side por el controller. Lo
        // EXIGIMOS aca tambien (defensa en profundidad, no confiar en el frontend). ===
        if (!userCanClassifyAgencyPenalty)
            throw new BusinessInvariantViolationException(
                "No tenés permiso para corregir la multa del operador. Pedíselo a un administrador.",
                invariantCode: "INV-CORRECT-PERM");

        // === Solo aplica a una multa CONFIRMADA. Si aun esta Estimated (pendiente) o se cerro sin multa (Waived),
        // no hay nada que "corregir": el flujo correcto es confirmar / reabrir. ===
        if (bc.PenaltyStatus != PenaltyStatus.Confirmed)
            throw new BusinessInvariantViolationException(
                "Esta multa todavía no fue confirmada, así que no hay nada para corregir. Confirmala primero.",
                invariantCode: "INV-CORRECT-001");

        // === GUARD DURO: NO corregir si ya hay un comprobante fiscal emitido/en vuelo. Se evalua aca (fuera de
        // transaccion) para el 409 rapido, y SE RE-EVALUA IDENTICO dentro del lock (ver abajo, B1). ===
        await EnsureDebitNoteNotBlockingCorrectionAsync(bc, ct);

        var previousAmount = bc.PenaltyAmountAtEvent;
        var previousCurrency = ProjectPenaltyCurrencyToIsoOrNull(bc.PenaltyCurrencyAtEvent);

        // === Unidad de trabajo ATOMICA bajo el mismo lock FOR UPDATE del padre que los callbacks/retry: deshacer la
        // imputacion vieja de la multa, grabar el monto/moneda nuevos, resetear la huella de ND, y re-encolar. Serializa
        // contra un retry o un callback concurrente del mismo BC. En InMemory (tests) corre directo. ===
        await RunUnderParentLockAsync<bool>(bc.Id, async () =>
        {
            // Re-leer el estado DURABLE dentro del lock y RE-EVALUAR LOS TRES guards (B1 security 2026-07-08): entre
            // el guard de arriba (fuera de transaccion) y aca, un retry concurrente pudo dejar la ND en Pending (link
            // seteado, sin CAE aun). Si solo mirasemos "link con CAE", clobbeariamos ese link y re-encolariamos otra
            // ND -> cuando el job de la vieja saque CAE quedarian DOS ND con CAE por la MISMA multa (irreversible).
            // Un Failed si es seguro de clobbear (nunca va a sacar CAE); un Pending o un Issued NO -> rebotamos.
            await _db.Entry(bc).ReloadAsync(ct);

            // GUARD adicional (fix carrera 2026-07-08, hallado en review): el ReloadAsync de arriba refresca TODO
            // el estado del BC (incluido el xmin), asi que si entre el chequeo de PenaltyStatus de mas arriba (fuera
            // del lock) y este punto otro admin ejecuto "cerrar sin multa" (WaiveOperatorPenaltyAsync, que NO usa
            // este mismo lock por Id), el Reload trae PenaltyStatus=Waived sin que nada lo detecte: la concurrencia
            // optimista por xmin NO salva esto, porque el Reload la neutraliza (adopta el xmin nuevo como si fuera
            // nuestro punto de partida). Sin este re-chequeo, la correccion seguiria de largo, pisaria
            // PenaltyAmountAtEvent/PenaltyCurrencyAtEvent de una multa que ya NO existe, y
            // AllocateConfirmedPenaltyToLinesAsync le recortaria el RefundCap a una linea que el waive ya habia
            // restaurado -> queda Waived con el reembolso del operador recortado igual = descuadre contable
            // silencioso (viola RefundCap + PenaltyAmount == capBeforePenalty). Por eso volvemos a exigir
            // Confirmed ACA ADENTRO, con la MISMA invariante que el chequeo de afuera (mismo codigo => mismo 409
            // para el usuario, sin importar en que momento exacto se cruzo el waive).
            if (bc.PenaltyStatus != PenaltyStatus.Confirmed)
                throw new BusinessInvariantViolationException(
                    "Esta multa todavía no fue confirmada, así que no hay nada para corregir. Confirmala primero.",
                    invariantCode: "INV-CORRECT-001");

            await EnsureDebitNoteNotBlockingCorrectionAsync(bc, ct);

            // === ADR-044 Fix B (2026-07-13): resolver una multa declarada en una moneda DISTINTA a la de la
            //     factura ANTES de tocar nada (antes del Reverse). La conversion ocurre aca —al capturar—, NO en
            //     la emision: asi el guard de coherencia de la ND (que exige moneda declarada == moneda factura)
            //     queda intacto y es IMPOSIBLE emitir un comprobante en la escala de otra moneda. Un TC mal
            //     cargado, a lo sumo, da un monto en pesos equivocado (visible y re-corregible), nunca un
            //     comprobante en la escala equivocada. Ver docs/architecture/2026-07-13-fixb-multa-moneda-cruzada.
            var invoiceCurrencyIso = ResolveInvoiceCurrencyIso(bc.OriginatingInvoice?.MonId);
            var operatorLines = await _db.BookingCancellationLines
                .Where(l => l.BookingCancellationId == bc.Id && l.SupplierId == bc.SupplierId)
                .ToListAsync(ct);

            // La fuente del TC llega como int (contrato con el front); la validamos contra el enum antes de usarla,
            // para no persistir un entero fuera de rango como si fuera un ExchangeRateSource.
            ExchangeRateSource? parsedExchangeRateSource = null;
            if (exchangeRateSource.HasValue)
            {
                if (!Enum.IsDefined(typeof(ExchangeRateSource), exchangeRateSource.Value))
                    throw new ArgumentException(
                        "El origen del tipo de cambio no es válido.", nameof(exchangeRateSource));
                parsedExchangeRateSource = (ExchangeRateSource)exchangeRateSource.Value;
            }

            var conversion = ResolveDeclaredPenaltyConversion(
                operatorLines,
                declaredCurrencyIso: normalizedCurrency,
                invoiceCurrencyIso: invoiceCurrencyIso,
                declaredAmount: amount,
                exchangeRate: exchangeRate,
                exchangeRateSource: parsedExchangeRateSource,
                exchangeRateDate: exchangeRateDate,
                exchangeRateJustification: exchangeRateJustification);

            switch (conversion.Outcome)
            {
                case PenaltyConversionOutcome.NeedsExchangeRate:
                    // Caso A incompleto (falta fecha/TC valido o justificacion del TC manual). 400 con mensaje
                    // claro; NO se toca nada (el throw revierte la transaccion sin haber deshecho la imputacion vieja).
                    throw new ArgumentException(conversion.Reason, nameof(exchangeRate));
                case PenaltyConversionOutcome.NotConvertible:
                    // Caso B: la multa esta en otra moneda que las lineas del operador; un solo TC no resuelve a la
                    // vez el lado cliente y el lado operador. Se deja en revision manual: NO se re-graba monto/moneda,
                    // NO se estampa nada en las lineas, NO se crea cargo, NO se toca el RefundCap. Idempotente (el
                    // BC queda como estaba, el usuario puede reintentar). Deuda futura: retencion cross-currency real.
                    _logger.LogWarning(
                        "metric:cancellation_penalty_correction_not_convertible | BcPublicId={BcPublicId} | " +
                        "multa en moneda distinta a las lineas del operador: queda para revision manual.",
                        bc.PublicId);
                    throw new BusinessInvariantViolationException(
                        conversion.Reason!, invariantCode: "INV-CORRECT-CROSSCURRENCY");
            }

            // A partir de aca la multa esta EN LA MONEDA DE LA FACTURA: convertida (Caso A) o ya coincidia (mismo-moneda).
            var effectivePenaltyAmount = conversion.EffectiveAmount;
            var effectivePenaltyCurrency = conversion.EffectiveCurrencyIso;

            // (1) DESHACER la imputacion vieja de la multa a las lineas del operador (restaura los RefundCap). Igual
            //     que hace el cierre sin multa desde Confirmed. Es no-op para agency-owned (no hubo imputacion).
            //     ADR-044 T2 Addendum (menor 1): capturamos la foto de los cargos que se borran para el audit.
            var correctionDeletedCharges = new List<DeletedOperatorChargeSnapshot>();
            await ReverseConfirmedPenaltyFromLinesAsync(bc, ct, deletedChargesSink: correctionDeletedCharges);

            // (2) Grabar el monto/moneda EFECTIVOS (ya en la moneda de la factura). PenaltyAmountAtEvent alimenta la
            //     ND al cliente; PenaltyCurrencyAtEvent es la moneda que el gating compara contra la factura (con la
            //     conversion hecha aca, coinciden -> el gating deja emitir por el numero correcto).
            bc.PenaltyAmountAtEvent = effectivePenaltyAmount;
            bc.PenaltyCurrencyAtEvent = effectivePenaltyCurrency;
            bc.PenaltyConfirmedByUserId = userId;
            bc.PenaltyConfirmedByUserName = userName;
            bc.PenaltyConfirmedAt = DateTime.UtcNow;

            // (2-bis) ADR-044 Fix B: en Caso A guardamos el ORIGINAL declarado + el TC usado en columnas
            //     ESTRUCTURADAS (no solo en el audit), para poder reconstruir el origen (ej. USD 200) sin leer
            //     blobs. En mismo-moneda estos vienen null y limpian cualquier conversion previa del BC.
            bc.DeclaredPenaltyOriginalAmount = conversion.DeclaredOriginalAmount;
            bc.DeclaredPenaltyOriginalCurrency = conversion.DeclaredOriginalCurrencyIso;
            bc.PenaltyConversionExchangeRate = conversion.ExchangeRate;
            bc.PenaltyConversionExchangeRateSource = conversion.ExchangeRateSource;
            bc.PenaltyConversionExchangeRateAt = conversion.ExchangeRateAt;
            bc.PenaltyConversionExchangeRateJustification = conversion.ExchangeRateJustification;

            // (3) Registrar la moneda en las lineas + re-imputar la multa nueva a los RefundCap (por moneda, nunca
            //     cruzado). Se usa la moneda EFECTIVA: en Caso A las lineas ya estan en esa moneda -> coherente.
            await PersistPenaltyCurrencyOnLinesAsync(bc, effectivePenaltyCurrency, ct);
            await AllocateConfirmedPenaltyToLinesAsync(
                bc, effectivePenaltyAmount, effectivePenaltyCurrency, ct, userId: userId, userName: userName);

            // (4) Resetear la huella de la ND trabada para que TryEmit la re-encole desde cero: soltamos el link a
            //     cualquier ND fallida vieja (su monto ya no aplica), volvemos a NotApplicable y limpiamos el error
            //     viejo de ARCA (A7). NO re-vinculamos huerfanos: el monto cambio, hay que emitir una ND nueva.
            bc.DebitNoteInvoiceId = null;
            bc.DebitNoteStatus = DebitNoteStatus.NotApplicable;
            bc.DebitNoteArcaErrorMessage = null;

            // (5) Auditoria PROPIA de la correccion (menor review 2026-07-08): accion dedicada OperatorPenaltyCorrected
            //     (no la de emision normal de ND) para que el contador la pueda filtrar. STAGED (no LogBusinessEventAsync
            //     que hace su propio SaveChanges): asi la auditoria entra en la MISMA SaveChanges que la mutacion ->
            //     atomico (o commitea todo, o nada; nunca audit-sin-efecto). Antes/despues de monto y moneda + motivo.
            _auditService.StageBusinessEvent(
                action: AuditActions.OperatorPenaltyCorrected,
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    bc.PublicId,
                    reservaPublicId = bc.Reserva?.PublicId,
                    action = "operator-penalty-corrected",
                    reason = trimmedReason,
                    previousAmount,
                    previousCurrency,
                    // Lo que finalmente quedo grabado (ya en la moneda de la factura). En Caso A esto es el CONVERTIDO.
                    newAmount = effectivePenaltyAmount,
                    newCurrency = effectivePenaltyCurrency,
                    // ADR-044 Fix B: lo que el usuario DECLARO + el TC usado, cuando hubo conversion cross-currency
                    // (Caso A). Todo null cuando la multa ya estaba en la moneda de la factura (mismo-moneda).
                    declaredOriginalAmount = conversion.DeclaredOriginalAmount,
                    declaredOriginalCurrency = conversion.DeclaredOriginalCurrencyIso,
                    conversionExchangeRate = conversion.ExchangeRate,
                    conversionExchangeRateSource = conversion.ExchangeRateSource?.ToString(),
                    conversionExchangeRateDate = conversion.ExchangeRateAt,
                    conversionExchangeRateJustification = conversion.ExchangeRateJustification,
                    // Menor 1 (ADR-044 T2): foto de los cargos borrados por la correccion (autocontencion del evento).
                    deletedOperatorCharges = correctionDeletedCharges,
                }),
                userId: userId,
                userName: userName);

            // Mutacion + auditoria staged en una UNICA SaveChanges (atomica).
            await _db.SaveChangesAsync(ct);

            // (5-bis, 2026-07-15) La correccion borro TODOS los cargos del operador (paso 1) y re-creo solo el
            // automatico Retenida (paso 3): si habia un cargo FACTURADO APARTE, desaparecio. Ese cargo sumaba al
            // saldo OFICIAL del operador (SupplierBalanceByCurrency), asi que recalculamos el saldo para que no
            // quede inflado. Corre DESPUES del SaveChanges de arriba (PersistAsync LEE los cargos flusheados) y
            // hace su propio SaveChanges. Recalcula el operador principal del BC (el que corrige esta accion).
            await TravelApi.Infrastructure.Reservations.SupplierDebtPersister.PersistAsync(_db, bc.SupplierId, ct);
            await _db.SaveChangesAsync(ct);

            // (6) Re-encolar la ND con la moneda NUEVA. Si ahora es coherente con la factura -> Pending; si sigue
            //     trabada (u otro fallo) -> revision manual + exito-con-aviso (nunca un 500 que la vuelva a trabar).
            //     Atribuida al actor real de la correccion (A3).
            try
            {
                await TryEmitCancellationDebitNoteAsync(bc, ct, actorUserId: userId, actorUserName: userName);
            }
            catch (Exception emissionError) when (emissionError is not OperationCanceledException)
            {
                _logger.LogError(emissionError,
                    "metric:cancellation_debit_note_emission_failed | BcPublicId={BcPublicId} | " +
                    "correccion de multa: la re-emision fallo; se deja en revision manual.",
                    bc.PublicId);
                await MarkDebitNoteEmissionForManualReviewAsync(bc.Id, ct);
            }
            return true;
        }, ct);

        _logger.LogInformation(
            "metric:cancellation_debit_note_corrected | BcPublicId={BcPublicId} By={UserId} Amount={Amount} Currency={Currency}",
            bc.PublicId, userId, amount, normalizedCurrency);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException("No se pudo completar la operación. Volvé a intentar.");
    }

    /// <summary>
    /// A4 (2026-07-08): GUARD DURO compartido de <see cref="CorrectPenaltyAsync"/>. Rechaza corregir la multa si su
    /// Nota de Debito ya esta EMITIDA o EN VUELO. Se llama DOS veces: fuera de transaccion (409 rapido) y —critico
    /// (B1 security)— DENTRO del lock tras <c>ReloadAsync</c>, para no clobbear una ND que un retry concurrente dejo
    /// Pending entre medio (evita dos ND con CAE por la misma multa). Un Failed NO bloquea (nunca sacara CAE, es
    /// seguro re-emitir); un Issued o un Pending SI.
    /// </summary>
    private async Task EnsureDebitNoteNotBlockingCorrectionAsync(BookingCancellation bc, CancellationToken ct)
    {
        if (bc.DebitNoteStatus == DebitNoteStatus.Issued)
            throw new BusinessInvariantViolationException(
                "La multa ya tiene una Nota de Débito emitida. Para cambiarla hay que anular ese comprobante primero.",
                invariantCode: "INV-CORRECT-002");
        if (bc.DebitNoteStatus == DebitNoteStatus.Pending)
            throw new BusinessInvariantViolationException(
                "La Nota de Débito de esta multa se está emitiendo. Esperá a que termine para poder corregirla.",
                invariantCode: "INV-CORRECT-003");
        // Por robustez: una ND vinculada cuyo Invoice YA tenga CAE (aunque el DebitNoteStatus escalar no lo refleje).
        if (bc.DebitNoteInvoiceId.HasValue)
        {
            var linkedHasCae = await _db.Invoices
                .Where(i => i.Id == bc.DebitNoteInvoiceId.Value)
                .AnyAsync(i => i.CAE != null && i.CAE != "", ct);
            if (linkedHasCae)
                throw new BusinessInvariantViolationException(
                    "La multa ya tiene una Nota de Débito emitida. Para cambiarla hay que anular ese comprobante primero.",
                    invariantCode: "INV-CORRECT-002");
        }
    }

    // =========================================================================================================
    // ADR-044 "Deshacer una multa ya emitida" (2026-07-14): la ND de la multa salio con CAE y estaba MAL
    // (monto/moneda equivocada, o no correspondia). UndoIssuedDebitNoteAsync emite una NC ESPEJO de esa ND
    // (OriginalInvoiceId=la ND, nunca la factura) que la anula fiscalmente. Molde de CorrectPenaltyAsync.
    // =========================================================================================================

    /// <inheritdoc />
    public async Task<BookingCancellationDto> UndoIssuedDebitNoteAsync(
        Guid publicId,
        string reason,
        string userId,
        string? userName,
        CancellationToken ct,
        bool requesterIsAdmin = false)
    {
        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
            throw new ArgumentException("Indicá un motivo para deshacer la multa.", nameof(reason));

        var settings = await _settings.GetEntityAsync(ct);
        if (!settings.EnableCancellationDebitNote)
            // Quien llama YA es Admin, asi que el mensaje queda autocontenido (no derivamos a "administracion").
            throw new InvalidOperationException(
                "No se pudo completar el deshacer de la multa. Volvé a intentar más tarde.");

        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.OriginatingInvoice)
            .Include(b => b.Supplier)
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // SOLO ADMINISTRADORES (spec UX firmada, gate B1 2026-07-14): deshacer un comprobante fiscal ya emitido
        // con CAE es la acción más sensible del paso de multa. A diferencia de confirm/correct/retry (permiso
        // classify), esto exige rol Admin. Defensa en profundidad: el controller ya lo resolvió, acá se re-exige.
        if (!requesterIsAdmin)
            throw new BusinessInvariantViolationException(
                "No tenés permiso para deshacer la multa del operador. Pedíselo a un administrador.",
                invariantCode: "INV-UNDO-PERM");

        // Guard duro fuera de transaccion (409 rapido); SE RE-EVALUA IDENTICO dentro del lock (abajo).
        await EnsureUndoDebitNoteAllowedAsync(bc, ct);

        // Unidad de trabajo ATOMICA bajo el MISMO lock FOR UPDATE del padre que confirm/correct/retry: arma la
        // NC-anula-ND (via el pipeline normal de InvoiceService) y crea la fila hija, todo o nada.
        await RunUnderParentLockAsync<bool>(bc.Id, async () =>
        {
            await _db.Entry(bc).ReloadAsync(ct);
            await EnsureUndoDebitNoteAllowedAsync(bc, ct);

            var nd = await _db.Invoices
                .Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.Id == bc.DebitNoteInvoiceId!.Value, ct)
                ?? throw new InvalidOperationException(
                    "No se pudo completar el deshacer de la multa. Volvé a intentar más tarde.");

            // Espejo 1:1 de los renglones de la ND (regla dura #3: la NC reversa CADA linea con su tipificacion).
            var items = nd.Items
                .Select(item => new InvoiceItemDto
                {
                    Description = item.Description,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    Total = item.Total,
                    AlicuotaIvaId = item.AlicuotaIvaId,
                })
                .ToList();

            var ncRequest = new CreateInvoiceRequest
            {
                ReservaId = bc.Reserva.PublicId.ToString(),
                Concepto = 3, // Productos y Servicios (mismo default que la NC total / la ND).
                OriginalInvoiceId = nd.PublicId.ToString(), // LA ND, nunca la factura (regla dura #1).
                IsCreditNote = true,
                IsDebitNote = false,
                Items = items,
                Tributes = new List<InvoiceTributeDto>(),
                // Regla dura #3/#4: la NC hereda el TC CONGELADO de la ND (nunca recotiza). AfipService deriva la
                // letra sola del tipo de la ND asociada (12->13, 2->3, 7->8) — no se hardcodea aca (regla #2).
                MonId = nd.MonId,
                MonCotiz = nd.MonCotiz,
                ExchangeRateSource = nd.ExchangeRateSource,
                ExchangeRateFetchedAt = nd.ExchangeRateFetchedAt,
                ExchangeRateJustification = string.IsNullOrWhiteSpace(nd.ExchangeRateJustification)
                    ? nd.ExchangeRateJustification
                    : $"Anulación de Nota de Débito por multa mal emitida — TC heredado: {nd.ExchangeRateJustification}",
            };

            var ncDto = await _invoiceService.CreateAsync(ncRequest, userId, userName, ct);

            var ncId = await _db.Invoices
                .Where(i => i.PublicId == ncDto.PublicId)
                .Select(i => (int?)i.Id)
                .FirstOrDefaultAsync(ct);
            if (ncId is null)
            {
                // Misma defensa que TryEmitCancellationDebitNoteAsync: la NC existe pero no se pudo resolver su
                // Id interno. No dejamos la fila hija sin comprobante: mejor error claro que un dato a medias.
                throw new InvalidOperationException(
                    "No se pudo completar el deshacer de la multa. Volvé a intentar más tarde.");
            }

            var annulmentCurrency = ProjectPenaltyCurrencyToIsoOrNull(bc.PenaltyCurrencyAtEvent)
                ?? ResolveInvoiceCurrencyIso(nd.MonId);

            var annulment = new BookingCancellationDebitNoteAnnulment
            {
                BookingCancellationId = bc.Id,
                AnnulledDebitNoteInvoiceId = nd.Id,
                AnnulmentCreditNoteInvoiceId = ncId.Value,
                Status = DebitNoteAnnulmentStatus.Pending,
                Reason = trimmedReason,
                Amount = nd.ImporteTotal,
                Currency = annulmentCurrency,
                ExchangeRate = nd.MonCotiz,
                RequestedByUserId = userId,
                RequestedByUserName = userName,
                RequestedAt = DateTime.UtcNow,
            };
            _db.Set<BookingCancellationDebitNoteAnnulment>().Add(annulment);

            // Auditoria STAGED (misma SaveChanges que la mutacion): motivo, comprobantes vinculados, importe+
            // moneda+TC, estado previo del paso (regla dura #14).
            _auditService.StageBusinessEvent(
                action: AuditActions.OperatorPenaltyDebitNoteUndoRequested,
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    bc.PublicId,
                    reservaPublicId = bc.Reserva?.PublicId,
                    action = "operator-penalty-debit-note-undo-requested",
                    reason = trimmedReason,
                    undoneDebitNoteInvoiceId = nd.Id,
                    undoneDebitNoteCbteTipo = nd.TipoComprobante,
                    annulmentCreditNoteInvoicePublicId = ncDto.PublicId,
                    amount = nd.ImporteTotal,
                    currency = annulmentCurrency,
                    exchangeRate = nd.MonCotiz,
                    previousDebitNoteStatus = bc.DebitNoteStatus.ToString(),
                }),
                userId: userId,
                userName: userName);

            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);

        _logger.LogInformation(
            "metric:cancellation_debit_note_undo_requested | BcPublicId={BcPublicId} By={UserId}",
            bc.PublicId, userId);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException("No se pudo completar la operación. Volvé a intentar.");
    }

    /// <summary>
    /// GUARD DURO compartido de <see cref="UndoIssuedDebitNoteAsync"/> (reglas duras #9/#10/tributos/#B2 de la
    /// spec; la regla #11 -factura original anulada del todo bloquea el deshacer- se ELIMINO el 2026-07-14 por
    /// ser un bug: bloqueaba el caso normal, ver comentario mas abajo).
    /// Se llama DOS veces: fuera de transaccion (409 rapido) y DENTRO del lock tras <c>ReloadAsync</c> (anti
    /// carrera con un retry/callback/correct concurrente), mismo patron que <c>EnsureDebitNoteNotBlockingCorrectionAsync</c>.
    /// </summary>
    private async Task EnsureUndoDebitNoteAllowedAsync(BookingCancellation bc, CancellationToken ct)
    {
        // Regla dura #9: la multa debe estar Confirmed, con ND vinculada, Issued Y con CAE real. Una ND en
        // PENDING (sin CAE) no tiene nada fiscal que anular por este camino.
        if (bc.PenaltyStatus != PenaltyStatus.Confirmed
            || bc.DebitNoteStatus != DebitNoteStatus.Issued
            || !bc.DebitNoteInvoiceId.HasValue)
        {
            // Voz de la obra (data-exposure 2026-07-14): "comprobante", no "Nota de Débito".
            throw new BusinessInvariantViolationException(
                "Esta multa no tiene un comprobante emitido para deshacer.",
                invariantCode: "INV-UNDO-001");
        }

        var ndSnapshot = await _db.Invoices
            .AsNoTracking()
            .Where(i => i.Id == bc.DebitNoteInvoiceId.Value)
            .Select(i => new { i.Resultado, i.CAE })
            .FirstOrDefaultAsync(ct);
        bool ndHasCae = ndSnapshot is not null
            && string.Equals(ndSnapshot.Resultado, "A", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(ndSnapshot.CAE);
        if (!ndHasCae)
        {
            // Voz de la obra: quien lee YA es Admin, así que no se deriva a "administración".
            throw new BusinessInvariantViolationException(
                "El comprobante de esta multa todavía se está emitiendo. Esperá a que termine.",
                invariantCode: "INV-UNDO-001");
        }

        // Regla dura #10: no anular dos veces el mismo comprobante (ya hay una anulacion viva o consumada para
        // ESTA ND). El indice unico filtrado de la tabla ya lo impide a nivel base; esto da el 409 legible.
        var hasLiveAnnulment = await _db.Set<BookingCancellationDebitNoteAnnulment>()
            .AnyAsync(a => a.AnnulledDebitNoteInvoiceId == bc.DebitNoteInvoiceId.Value
                        && a.Status != DebitNoteAnnulmentStatus.Failed, ct);
        if (hasLiveAnnulment)
        {
            throw new BusinessInvariantViolationException(
                "Esta multa ya se está deshaciendo (o ya se deshizo). No se puede repetir.",
                invariantCode: "INV-UNDO-002");
        }

        // Regla dura #11 ELIMINADA (2026-07-14, bug confirmado en prod con F-2026-1043): la regla original
        // bloqueaba deshacer la ND cuando la factura de venta original ya estaba anulada del todo
        // (AnnulmentStatus.Succeeded). El problema es que ESE es el caso normal, no la excepcion: cuando se
        // anula una reserva completa, primero se emite la NC total de la factura de venta (que la deja
        // Succeeded) y RECIEN DESPUES puede existir una multa de operador para esa cancelacion. O sea que
        // cualquier multa que exista siempre cuelga de una factura ya anulada del todo -> la regla #11 tiraba
        // "revision manual" en el 100% de los casos reales y el boton de deshacer nunca funcionaba.
        // Por que es seguro sacarla: la NC que deshace la multa apunta a la ND (OriginatingInvoiceId = la ND),
        // nunca a la factura de venta original, y hereda la moneda y el tipo de cambio congelados de la ND. El
        // estado de la factura de venta no afecta en nada esta operacion. Ademas, emitir una ND sobre una
        // factura ya anulada es el flujo normal de las multas post-anulacion (ADR-013), asi que deshacer esa
        // misma ND tiene que poder hacerse aunque la factura de venta este Succeeded.

        // TRIBUTOS (guard defensivo barato, corrección post-gate 2026-07-14): la NC-anula-ND espeja los renglones
        // de la ND pero NO sus tributos (IIBB provinciales). El gating de EMISIÓN automática de la ND ya manda a
        // revisión manual toda factura CON tributos (EvaluateDebitNoteGating), así que una ND emitida
        // automáticamente NUNCA lleva tributos -> por ese camino esto es INALCANZABLE. Pero si una ND con
        // tributos existiera por un camino manual/legacy, deshacerla acá dejaría los tributos sin reversar (fuga
        // fiscal silenciosa). Guard cheap: ND con tributos -> revisión manual, nunca auto-deshacer.
        var ndHasTributes = await _db.Set<InvoiceTribute>()
            .AnyAsync(t => t.InvoiceId == bc.DebitNoteInvoiceId.Value, ct);
        if (ndHasTributes)
        {
            throw new BusinessInvariantViolationException(
                "El comprobante de esta multa tiene impuestos asociados. Este caso lo tiene que revisar una persona.",
                invariantCode: "INV-UNDO-MANUAL");
        }

        // B2 (guard conservador, ambiguedad irresoluble): la ND mezcla cargos de 2+ operadores Y hay al menos
        // una linea ManualReview (ND complementaria de OTRO operador) cuyo vinculo con esta ND no se puede
        // determinar (cargos legacy sin TargetInvoiceId). Nunca resetear a ciegas: se rutea a revision manual.
        if (await HasUnresolvableMultiOperatorAmbiguityAsync(bc, ct))
        {
            throw new BusinessInvariantViolationException(
                "Esta multa la tiene que revisar una persona antes de poder deshacerla (hay más de un operador en juego).",
                invariantCode: "INV-UNDO-MULTIOP");
        }
    }

    /// <summary>
    /// B2 (ADR-044 "Deshacer una multa ya emitida"): true si NO se puede determinar con seguridad que resetear
    /// las lineas de esta ND NO va a pisar la marca <see cref="DebitNoteStatus.ManualReview"/> de una linea de
    /// OTRO operador. Mono-operador (el 99%+ de los casos hoy) siempre da false.
    /// </summary>
    private async Task<bool> HasUnresolvableMultiOperatorAmbiguityAsync(BookingCancellation bc, CancellationToken ct)
    {
        if (!bc.DebitNoteInvoiceId.HasValue) return false;
        var ndId = bc.DebitNoteInvoiceId.Value;

        var supplierIdsInNd = await _db.BookingCancellationLineOperatorCharges
            .Where(c => c.BookingCancellationLine.BookingCancellationId == bc.Id
                     && c.Kind != OperatorChargeKind.Withholding
                     && c.TargetInvoiceId == ndId)
            .Select(c => c.BookingCancellationLine.SupplierId)
            .Distinct()
            .ToListAsync(ct);

        if (supplierIdsInNd.Count < 2)
            return false; // mono-operador (o T3a legado sin TargetInvoiceId poblado): sin ambiguedad, B2 reusa el fallback del BC unico.

        // ¿Hay una linea ManualReview (multa complementaria pendiente de OTRO operador) cuyos cargos NO tienen
        // TargetInvoiceId? Sin ese dato no se puede afirmar mecanicamente que quedo AFUERA de esta ND.
        return await _db.BookingCancellationLines
            .Where(l => l.BookingCancellationId == bc.Id && l.DebitNoteStatus == DebitNoteStatus.ManualReview)
            .AnyAsync(l => l.OperatorCharges.Any(
                c => c.Kind != OperatorChargeKind.Withholding && c.TargetInvoiceId == null), ct);
    }

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): ¿hay un evento de deshacer EN VUELO (Pending) o el
    /// ULTIMO intento quedo Failed, para la ND vinculada de este BC? Query chica a la tabla hija, compartida por
    /// <see cref="GetOperatorPenaltySituationAsync"/> y <see cref="GetOperatorPenaltySituationsAsync"/> para que
    /// ambos lectores deriven el mismo paso.
    /// </summary>
    private async Task<(bool HasPending, bool HasFailed)> GetDebitNoteAnnulmentFlagsAsync(
        int? debitNoteInvoiceId, CancellationToken ct)
    {
        if (!debitNoteInvoiceId.HasValue)
            return (false, false);

        var hasPending = await _db.Set<BookingCancellationDebitNoteAnnulment>()
            .AnyAsync(a => a.AnnulledDebitNoteInvoiceId == debitNoteInvoiceId.Value
                        && a.Status == DebitNoteAnnulmentStatus.Pending, ct);
        if (hasPending)
            return (true, false);

        // Sin Pending: miramos si el ULTIMO intento (por fecha) quedo Failed. Un Failed viejo con un Pending mas
        // nuevo ya cayo en la rama de arriba; esto cubre el caso "el ultimo intento fallo y nadie reintento aun".
        var lastStatus = await _db.Set<BookingCancellationDebitNoteAnnulment>()
            .Where(a => a.AnnulledDebitNoteInvoiceId == debitNoteInvoiceId.Value)
            .OrderByDescending(a => a.RequestedAt)
            .Select(a => (DebitNoteAnnulmentStatus?)a.Status)
            .FirstOrDefaultAsync(ct);

        return (false, lastStatus == DebitNoteAnnulmentStatus.Failed);
    }

    /// <summary>
    /// ADR-013/Waive (2026-07-08): true si la Nota de Debito de la multa esta "EN JUEGO" (ya vinculada a una
    /// factura, encolada esperando CAE, o emitida con CAE). Mientras este en juego, "cerrar sin multa" no puede
    /// tocarla: haria falta anular ese comprobante fiscal desde administracion primero.
    ///
    /// <para>Se extrae a un metodo puro porque la usan DOS lugares que TIENEN que coincidir: la precondicion real
    /// de <see cref="WaiveOperatorPenaltyAsync"/> (bloquea la accion con 409) y el read-model
    /// <see cref="GetOperatorPenaltySituationAsync"/> (decide si la ficha OFRECE el boton "cerrar sin multa" via
    /// <c>CanWaive</c>). Si divergieran, la ficha mostraria un boton que despues rebota 409 al tocarlo.</para>
    /// </summary>
    private static bool IsOperatorPenaltyDebitNoteInPlay(bool hasDebitNoteInvoiceId, DebitNoteStatus debitNoteStatus) =>
        hasDebitNoteInvoiceId ||
        debitNoteStatus == DebitNoteStatus.Pending ||
        debitNoteStatus == DebitNoteStatus.Issued;

    /// <inheritdoc />
    public async Task<BookingCancellationDto> WaiveOperatorPenaltyAsync(
        Guid publicId,
        string reason,
        string userId,
        string? userName,
        CancellationToken ct,
        bool userCanClassifyAgencyPenalty = false,
        bool requesterIsAdmin = false,
        Guid? supplierPublicId = null)
    {
        // Fase A (2026-06-28): cierre SIN multa. Es la rama ALTERNATIVA a ConfirmPenaltyAsync para el caso mas
        // comun del negocio: el operador no cobro ninguna multa y devuelve todo. Reusa las precondiciones de
        // ESTADO de confirmar (flag / BC existe / permiso / post-NC con CAE / idempotencia) pero NO hace nada
        // fiscal: no emite Nota de Debito, no inventa un monto. Solo deja la penalidad en su estado terminal
        // "sin multa" (Waived) para que el boton pendiente se limpie, y registra el rastro obligatorio.

        // El motivo es obligatorio (lo valida tambien el DataAnnotation del request, esto es defensivo).
        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
            throw new ArgumentException(
                "Indica un motivo para confirmar el cierre sin multa del operador.", nameof(reason));

        var settings = await _settings.GetEntityAsync(ct);

        // === Precondicion 1: flag maestro. Con OFF, todo el subsistema de penalidad del operador esta inerte
        // (HasPendingOperatorPenalty nunca da true), asi que el boton no se ofrece; rechazamos por simetria con
        // ConfirmPenaltyAsync para no mutar estado de un subsistema deshabilitado. ===
        if (!settings.EnableCancellationDebitNote)
            throw new InvalidOperationException(
                "La gestión de penalidades de cancelación no está disponible en este momento. " +
                "Consultá con administración.");

        // === Precondicion 2: el BC existe (404). Cargamos la Reserva para el detalle del audit.
        // ADR-044 T1 (2026-07-10): sumamos el Supplier del padre (para el fallback legacy de ResolveTargetSupplierId)
        // + Lines con su Supplier para poder resolver a que operador corresponde este cierre sin multa cuando la
        // cancelacion tiene servicios de mas de uno (ADR-025). ===
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.Supplier)
            .Include(b => b.Lines)
                .ThenInclude(l => l.Supplier)
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // === Precondicion 3: permiso elevado (mismo que confirmar la penalidad). Resolver la pata fiscal de la
        // penalidad — con o sin multa — es una accion sensible: la EXIGIMOS (no degrada). ===
        if (!userCanClassifyAgencyPenalty)
            throw new BusinessInvariantViolationException(
                "No tenés permiso para registrar el cierre sin multa del operador. Pedíselo a un administrador.",
                invariantCode: "INV-WAIVE-PERM");

        // === Precondicion 4: estado post-NC con CAE. La pata de la penalidad solo se resuelve despues de que la
        // NC total al cliente ya tiene CAE (mismo gate que ConfirmPenaltyAsync). ===
        if (!PostCreditNoteStatuses.Contains(bc.Status) || bc.CreditNoteInvoiceId is null)
            throw new BusinessInvariantViolationException(
                "Todavía no se puede cerrar sin multa: la nota de crédito al cliente aún no está " +
                "confirmada por la AFIP.",
                invariantCode: "INV-WAIVE-001");

        // === ADR-044 T1 (2026-07-10): a que OPERADOR corresponde este cierre sin multa. Mismo helper y misma
        // retrocompatibilidad que ConfirmPenaltyAsync (ver ResolveTargetSupplierId). ===
        var targetSupplierId = ResolveTargetSupplierId(bc, supplierPublicId);
        var isPrimaryOperator = targetSupplierId == bc.SupplierId;
        var targetLines = bc.Lines.Where(l => l.SupplierId == targetSupplierId).ToList();

        // === Precondicion 5: idempotencia. Si la penalidad de ESTE operador YA se cerro sin multa, rebota 409
        // (no-op seguro). Waive doble => 409. Para el operador PRINCIPAL el snapshot del BC padre sigue siendo la
        // fuente de verdad (byte-identico a antes de esta tanda); para uno SECUNDARIO, ese snapshot describe al
        // principal, asi que miramos SUS PROPIAS lineas ("cada linea confirma/cierra la suya"). ===
        var alreadyWaivedForTarget = isPrimaryOperator
            ? bc.PenaltyStatus == PenaltyStatus.Waived
            : targetLines.Any(l => l.PenaltyStatus == PenaltyStatus.Waived);
        if (alreadyWaivedForTarget)
            throw new BusinessInvariantViolationException(
                "La multa del operador de esta cancelación ya fue cerrada sin multa. No se vuelve a procesar.",
                invariantCode: "INV-WAIVE-003");

        // === Precondicion 6: comprobante fiscal de la multa EN JUEGO. Si ya hay una Nota de Debito vinculada, o
        // encolada (Pending), o con CAE (Issued), NO se puede "cerrar sin multa" por este boton: habria que anular
        // ese comprobante desde administracion primero. Cerrar sin multa dejaria una ND viva sin su multa =
        // incoherencia fiscal. Esta MISMA condicion la reusa el read-model GetOperatorPenaltySituationAsync (via
        // IsOperatorPenaltyDebitNoteInPlay) para decidir si la ficha OFRECE el boton "cerrar sin multa": si
        // divergieran, la ficha mostraria un boton que despues rebota 409 aca.
        //
        // ADR-044 T1 (2026-07-10): este chequeo aplica SOLO al operador PRINCIPAL (el UNICO slot de ND que
        // existe hoy a nivel BC describe justamente a ese operador). Para uno SECUNDARIO ese documento (real o en
        // curso) es de OTRO operador: bloquear su cierre sin multa por un comprobante ajeno dejaria a un
        // secundario sin forma de cerrar su pata si el principal ya emitio su ND — rompiendo "cada operador se
        // resuelve por separado" (mismo criterio que la Precondicion 6 de ConfirmPenaltyAsync). ===
        var debitNoteInPlay = isPrimaryOperator
            && IsOperatorPenaltyDebitNoteInPlay(bc.DebitNoteInvoiceId.HasValue, bc.DebitNoteStatus);
        if (debitNoteInPlay)
            throw new BusinessInvariantViolationException(
                "La multa tiene una nota de débito emitida o en emisión; se resuelve desde administración.",
                invariantCode: "INV-WAIVE-004");

        // === Precondicion 7: cierre sin multa DESDE una multa ya confirmada. Es la rama que apaga el cartel de
        // "multa fantasma": la multa se confirmo (Confirmed) pero su ND nunca llego a existir (NotApplicable / Failed
        // / ManualReview, sin factura vinculada — garantizado por la Precondicion 6). Decidir NO cobrarla es una
        // accion sensible (revierte una confirmacion y restaura topes de reembolso), asi que la EXIGIMOS a Admin,
        // igual que reabrir un cierre (RevertWaivedOperatorPenaltyAsync). El cierre desde el estado pendiente normal
        // (Estimated) NO requiere Admin. Mismo criterio principal-vs-secundario que la Precondicion 5. ===
        var waivingFromConfirmed = isPrimaryOperator
            ? bc.PenaltyStatus == PenaltyStatus.Confirmed
            : targetLines.Any(l => l.PenaltyStatus == PenaltyStatus.Confirmed);
        if (waivingFromConfirmed && !requesterIsAdmin)
            throw new BusinessInvariantViolationException(
                "Cerrar sin multa una penalidad ya confirmada requiere rol de Administrador.",
                invariantCode: "INV-WAIVE-005");

        // === Si venimos de una multa confirmada, DESHACEMOS la imputacion de la multa a las lineas del operador
        // RESUELTO ANTES de cambiar el estado. Al confirmar (AllocateConfirmedPenaltyToLinesAsync) en concepto
        // pass-through se REDUJO el RefundCap de esas lineas (invariante RefundCap + PenaltyAmount ==
        // capBeforePenalty). Si no lo restauramos, "Reembolsos a cobrar" queda subestimado para siempre. La lista de
        // caps restaurados va al audit. Para el estado pendiente (Estimated) esto es no-op (no hubo imputacion). ===
        // ADR-044 T2 Addendum (menor 1): capturamos la foto de los cargos que la reversa borra, para el audit.
        var waiveDeletedCharges = new List<DeletedOperatorChargeSnapshot>();
        var restoredCaps = waivingFromConfirmed
            ? await ReverseConfirmedPenaltyFromLinesAsync(bc, ct, targetSupplierId, deletedChargesSink: waiveDeletedCharges)
            : new List<PenaltyCapRestore>();

        // ADR-044 T1: marcar las lineas de ESTE operador como Waived (espejo de como Allocate las marca
        // Confirmed). El Reverse de arriba ya las dejo en Estimated; este es el ultimo paso del estado terminal.
        foreach (var line in targetLines)
            line.PenaltyStatus = PenaltyStatus.Waived;

        // === Snapshot de la huella de ND ANTES de limpiarla, para el audit. Guardar el estado de ND previo (p.ej.
        // Failed / ManualReview) y el mensaje de error de ARCA que se borra deja la historia AUTOCONTENIDA en el evento:
        // se reconstruye "de que estado veniamos" sin tener que correlacionar timestamps con otros registros. ===
        var previousDebitNoteStatus = bc.DebitNoteStatus.ToString();
        var clearedArcaErrorMessage = bc.DebitNoteArcaErrorMessage;

        // === Aplicar el cierre sin multa. Estado terminal propio (Waived) + monto 0, y limpieza de cualquier huella
        // de ND que hubiera quedado (Failed/ManualReview + su mensaje de error): la cara fiscal al cliente por la
        // multa pasa a cero, sin comprobante. NO tocamos el estado de la reserva: cierra cuando llega el reembolso
        // completo (los post-pasos de abajo reevaluan el auto-cierre).
        //
        // ADR-044 T1 (2026-07-10): estos campos son el snapshot BC-padre, que hoy alimenta el UNICO slot de ND
        // del BC — solo tiene sentido pisarlo cuando el operador que se cierra es el PRINCIPAL. Para uno
        // SECUNDARIO, este snapshot describe (o describira) a otro operador: tocarlo aca lo corromperia. Su
        // cierre sin multa queda representado SOLO a nivel linea (targetLines, ya marcadas Waived arriba). ===
        if (isPrimaryOperator)
        {
            bc.PenaltyStatus = PenaltyStatus.Waived;
            bc.PenaltyAmountAtEvent = 0m; // "no hubo multa" explicito (la cara fiscal al cliente es cero, sin ND).
            bc.DebitNoteStatus = DebitNoteStatus.NotApplicable; // sin ND (limpia un Failed/ManualReview previo).
            bc.DebitNoteArcaErrorMessage = null;                // el error de la ND que fallo ya no aplica.
            bc.DebitNotePurpose = null;                         // no hay finalidad de ND: la multa se cerro sin comprobante.
            bc.PenaltyConfirmedByUserId = userId;
            bc.PenaltyConfirmedByUserName = userName;
            bc.PenaltyConfirmedAt = DateTime.UtcNow;
        }

        // === Auditoria OBLIGATORIA (Condicion 1 del review): rastro que distingue "el operador no cobro multa"
        // (decision de negocio) de "penalidad = 0 por error". El campo `action` distingue el cierre normal del cierre
        // DESDE una multa confirmada, y en ese caso deja los caps restaurados (viejo->nuevo) para el contador.
        // LogBusinessEventAsync hace su propio SaveChanges, pero corre antes del commit de abajo; el orden es
        // aceptable (si el SaveChanges de abajo fallara por xmin, el reintento rebota por INV-WAIVE-003). ===
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.OperatorPenaltyWaived,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                reservaPublicId = bc.Reserva?.PublicId,
                action = waivingFromConfirmed ? "operator-penalty-waived-from-confirmed" : "operator-penalty-waived",
                reason = trimmedReason,
                bcStatus = bc.Status.ToString(),
                restoredRefundCaps = restoredCaps,
                // Menor 1 (ADR-044 T2): foto de los cargos borrados por el cierre sin multa (autocontencion).
                deletedOperatorCharges = waiveDeletedCharges,
                // Huella de ND ANTES de limpiarla (autocontencion del evento): estado previo + error de ARCA borrado.
                // Va SOLO al audit interno; nunca se expone al usuario final.
                previousDebitNoteStatus,
                clearedArcaErrorMessage,
                // ADR-044 T1 (2026-07-10): a que operador corresponde este cierre sin multa (rastro multi-operador).
                supplierPublicId = targetLines.FirstOrDefault()?.Supplier?.PublicId,
                isPrimaryOperator,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        // (2026-07-15) Si el cierre sin multa vino DESDE una multa confirmada, el Reverse de arriba borro TODOS
        // los cargos del operador — incluido un eventual FACTURADO APARTE, que sumaba al saldo OFICIAL. Recalculamos
        // el saldo del operador RESUELTO ANTES de reconciliar el pool (el reconciler lee ConfirmedPurchases -
        // TotalPaid de esa proyeccion, asi que tiene que estar fresca). Corre DESPUES del SaveChanges de arriba
        // (PersistAsync LEE los cargos ya flusheados) y hace su propio SaveChanges. Si el waive fue desde el estado
        // pendiente (Estimated), no hubo cargos que borrar y esto es un no-op barato.
        if (waivingFromConfirmed)
        {
            await TravelApi.Infrastructure.Reservations.SupplierDebtPersister.PersistAsync(_db, targetSupplierId, ct);
            await _db.SaveChangesAsync(ct);
        }

        // Pasos B/C (2026-06-29): el cierre sin multa deja los RefundCap COMPLETOS (el operador devuelve todo), asi
        // que el receivable Y sigue contando entero y el reconciler mantiene el pool en 0 (NO mintea la fuga). Lo
        // disparamos para que el pool quede coherente con el estado nuevo. C5: tras el waive sin reembolso, la BC
        // sigue esperando el reembolso (su Y vive) -> Prepago 0.
        //
        // ADR-044 T1: reconciliamos el pool del operador RESUELTO (targetSupplierId), no siempre el principal.
        await ReconcileSupplierCreditPoolAsync(targetSupplierId, userId, userName, ct);

        // (2026-07-03) Cierre INMEDIATO: al cerrar sin multa se resolvio la pata que bloqueaba el auto-cierre. Si
        // ademas la agencia nunca le pago nada reembolsable al operador (receivable $0), la anulacion ya no espera
        // nada -> se cierra en el momento en vez de quedar "esperando reembolso" hasta el barrido de las 4am. Si SI
        // hay reembolso pendiente (RefundCap > 0, el operador devuelve la plata pagada), es no-op: sigue esperando.
        await TryAutoCloseAfterOperatorPenaltyResolvedAsync(bc, ct);

        _logger.LogInformation(
            "metric:cancellation_operator_penalty_waived | BcPublicId={BcPublicId} By={UserId}",
            bc.PublicId, userId);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException("No se pudo completar la operación. Volvé a intentar.");
    }

    /// <inheritdoc />
    public async Task<BookingCancellationDto> RevertWaivedOperatorPenaltyAsync(
        Guid publicId,
        string reason,
        string userId,
        string? userName,
        bool requesterIsAdmin,
        CancellationToken ct,
        Guid? supplierPublicId = null)
    {
        // Fase A (2026-06-28): REVERSA del cierre sin multa. El cierre sin multa (Waived) es terminal, pero el
        // negocio puede necesitar reabrirlo: o bien fue un error, o bien el operador termino cobrando una multa
        // TARDIA. Como el waive no emitio ninguna Nota de Debito ni dejo cap sin restaurar, revertir es un flip de
        // estado LIMPIO de vuelta a Estimated (el estado pendiente), sin nada fiscal que deshacer.

        // El motivo es obligatorio (lo valida tambien el DataAnnotation del request, esto es defensivo).
        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
            throw new ArgumentException(
                "Indica un motivo para reabrir la penalidad del operador.", nameof(reason));

        var settings = await _settings.GetEntityAsync(ct);

        // === Precondicion 1: flag maestro. Con OFF todo el subsistema de penalidad esta inerte; rechazamos por
        // simetria con Waive/Confirm para no mutar estado de un subsistema deshabilitado. ===
        if (!settings.EnableCancellationDebitNote)
            throw new InvalidOperationException(
                "La gestión de penalidades de cancelación no está disponible en este momento. " +
                "Consultá con administración.");

        // === Precondicion 2: solo Admin. Va ANTES de cargar el BC a proposito: asi un usuario sin rol Admin no
        // puede distinguir un BC existente de uno inexistente por el codigo de error. El controller ya rechaza con
        // 403 a los no-Admin; este chequeo es defensa en profundidad (el service no confia en el caller). ===
        if (!requesterIsAdmin)
            throw new BusinessInvariantViolationException(
                "Reabrir un cierre sin multa del operador requiere rol de Administrador.",
                invariantCode: "INV-WAIVE-REVERT-PERM");

        // === Precondicion 3: el BC existe (404). Cargamos la Reserva para el detalle del audit; ademas Lines +
        // su Supplier + el Supplier del padre (ADR-044 T1) para resolver a que operador corresponde la reapertura
        // cuando la cancelacion tiene servicios de mas de uno (ADR-025). ===
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.Supplier)
            .Include(b => b.Lines)
                .ThenInclude(l => l.Supplier)
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // === ADR-044 T1 (2026-07-10, fix B2): a que OPERADOR corresponde esta reapertura. Mismo helper y misma
        // retrocompatibilidad que Confirm/Waive. Para el PRINCIPAL el estado Waived vive en el snapshot del BC
        // padre (byte-identico a antes de esta tanda); para un SECUNDARIO vive en SUS lineas. ===
        var targetSupplierId = ResolveTargetSupplierId(bc, supplierPublicId);
        var isPrimaryOperator = targetSupplierId == bc.SupplierId;
        var targetLines = bc.Lines.Where(l => l.SupplierId == targetSupplierId).ToList();

        // === Precondicion 4: solo se puede reabrir lo que esta cerrado sin multa (Waived). Si la penalidad esta
        // Estimated (ya pendiente) o Confirmed (se resolvio CON multa, hay una ND real de por medio), reabrir no
        // procede -> 409. Tambien cubre la idempotencia: revert dos veces => la segunda rebota aca. Para el
        // PRINCIPAL se mira el snapshot del padre; para un SECUNDARIO, SUS lineas (el snapshot del padre describe
        // al principal, no a este — sin esto un secundario cerrado sin multa quedaria IRREVERSIBLE). ===
        var isWaivedForTarget = isPrimaryOperator
            ? bc.PenaltyStatus == PenaltyStatus.Waived
            : targetLines.Any(l => l.PenaltyStatus == PenaltyStatus.Waived);
        if (!isWaivedForTarget)
            throw new BusinessInvariantViolationException(
                "Esta cancelación no está cerrada sin multa.",
                invariantCode: "INV-WAIVE-REVERT-001");

        // === Precondicion 5 (defensiva): un waive NUNCA debe tener una ND vinculada. Si por algun motivo la
        // hubiera, NO revertimos en silencio: habria que tratar primero esa ND. Mantiene la invariante "un cierre
        // sin multa no tiene comprobante fiscal" antes de tocar el estado. ADR-044 T1: aplica SOLO al principal —
        // el UNICO slot de ND del BC describe al principal; para un secundario ese documento (si existe) es de
        // OTRO operador y no debe bloquear su reapertura (mismo criterio que Confirm/Waive). ===
        if (isPrimaryOperator &&
            (bc.DebitNoteInvoiceId.HasValue ||
             bc.DebitNoteStatus == DebitNoteStatus.Pending ||
             bc.DebitNoteStatus == DebitNoteStatus.Issued))
            throw new BusinessInvariantViolationException(
                "No se puede reabrir: la cancelación tiene una Nota de Débito asociada.",
                invariantCode: "INV-WAIVE-REVERT-002");

        // === Precondicion 6 (A6, freno de consistencia, 2026-07-08): NO reabrir si el saldo a favor del cliente
        // originado por ESTA anulacion ya se uso/retiro por completo. Motivo: reabrir es el paso previo a confirmar
        // una multa; confirmar una multa recorta los RefundCap (lo que el operador debe reembolsar) y, en el neto de
        // la cancelacion, se apoya en que ese saldo a favor del cliente todavia exista para absorberla. Si el cliente
        // YA gasto todo ese saldo (lo aplico a otra reserva o lo retiro en efectivo), confirmar la multa despues
        // dejaria la cuenta descuadrada sin forma de recortar nada.
        //
        // CRITERIO conservador y simple (el "monto posible de multa" es DESCONOCIDO al deshacer): miramos solo el
        // caso sin retorno -> el saldo a favor DISPONIBLE de esta cancelacion quedo en CERO (todo consumido). Si
        // todavia queda algo, permitimos reabrir (la multa que se confirme despues queda capeada por los RefundCap,
        // como hoy). La info de "cuanto queda" ya vive en ClientCreditEntries.RemainingBalance (columna denormalizada,
        // suma de lo no consumido/retirado por moneda).
        //
        // OJO (no sobre-bloquear): si esta anulacion NUNCA genero saldo a favor del cliente (ej. multa pura, el
        // cliente no tenia plata a favor), no hay nada que proteger -> se permite. Por eso el bloqueo exige que
        // HAYA existido saldo (alguna entry) Y que hoy este todo en cero, no simplemente "suma cero" (que tambien
        // daria una anulacion sin ninguna entry).
        var clientCredit = await _db.ClientCreditEntries
            .AsNoTracking()
            .Where(e => e.BookingCancellationId == bc.Id)
            .GroupBy(e => 1)
            .Select(g => new { HadCredit = true, Available = g.Sum(e => e.RemainingBalance) })
            .FirstOrDefaultAsync(ct);
        if (clientCredit is not null && clientCredit.Available <= 0m)
            // Voz de los avisos (regla del dueño 2026-07-08): "registrala con quien maneje la cuenta del cliente"
            // derivaba a un rol que quien esta viendo este cartel probablemente ES. Texto coordinado con el
            // fallback que pone el front cuando le llega este mismo code (SALDO_YA_USADO): mismo texto en los dos
            // lados para que nunca se vean mensajes distintos segun de donde vino el cartel.
            throw new ClientCreditAlreadyUsedException(
                "El cliente ya usó ese saldo a favor, por eso no se puede deshacer este cierre. Si el operador " +
                "te cobró una multa ahora, cobrásela al cliente como un cargo de la agencia desde la ficha.");

        // === Reversa LIMPIA a Estimated. Restauramos exactamente los defaults del estado pendiente que el waive
        // habia pisado: el waive habia puesto PenaltyAmountAtEvent=0 y los campos de confirmado-por; los volvemos a
        // null. DebitNoteStatus ya es NotApplicable y DebitNoteInvoiceId null (garantizado en la Precondicion 5),
        // que es justamente el default del estado Estimated -> no hay nada mas que deshacer.
        //
        // NOTA (fix "multa fantasma", 2026-07-05): si el waive vino DESDE una multa confirmada, ese waive ya habia
        // restaurado los topes de reembolso de las lineas (ReverseConfirmedPenaltyFromLinesAsync dejo PenaltyAmount
        // en null). Por eso aca NO hay que tocar los caps: la reversa vuelve a Estimated y, si el operador termino
        // cobrando la multa, se RE-CONFIRMA por el camino normal (ConfirmPenaltyAsync), que vuelve a imputar la multa
        // a las lineas. La secuencia waive-desde-Confirmed -> revert -> confirmar es coherente y no descuadra caps.
        //
        // ADR-044 T1 (2026-07-10, fix B2): el snapshot del BC padre describe al PRINCIPAL — solo se restaura si el
        // que se reabre es el principal. Para un SECUNDARIO, su estado Waived vive en SUS lineas: se vuelven a
        // Estimated (espejo de como el waive de un secundario las dejo Waived). En ambos casos las lineas del
        // operador resuelto pasan a Estimated (para el principal es redundante con el snapshot, pero coherente). ===
        if (isPrimaryOperator)
        {
            bc.PenaltyStatus = PenaltyStatus.Estimated;
            bc.PenaltyAmountAtEvent = null;          // el waive lo habia puesto en 0; Estimated = aun sin monto
            bc.PenaltyConfirmedByUserId = null;
            bc.PenaltyConfirmedByUserName = null;
            bc.PenaltyConfirmedAt = null;
        }
        foreach (var line in targetLines)
            line.PenaltyStatus = PenaltyStatus.Estimated;

        // === Auditoria OBLIGATORIA: rastro de quien reabrio, cuando y por que. LogBusinessEventAsync corre antes
        // del commit de abajo; el orden es aceptable (si el SaveChanges fallara por xmin, el reintento rebota por
        // INV-WAIVE-REVERT-001 porque el estado ya no seria Waived, o por concurrencia). ===
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.OperatorPenaltyWaiveReverted,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                reservaPublicId = bc.Reserva?.PublicId,
                action = "operator-penalty-waive-reverted",
                reason = trimmedReason,
                bcStatus = bc.Status.ToString(),
                // ADR-044 T1: a que operador corresponde la reapertura (rastro multi-operador).
                supplierPublicId = targetLines.FirstOrDefault()?.Supplier?.PublicId ?? bc.Supplier?.PublicId,
                isPrimaryOperator,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        // Pasos B/C (2026-06-29): reabrir el cierre sin multa vuelve la penalidad a Estimated sin tocar caps; el
        // pool no deberia cambiar, pero reconciliamos por coherencia (idempotente). ADR-044 T1: pool del operador
        // RESUELTO (targetSupplierId), no siempre el principal.
        await ReconcileSupplierCreditPoolAsync(targetSupplierId, userId, userName, ct);

        _logger.LogInformation(
            "metric:cancellation_operator_penalty_waive_reverted | BcPublicId={BcPublicId} By={UserId}",
            bc.PublicId, userId);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException("No se pudo completar la operación. Volvé a intentar.");
    }

    // =========================================================================
    // ADR-044 T3b Decision 1 (2026-07-10): resolucion de la factura destino de un cargo del operador.
    // =========================================================================

    /// <summary>
    /// Lista TODAS las facturas de venta VIVAS con CAE de una reserva. Mismo filtro que ya usan
    /// <c>DraftAsync</c>/<c>ResolveAndPreflightInvoicesToAnnulAsync</c> (excluye NC/ND y filas fantasma sin CAE
    /// — un intento de emision fallido/reintento nunca cuenta como factura activa). Se reusa aca porque el
    /// numero de facturas activas es lo que decide si un cargo del operador se autocompleta solo (1 factura) o
    /// necesita eleccion humana (2+).
    /// </summary>
    private async Task<List<Invoice>> LoadActiveSaleInvoicesForReservaAsync(int reservaId, CancellationToken ct)
    {
        return await _db.Invoices
            .Where(i => i.ReservaId == reservaId
                     && i.AnnulmentStatus != AnnulmentStatus.Succeeded
                     && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante)
                     && !LiveInvoiceDebitNoteTypes.Contains(i.TipoComprobante)
                     && !string.IsNullOrEmpty(i.CAE))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// ADR-044 T3b Decision 1: caso simple, sin friccion. Con 1 sola factura de venta activa, esa es la factura
    /// destino del cargo (autocompletada, ningun desplegable visible). Con 0 o 2+ facturas activas, devuelve
    /// null: 0 no deberia pasar (ya hay un BC, tiene que existir al menos una factura); 2+ exige que un humano
    /// elija (nunca se adivina con cual moneda/monto "matchea mejor" — riesgo fiscal inaceptable, ver Addendum).
    /// </summary>
    private static int? ResolveAutoTargetInvoiceId(List<Invoice> activeInvoices)
        => activeInvoices.Count == 1 ? activeInvoices[0].Id : (int?)null;

    /// <summary>
    /// ADR-044 T3b Decision 1 (M2, invariante dura): todos los cargos TRASLADABLES al cliente (<c>Kind !=
    /// Withholding</c>) de la MISMA linea tienen que compartir la misma factura destino — una linea es un
    /// servicio, y un servicio vive en UNA sola factura del cliente. Devuelve <c>null</c> si
    /// <paramref name="candidateTargetInvoiceId"/> es compatible (sin conflicto), o un mensaje claro si otro
    /// cargo trasladable de esa linea ya quedo resuelto contra una factura DISTINTA. Los <c>Withholding</c> y los
    /// candidatos <c>null</c> quedan exentos (nunca chocan): un <c>Withholding</c> nunca emite renglon de ND, y
    /// sin candidato no hay nada que comparar.
    /// </summary>
    private async Task<string?> ValidateTargetInvoiceConsistencyForLineAsync(
        int lineId, OperatorChargeKind kind, int? candidateTargetInvoiceId, int? excludingChargeId, CancellationToken ct)
    {
        if (kind == OperatorChargeKind.Withholding || candidateTargetInvoiceId is null)
            return null;

        var conflicting = await _db.BookingCancellationLineOperatorCharges
            .Where(c => c.BookingCancellationLineId == lineId
                     && c.Kind != OperatorChargeKind.Withholding
                     && c.TargetInvoiceId != null
                     && c.TargetInvoiceId != candidateTargetInvoiceId
                     && (excludingChargeId == null || c.Id != excludingChargeId.Value))
            .AnyAsync(ct);

        return conflicting
            ? "Ese servicio ya tiene otro cargo del operador con una factura destino distinta: los cargos de un " +
              "mismo servicio tienen que ir a la misma factura del cliente."
            : null;
    }

    /// <inheritdoc />
    public async Task<BookingCancellationDto> AddOperatorChargeAsync(
        Guid publicId,
        AddOperatorChargeRequest request,
        string userId,
        string? userName,
        CancellationToken ct,
        bool userCanClassifyAgencyPenalty = false)
    {
        // ADR-044 T2 Addendum (2026-07-10): "agregar otro cargo de este operador" — accion SECUNDARIA y OPCIONAL
        // sobre una multa YA confirmada (ej. sumar una retencion fiscal ademas del cargo administrativo que el
        // confirm automatico ya creo). NO es el flujo simple: ese sigue siendo confirmar la multa a secas.
        if (request is null) throw new ArgumentNullException(nameof(request));

        var settings = await _settings.GetEntityAsync(ct);
        if (!settings.EnableCancellationDebitNote)
            // Voz de los avisos (regla del dueño): quien llama a esta accion ES administracion; no lo derivamos a
            // "administracion". Mensaje autocontenido, mismo estilo que CorrectPenaltyAsync para el flag OFF.
            throw new InvalidOperationException(
                "No se pudo agregar el cargo del operador. Volvé a intentar más tarde.");

        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.Supplier)
            .Include(b => b.Lines)
                .ThenInclude(l => l.Supplier)
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // === PERMISO PRIMERO (menor 5, security): antes de validar los inputs (documento / moneda), para no
        // filtrarle detalles de validacion a quien no tiene permiso de resolver la pata del operador. Mismo gate
        // fiscal que confirmar/corregir la multa. ===
        if (!userCanClassifyAgencyPenalty)
            throw new BusinessInvariantViolationException(
                "No tenés permiso para agregar un cargo del operador. Pedíselo a un administrador.",
                invariantCode: "INV-ADR044-CHARGE-PERM");

        // Un cargo facturado aparte necesita su documento del proveedor (espejo del CHECK SQL de la tabla).
        var trimmedDocumentRef = request.DocumentRef?.Trim();
        if (request.CollectionMode == PenaltyCollectionMode.FacturadaAparte
            && string.IsNullOrWhiteSpace(trimmedDocumentRef))
            throw new ArgumentException(
                "Indicá el documento del proveedor: un cargo facturado aparte necesita su referencia.",
                nameof(request));

        // ADR-044 T3a (2026-07-10): coherencia ClientTransferMode <-> ManagementFeeAmount, espejo de los 2 CHECK
        // SQL de la migracion T3a. El monto del cargo de gestion es obligatorio CON "+ cargo de gestion" y tiene
        // que quedar vacio en cualquier otro modo (un monto cargado que nadie factura confundiria el extracto).
        if (request.ClientTransferMode == ClientTransferMode.WithManagementFee
            && (!request.ManagementFeeAmount.HasValue || request.ManagementFeeAmount.Value <= 0m))
            throw new ArgumentException(
                "Para trasladar este cargo \"+ cargo de gestión\" indicá el monto del cargo de gestión (mayor a cero).",
                nameof(request));
        if (request.ClientTransferMode != ClientTransferMode.WithManagementFee
            && request.ManagementFeeAmount.HasValue)
            throw new ArgumentException(
                "El monto del cargo de gestión solo se informa cuando el traslado es \"+ cargo de gestión\".",
                nameof(request));

        // ADR-044 T3a (menor 1, review 2026-07-10): una retencion fiscal (Withholding) NUNCA llega al cliente
        // (es credito fiscal de la agencia), asi que su forma de traslado al cliente no tiene sentido: debe ser
        // AsIs (el default). Rechazamos "+ cargo de gestion" o "absorber" sobre un Withholding — seria un dato
        // contradictorio (un fee de gestion o una absorcion sobre plata que nunca se le cobra al cliente).
        if (request.Kind == OperatorChargeKind.Withholding
            && request.ClientTransferMode != ClientTransferMode.AsIs)
            throw new ArgumentException(
                "Una retención fiscal no se le traslada al cliente, así que no admite cargo de gestión ni absorción.",
                nameof(request));

        // A que operador corresponde este cargo. Mismo helper y misma retrocompatibilidad que confirm/waive.
        var targetSupplierId = ResolveTargetSupplierId(bc, request.SupplierPublicId);
        var targetLines = bc.Lines.Where(l => l.SupplierId == targetSupplierId).ToList();

        // Solo se puede agregar un cargo SECUNDARIO sobre una multa que YA esta confirmada (el cargo base ya
        // existe): agregar antes seria "inventar" un cargo sin el flujo normal de confirmar la multa primero.
        var isPrimaryOperator = targetSupplierId == bc.SupplierId;
        var alreadyConfirmedForTarget = isPrimaryOperator
            ? bc.PenaltyStatus == PenaltyStatus.Confirmed
            : targetLines.Any(l => l.PenaltyStatus == PenaltyStatus.Confirmed);
        if (!alreadyConfirmedForTarget)
            throw new BusinessInvariantViolationException(
                "Primero confirmá la multa de este operador; recién ahí se le puede agregar otro cargo.",
                invariantCode: "INV-ADR044-CHARGE-001");

        // Gate CommissionOnly (Decision A del Addendum): mismo criterio y mismo mensaje que el confirm automatico.
        if (AnyLineHasCommissionOnlyInvoicingMode(targetLines))
            throw new BusinessInvariantViolationException(
                "Este operador solo cobra comisión: no retiene multas. Si te descontó algo, registralo como cargo " +
                "facturado aparte con su documento.",
                invariantCode: "INV-ADR044-T2-COMMISSIONONLY");

        // B2 (moneda coherente con la linea): el cargo solo se reparte entre las lineas de ESTE operador cuya
        // moneda de servicio coincide con la del cargo. Si ninguna coincide, no hay donde registrarlo.
        var normalizedCurrency = Monedas.Normalizar(request.Currency);
        var matchingLineIds = targetLines
            .Where(l => string.Equals(Monedas.Normalizar(l.Currency), normalizedCurrency, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Id)
            .ToList();
        if (matchingLineIds.Count == 0)
            throw new ArgumentException(
                "La moneda del cargo no coincide con la moneda de los servicios de este operador en esta cancelación.",
                nameof(request));

        // ADR-044 T3b Decision 1 (2026-07-10): factura destino de este cargo. Con 1 sola factura de venta
        // activa se autocompleta (transparente). Con 2+, si el caller especifico una, la validamos contra las
        // facturas ACTIVAS de la reserva (nunca aceptamos a ciegas una factura ajena o ya muerta). Sin eleccion
        // y con 2+ activas, queda null (el motor de emision de la ND la rutea a revision manual mas adelante).
        var activeInvoicesForTarget = await LoadActiveSaleInvoicesForReservaAsync(bc.ReservaId, ct);
        int? resolvedTargetInvoiceId = ResolveAutoTargetInvoiceId(activeInvoicesForTarget);
        if (resolvedTargetInvoiceId is null && request.TargetInvoicePublicId.HasValue)
        {
            var chosenInvoice = activeInvoicesForTarget
                .FirstOrDefault(i => i.PublicId == request.TargetInvoicePublicId.Value);
            if (chosenInvoice is null)
                throw new BusinessInvariantViolationException(
                    "La factura elegida no es una factura de venta activa de esta reserva.",
                    invariantCode: "INV-ADR044-TARGETINVOICE-001");
            resolvedTargetInvoiceId = chosenInvoice.Id;
        }

        // M2 (invariante dura): ningun cargo trasladable (Kind != Withholding) de las lineas alcanzadas puede
        // ya tener una factura destino DISTINTA a la resuelta arriba.
        foreach (var lineIdToCheck in matchingLineIds)
        {
            var conflictMessage = await ValidateTargetInvoiceConsistencyForLineAsync(
                lineIdToCheck, request.Kind, resolvedTargetInvoiceId, excludingChargeId: null, ct);
            if (conflictMessage is not null)
                throw new BusinessInvariantViolationException(
                    conflictMessage, invariantCode: "INV-ADR044-TARGETINVOICE-002");
        }

        // ADR-044 T3b Decision 2 (2026-07-10): TC ESTIMADO (preview), solo si el caller lo informo (relevante
        // cuando la moneda del cargo difiere de la de su factura destino). Coherencia minima: el TC, su origen y
        // su fecha se cargan JUNTOS (menor 1), "Manual" exige justificacion (mismo criterio INV-120 de toda
        // factura en moneda extranjera del sistema), y el TC pasa la banda de sanidad (S1/F1: no puede quedar en
        // 1 ni en 0, "default peligroso" de cotizacion sin cargar).
        if (request.EstimatedExchangeRateToClientInvoiceCurrency.HasValue != request.EstimatedExchangeRateSource.HasValue)
            throw new ArgumentException(
                "El tipo de cambio estimado y su origen se cargan juntos.", nameof(request));
        if (request.EstimatedExchangeRateToClientInvoiceCurrency.HasValue
            && !request.EstimatedExchangeRateAt.HasValue)
            throw new ArgumentException(
                "El tipo de cambio estimado necesita su fecha.", nameof(request));
        if (request.EstimatedExchangeRateToClientInvoiceCurrency.HasValue
            && IsUnreliableExchangeRate(request.EstimatedExchangeRateToClientInvoiceCurrency.Value))
            throw new ArgumentException(
                "El tipo de cambio no puede quedar en 1 (parece sin completar): cargá la cotización real.",
                nameof(request));
        if (request.EstimatedExchangeRateSource == ExchangeRateSource.Manual
            && string.IsNullOrWhiteSpace(request.EstimatedExchangeRateJustification))
            throw new ArgumentException(
                "Un tipo de cambio cargado a mano necesita una justificación.", nameof(request));

        // Efecto en la plata (ver el XML-doc de la interfaz): solo Retenida + Kind!=Withholding resta el
        // RefundCap. Withholding nunca resta (credito fiscal); FacturadaAparte nunca resta (deuda AP aparte).
        bool affectsRefundCap = request.CollectionMode == PenaltyCollectionMode.Retenida
            && request.Kind != OperatorChargeKind.Withholding;
        // Eje CLIENTE (PenaltyAmount = lo que efectivamente se le traslada al cliente via ND): suma un cargo solo
        // si NO es Withholding (retencion fiscal, nunca llega al cliente) Y NO es Absorbed (la agencia decidio no
        // cobrarselo). ADR-044 T3a (menor 4, review 2026-07-10): antes esto solo excluia Withholding, asi que un
        // cargo Absorbed inflaba PenaltyAmount con plata que la ND NUNCA factura — cualquier lector que use
        // PenaltyAmount como "lo trasladado al cliente" veria un total mentiroso. El eje CAJA (RetainedDeductionAmount,
        // que descuenta el RefundCap del operador) es ORTOGONAL: un cargo Retenida+Absorbed SI redujo el reembolso
        // del operador (el operador retuvo), pero NO se le cobra al cliente — por eso los dos ejes se calculan aparte.
        bool affectsClientAmount = request.Kind != OperatorChargeKind.Withholding
            && request.ClientTransferMode != ClientTransferMode.Absorbed;
        var trimmedNotes = request.Notes?.Trim();

        // === BLOQUEANTE 2 (backend): candado pesimista del padre (mismo patron que CorrectPenaltyAsync) +
        // dedup por ventana de 60s. Un doble click / retry de red NO debe duplicar plata. En InMemory (tests unit)
        // corre el cuerpo directo, sin lock ni transaccion. ===
        await RunUnderParentLockAsync<bool>(bc.Id, async () =>
        {
            await _db.Entry(bc).ReloadAsync(ct);

            // Re-leer las lineas del operador TRACKED y FRESCAS dentro del lock (un AddOperatorCharge o un
            // confirm/waive concurrente del mismo BC, serializado por este mismo lock, pudo cambiar los caps).
            // ReloadAsync por linea porque una tracking-query NO refresca entidades ya tracked por la Include.
            var freshMatching = await _db.BookingCancellationLines
                .Where(l => matchingLineIds.Contains(l.Id))
                .ToListAsync(ct);
            foreach (var l in freshMatching) await _db.Entry(l).ReloadAsync(ct);

            // Re-chequear la precondicion de "multa confirmada" DENTRO del lock: un waive-desde-Confirmed
            // concurrente pudo revertir la confirmacion entre la validacion de afuera y aca (mismo motivo por el
            // que CorrectPenaltyAsync re-exige Confirmed tras el Reload).
            var stillConfirmed = isPrimaryOperator
                ? bc.PenaltyStatus == PenaltyStatus.Confirmed
                : freshMatching.Any(l => l.PenaltyStatus == PenaltyStatus.Confirmed);
            if (!stillConfirmed)
                throw new BusinessInvariantViolationException(
                    "Primero confirmá la multa de este operador; recién ahí se le puede agregar otro cargo.",
                    invariantCode: "INV-ADR044-CHARGE-001");

            // Dedup (BLOQUEANTE 2.b): si en los ultimos 60s ya se registro un cargo VIVO con la MISMA firma
            // (linea del operador + Kind + CollectionMode + Currency + DocumentRef) por un total >= al pedido,
            // es casi seguro un doble submit. Rebota 409 idempotente. Un cargo IGUAL de verdad se puede volver a
            // cargar pasada la ventana. Comparamos DocumentRef en memoria (string.Equals) para no depender de la
            // semantica de NULL del provider (InMemory vs Postgres).
            var dedupCutoff = DateTime.UtcNow.AddSeconds(-60);
            var recentSameSignature = await _db.BookingCancellationLineOperatorCharges
                .Where(c => matchingLineIds.Contains(c.BookingCancellationLineId)
                         && c.Kind == request.Kind
                         && c.CollectionMode == request.CollectionMode
                         && c.Currency == normalizedCurrency
                         && c.ConfirmedAt >= dedupCutoff)
                .Select(c => new { c.DocumentRef, c.Amount })
                .ToListAsync(ct);
            var recentSameTotal = recentSameSignature
                .Where(c => string.Equals(c.DocumentRef, trimmedDocumentRef, StringComparison.Ordinal))
                .Sum(c => c.Amount);
            if (recentSameTotal >= request.Amount)
                throw new BusinessInvariantViolationException(
                    "Ese cargo ya se registró recién. Si es otro cargo igual de verdad, esperá un momento y volvé " +
                    "a cargarlo.",
                    invariantCode: "INV-ADR044-CHARGE-DUP");

            // Repartir contra los caps FRESCOS (por RefundCap remanente si afecta caja; por LineSaleAmount si no).
            var shares = DistributeChargeAcrossLines(freshMatching, request.Amount, affectsRefundCap);

            // === BLOQUEANTE 1 (security): NUNCA aplicar parcial. Si el cargo afecta caja y el RefundCap remanente
            // no alcanza para retener el monto COMPLETO (incluye el caso "cap agotado" = suma 0), rebota sin
            // persistir NADA y sin emitir audit. La retencion es todo-o-nada: si no entra, se corrige el monto o
            // se registra como facturado aparte. ===
            var applicableTotal = shares.Where(s => s.Share > 0m).Sum(s => s.Share);
            if (affectsRefundCap && applicableTotal < request.Amount)
                throw new BusinessInvariantViolationException(
                    "El operador no tiene reembolso pendiente suficiente para retener este cargo. Corregí el monto " +
                    "o registralo como facturado aparte con su documento.",
                    invariantCode: "INV-ADR044-CHARGE-002");

            var appliedAt = DateTime.UtcNow;
            decimal appliedTotal = 0m;
            // ADR-044 T3a: si el cargo lleva fee de gestion, se reparte PROPORCIONAL a como se repartio el monto
            // base entre las lineas alcanzadas (misma proporcion share/Amount) — un cargo dividido entre 2 lineas
            // del mismo operador reparte su fee en la misma proporcion, no en partes iguales. La ULTIMA linea con
            // porcion > 0 absorbe el centavo de redondeo, mismo criterio que el resto del reparto de este archivo.
            var lastShareIndexWithAmount = shares.FindLastIndex(s => s.Share > 0m);
            decimal managementFeeAllocatedSoFar = 0m;
            for (int shareIndex = 0; shareIndex < shares.Count; shareIndex++)
            {
                var (line, share) = shares[shareIndex];
                if (share <= 0m) continue;

                decimal? managementFeeForLine = null;
                if (request.ClientTransferMode == ClientTransferMode.WithManagementFee)
                {
                    managementFeeForLine = shareIndex == lastShareIndexWithAmount
                        ? request.ManagementFeeAmount!.Value - managementFeeAllocatedSoFar
                        : Math.Round(
                            request.ManagementFeeAmount!.Value * (share / request.Amount),
                            2, MidpointRounding.AwayFromZero);
                    managementFeeAllocatedSoFar += managementFeeForLine.Value;
                }

                _db.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
                {
                    BookingCancellationLine = line,
                    Kind = request.Kind,
                    CollectionMode = request.CollectionMode,
                    Amount = share,
                    Currency = normalizedCurrency,
                    DocumentRef = trimmedDocumentRef,
                    Notes = trimmedNotes,
                    ClientTransferMode = request.ClientTransferMode,
                    ManagementFeeAmount = managementFeeForLine,
                    ConfirmedByUserId = userId,
                    ConfirmedByUserName = userName,
                    ConfirmedAt = appliedAt,
                    TargetInvoiceId = resolvedTargetInvoiceId,
                    EstimatedExchangeRateToClientInvoiceCurrency = request.EstimatedExchangeRateToClientInvoiceCurrency,
                    EstimatedExchangeRateSource = request.EstimatedExchangeRateSource,
                    EstimatedExchangeRateAt = request.EstimatedExchangeRateAt,
                    EstimatedExchangeRateJustification = request.EstimatedExchangeRateJustification?.Trim(),
                });

                if (affectsRefundCap)
                {
                    line.RefundCap -= share;
                    if (line.RefundCap < 0m) line.RefundCap = 0m; // defensivo: DistributeChargeAcrossLines ya topea
                    line.RetainedDeductionAmount += share;
                    if (line.RefundCap <= 0m)
                        line.RefundStatus = BookingCancellationLineRefundStatus.None;
                }

                if (affectsClientAmount)
                    line.PenaltyAmount = (line.PenaltyAmount ?? 0m) + share;

                appliedTotal += share;
            }

            // Auditoria STAGED (mismo SaveChanges que la mutacion, atomico): SOLO se emite porque algo se
            // persistio, y con el monto REALMENTE aplicado (appliedTotal), no request.Amount. Para caja completo,
            // appliedTotal == request.Amount por el guard de arriba; para Withholding/FacturadaAparte tambien
            // (no hay tope). Se registra por si en el futuro cambia la regla de reparto.
            _auditService.StageBusinessEvent(
                action: AuditActions.OperatorChargeAdded,
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    bc.PublicId,
                    reservaPublicId = bc.Reserva?.PublicId,
                    supplierPublicId = targetLines.FirstOrDefault()?.Supplier?.PublicId,
                    isPrimaryOperator,
                    kind = request.Kind.ToString(),
                    collectionMode = request.CollectionMode.ToString(),
                    requestedAmount = request.Amount,
                    appliedAmount = appliedTotal,
                    currency = normalizedCurrency,
                    documentRef = trimmedDocumentRef,
                    // ADR-044 T3a (menor 2, review 2026-07-10): como se traslada al cliente + el cargo de gestion,
                    // para que el contador vea en la auditoria si el cargo se cobro tal cual / con cargo de gestion
                    // aparte / se absorbio, y por cuanto.
                    clientTransferMode = request.ClientTransferMode.ToString(),
                    managementFeeAmount = request.ManagementFeeAmount,
                }),
                userId: userId,
                userName: userName);

            await _db.SaveChangesAsync(ct);

            // (2026-07-15) Un cargo FACTURADO APARTE es deuda nueva hacia el operador que ahora suma al saldo
            // OFICIAL (Supplier.CurrentBalance / SupplierBalanceByCurrency). Recalculamos ese saldo DESPUES del
            // SaveChanges de arriba: PersistAsync LEE el cargo de la base (via OperatorChargeInvoicedReader), asi
            // que el cargo tiene que estar flusheado primero (mismo gotcha EF que el path de cancelacion parcial).
            // Solo para FacturadaAparte: un cargo Retenida ya esta neteado en el RefundCap y NO toca el saldo
            // oficial, recalcular ahi seria trabajo al pedo. PersistAsync no guarda solo -> SaveChanges propio.
            if (request.CollectionMode == PenaltyCollectionMode.FacturadaAparte)
            {
                await TravelApi.Infrastructure.Reservations.SupplierDebtPersister.PersistAsync(
                    _db, targetSupplierId, ct);
                await _db.SaveChangesAsync(ct);
            }

            _logger.LogInformation(
                "metric:cancellation_operator_charge_added | BcPublicId={BcPublicId} Supplier={SupplierId} " +
                "Kind={Kind} CollectionMode={CollectionMode} Applied={Applied} By={UserId}",
                bc.PublicId, targetSupplierId, request.Kind, request.CollectionMode, appliedTotal, userId);

            return true;
        }, ct);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException("No se pudo completar la operación. Volvé a intentar.");
    }

    /// <inheritdoc />
    public async Task<BookingCancellationDto> SetOperatorChargeTargetInvoiceAsync(
        Guid publicId,
        Guid chargePublicId,
        SetOperatorChargeTargetInvoiceRequest request,
        string userId,
        string? userName,
        CancellationToken ct,
        bool userCanClassifyAgencyPenalty = false)
    {
        // ADR-044 T3b Decision 1 (2026-07-10): "elegir/corregir la factura destino de un cargo del operador" —
        // se usa con 2+ facturas de venta activas de la reserva (ADR-042), cuando el motor de emision de la ND
        // no pudo autocompletar sola. La pantalla que llama esto (desplegable, oculto si hay 1 sola factura) es
        // ADR-044 T4; este metodo es el contrato de backend que esa pantalla consume.
        if (request is null) throw new ArgumentNullException(nameof(request));

        var settings = await _settings.GetEntityAsync(ct);
        if (!settings.EnableCancellationDebitNote)
            throw new InvalidOperationException(
                "No se pudo actualizar la factura del cargo. Volvé a intentar más tarde.");

        var bc = await _db.BookingCancellations
            .FirstOrDefaultAsync(b => b.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"BC {publicId} no encontrada.");

        // Mismo gate fiscal que agregar/confirmar un cargo del operador (permiso PRIMERO, antes de validar datos).
        if (!userCanClassifyAgencyPenalty)
            throw new BusinessInvariantViolationException(
                "No tenés permiso para elegir la factura de este cargo. Pedíselo a un administrador.",
                invariantCode: "INV-ADR044-CHARGE-PERM");

        var charge = await _db.BookingCancellationLineOperatorCharges
            .Include(c => c.BookingCancellationLine)
            .FirstOrDefaultAsync(c => c.PublicId == chargePublicId
                                    && c.BookingCancellationLine.BookingCancellationId == bc.Id, ct)
            ?? throw new KeyNotFoundException($"Cargo {chargePublicId} no encontrado en esta cancelación.");

        // S3 (bloqueante security, 2026-07-10): una vez que la Nota de Debito al cliente ya se EMITIO (o esta en
        // vuelo), la factura destino de este cargo YA quedo congelada en el comprobante — cambiarla ahora
        // dejaria el ajuste FX y el snapshot fiscal apuntando a una factura distinta de la que salio. Se bloquea
        // si el BC ya tiene ND vinculada / en vuelo / emitida, o si la propia linea del cargo la tiene (caso
        // multi-operador, ADR-044 T1). ManualReview y NotApplicable SI dejan corregir: son justamente los
        // estados donde todavia no salio nada y hay que elegir/corregir la factura antes de reintentar.
        var line = charge.BookingCancellationLine;
        bool debitNoteInFlightOrIssued =
            bc.DebitNoteInvoiceId.HasValue
            || bc.DebitNoteStatus is DebitNoteStatus.Pending or DebitNoteStatus.Issued
            || line.DebitNoteInvoiceId.HasValue
            || line.DebitNoteStatus is DebitNoteStatus.Pending or DebitNoteStatus.Issued;
        if (debitNoteInFlightOrIssued)
            throw new BusinessInvariantViolationException(
                "La multa al cliente ya se emitió: la factura destino de este cargo ya no se puede cambiar.",
                invariantCode: "INV-ADR044-TARGETINVOICE-003");

        // La factura elegida tiene que ser una factura de venta ACTIVA de la reserva (viva, con CAE, no NC/ND):
        // nunca se acepta a ciegas una factura ajena o ya muerta.
        var activeInvoices = await LoadActiveSaleInvoicesForReservaAsync(bc.ReservaId, ct);
        var chosenInvoice = activeInvoices.FirstOrDefault(i => i.PublicId == request.TargetInvoicePublicId);
        if (chosenInvoice is null)
            throw new BusinessInvariantViolationException(
                "La factura elegida no es una factura de venta activa de esta reserva.",
                invariantCode: "INV-ADR044-TARGETINVOICE-001");

        // M2 (invariante dura): ningun OTRO cargo trasladable (Kind != Withholding) de la MISMA linea puede ya
        // tener una factura destino distinta a la elegida.
        var conflictMessage = await ValidateTargetInvoiceConsistencyForLineAsync(
            charge.BookingCancellationLineId, charge.Kind, chosenInvoice.Id, excludingChargeId: charge.Id, ct);
        if (conflictMessage is not null)
            throw new BusinessInvariantViolationException(
                conflictMessage, invariantCode: "INV-ADR044-TARGETINVOICE-002");

        charge.TargetInvoiceId = chosenInvoice.Id;

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.OperatorChargeAdded,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                operatorChargePublicId = charge.PublicId,
                action = "operator-charge-target-invoice-set",
                targetInvoicePublicId = chosenInvoice.PublicId,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "metric:cancellation_operator_charge_target_invoice_set | BcPublicId={BcPublicId} ChargePublicId={ChargePublicId}",
            bc.PublicId, charge.PublicId);

        return await MapToDtoAsync(bc.Id, ct)
            ?? throw new InvalidOperationException("No se pudo completar la operación. Volvé a intentar.");
    }

    /// <summary>
    /// ADR-044 T2 Addendum (2026-07-10): reparte un cargo nuevo entre las lineas candidatas del operador (ya
    /// filtradas por moneda). Con UNA sola linea (el caso comun de hoy), el 100% va a esa linea. Con VARIAS
    /// (cancelacion parcial multi-servicio del mismo operador, ADR-025), reparte proporcional:
    /// <list type="bullet">
    /// <item>Si <paramref name="affectsRefundCap"/> (Retenida + Kind!=Withholding): pondera por el
    /// <c>RefundCap</c> REMANENTE de cada linea (mismo criterio que <c>AllocateConfirmedPenaltyToLinesAsync</c>)
    /// y nunca supera ese cap por linea — asi nunca deja un <c>RefundCap</c> negativo.</item>
    /// <item>Si NO afecta el cap (Withholding o FacturadaAparte: no hay tope de caja que preservar), pondera por
    /// <c>LineSaleAmount</c> (proxy razonable de "cuanto de este operador corresponde a cada servicio"); si todas
    /// las lineas tienen <c>LineSaleAmount</c> 0, reparte en partes iguales.</item>
    /// </list>
    /// La ULTIMA linea siempre absorbe el residuo de redondeo, para que la suma de las porciones sea EXACTA.
    /// </summary>
    private static List<(BookingCancellationLine Line, decimal Share)> DistributeChargeAcrossLines(
        List<BookingCancellationLine> matchingLines, decimal totalAmount, bool affectsRefundCap)
    {
        var result = new List<(BookingCancellationLine, decimal)>();
        if (matchingLines.Count == 1)
        {
            var onlyLine = matchingLines[0];
            var amount = affectsRefundCap ? Math.Min(totalAmount, onlyLine.RefundCap) : totalAmount;
            if (amount < 0m) amount = 0m;
            result.Add((onlyLine, amount));
            return result;
        }

        Func<BookingCancellationLine, decimal> weightOf = affectsRefundCap
            ? l => l.RefundCap
            : l => l.LineSaleAmount;

        decimal totalWeight = matchingLines.Sum(weightOf);
        bool useEqualSplit = totalWeight <= 0m;

        decimal allocatedSoFar = 0m;
        for (int i = 0; i < matchingLines.Count; i++)
        {
            var line = matchingLines[i];
            bool isLastLine = i == matchingLines.Count - 1;

            decimal share;
            if (isLastLine)
            {
                share = totalAmount - allocatedSoFar;
            }
            else if (useEqualSplit)
            {
                share = Math.Round(totalAmount / matchingLines.Count, 2, MidpointRounding.AwayFromZero);
            }
            else
            {
                share = Math.Round(totalAmount * (weightOf(line) / totalWeight), 2, MidpointRounding.AwayFromZero);
            }

            if (affectsRefundCap && share > line.RefundCap) share = line.RefundCap;
            if (share < 0m) share = 0m;

            result.Add((line, share));
            allocatedSoFar += share;
        }

        return result;
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
    /// 2026-06-24: deja el rastro de auditoria OBLIGATORIO cuando el Admin saltea una barrera de doble firma
    /// (4-eyes / approval) y ejecuta la accion directo. NO reemplaza la validacion de permisos/ownership del
    /// Admin (esa es total y correcta por otro lado); solo documenta el bypass del approval para el contador.
    ///
    /// <para>El detail JSON lleva SIEMPRE <c>bypassedGate</c> (que barrera se salteo), la entidad afectada, el
    /// motivo (exigido al Admin) y, cuando aplica, monto + moneda. NUNCA datos sensibles. Mismo patron de
    /// emision que el resto de los audits del modulo (LogBusinessEventAsync hace su propio SaveChanges).</para>
    /// </summary>
    private Task LogAdminSelfAuthorizedAsync(
        string bypassedGate,
        string entityName,
        string entityId,
        string reason,
        decimal? amount,
        string? currency,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        return _auditService.LogBusinessEventAsync(
            action: AuditActions.AdminSelfAuthorized,
            entityName: entityName,
            entityId: entityId,
            details: JsonSerializer.Serialize(new
            {
                bypassedGate,
                entityName,
                entityId,
                reason,
                amount,
                currency,
                selfAuthorizedByUserId = userId,
            }),
            userId: userId,
            userName: userName,
            ct: ct);
    }

    /// <summary>
    /// CAMBIO 3 (2026-06-24): resuelve la moneda de la multa del operador para REGISTRO/auditoria. Prioridad:
    /// (1) la moneda explicita del request (ISO 4217); (2) la moneda de la primera linea del operador resuelto
    /// (cada servicio cancelado lleva la suya); (3) ARS como fallback conservador. NO cambia la moneda en la que
    /// se EMITE la ND al cliente (eso sigue como hoy): solo registra la verdad de lo que retuvo el operador.
    /// </summary>
    /// <param name="targetSupplierId">
    /// ADR-044 T1 (2026-07-10): si se informa, la primera linea se busca SOLO entre las de ese operador (evita
    /// devolver la moneda de OTRO operador en una cancelacion multi-operador). Null = comportamiento historico
    /// (primera linea del BC, sin filtrar por operador).
    /// </param>
    private async Task<string> ResolvePenaltyCurrencyForAuditAsync(
        BookingCancellation bc, string? requestedCurrency, CancellationToken ct, int? targetSupplierId = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedCurrency))
            return Monedas.Normalizar(requestedCurrency);

        var firstLineCurrency = await _db.BookingCancellationLines
            .Where(l => l.BookingCancellationId == bc.Id
                     && (targetSupplierId == null || l.SupplierId == targetSupplierId.Value))
            .OrderBy(l => l.Id)
            .Select(l => l.Currency)
            .FirstOrDefaultAsync(ct);

        return Monedas.Normalizar(firstLineCurrency);
    }

    /// <summary>
    /// CAMBIO 3 (2026-06-24): persiste la moneda de la multa del operador en la(s) <see cref="BookingCancellationLine"/>
    /// del BC. NO hace SaveChanges: lo hace el caller (ConfirmPenaltyAsync paso c). Default por linea = la moneda
    /// del servicio (<see cref="BookingCancellationLine.Currency"/>) cuando el request no trae una explicita.
    ///
    /// <para>Es SOLO registro: no toca el balance, ni el estado, ni la moneda de emision de la ND.</para>
    /// </summary>
    /// <param name="targetSupplierId">
    /// ADR-044 T1 (2026-07-10): si se informa, SOLO se tocan las lineas de ESE operador (antes de esta tanda se
    /// tocaban TODAS las lineas del BC sin importar el operador, lo que en una cancelacion multi-operador habria
    /// pisado la moneda registrada de un operador distinto al que se esta confirmando/corrigiendo). Null =
    /// comportamiento historico (todas las lineas del BC), que sigue siendo lo correcto para el 100% de los BCs
    /// mono-operador de hoy.
    /// </param>
    private async Task PersistPenaltyCurrencyOnLinesAsync(
        BookingCancellation bc, string? requestedCurrency, CancellationToken ct, int? targetSupplierId = null)
    {
        // Cargamos las lineas TRACKED (sin AsNoTracking) porque vamos a setear PenaltyCurrency y el caller
        // las persiste en su SaveChanges.
        var lines = await _db.BookingCancellationLines
            .Where(l => l.BookingCancellationId == bc.Id
                     && (targetSupplierId == null || l.SupplierId == targetSupplierId.Value))
            .ToListAsync(ct);

        if (lines.Count == 0) return; // BC sin lineas (legacy), o sin lineas de ese operador: no hay donde registrar.

        foreach (var line in lines)
        {
            // Si el request trae una moneda explicita, se aplica pareja a todas las lineas ALCANZADAS (ver
            // limitacion en el call site). Si no, cada linea conserva SU moneda como moneda de la multa.
            line.PenaltyCurrency = string.IsNullOrWhiteSpace(requestedCurrency)
                ? Monedas.Normalizar(line.Currency)
                : Monedas.Normalizar(requestedCurrency);
        }
    }

    /// <summary>
    /// FASE 0 (2026-06-28): al CONFIRMAR la penalidad del operador, baja el reembolso que ese operador debe
    /// devolver por el monto de la multa. Setea <see cref="BookingCancellationLine.PenaltyAmount"/> y recalcula
    /// <see cref="BookingCancellationLine.RefundCap"/> = capBeforePenalty − multa (nunca negativo), asi el
    /// read-model "Reembolsos a cobrar" deja de SOBREESTIMAR: pasa a mostrar pagado − multa − ya recibido.
    ///
    /// <para><b>Por que existe</b>: antes de esta fase, confirmar la multa escribia solo el escalar del BC padre
    /// (<see cref="BookingCancellation.PenaltyAmountAtEvent"/>, que alimenta la Nota de Debito al CLIENTE) y NO
    /// tocaba la(s) linea(s). El reembolso esperado del operador (<see cref="OperatorRefundReadModelService"/>,
    /// = RefundCap − recibido) seguia mostrando el monto SIN descontar la multa. Esto lo corrige.</para>
    ///
    /// <para><b>A que operador se imputa</b>: la confirmacion (sincrona o diferida) lleva UN solo monto de multa
    /// por llamada. Se imputa SOLO a las lineas del operador RESUELTO (<paramref name="targetSupplierId"/>, que
    /// por defecto es el principal del BC, <see cref="BookingCancellation.SupplierId"/>). En una cancelacion
    /// multi-operador (ADR-025), los demas operadores conservan su reembolso intacto hasta que se confirme SU
    /// propia multa con una llamada aparte (ADR-044 T1: "cada linea confirma la suya", ver
    /// <see cref="ConfirmPenaltyAsync"/>); la ND al cliente sigue siendo UNA sola por BC (T3 la desagrega por
    /// operador, ver <see cref="TryEmitCancellationDebitNoteAsync"/>).</para>
    ///
    /// <para><b>Seguridad de moneda (NUNCA mezclar ARS/USD)</b>: la multa es un solo numero en UNA moneda. Solo
    /// se netea contra las lineas cuya moneda de servicio coincide con la moneda de la multa. Si no se puede
    /// determinar UNA moneda sin ambiguedad (operador principal con servicios en varias monedas y sin
    /// <c>PenaltyCurrency</c> explicita), NO se reduce nada y se loguea — preferimos NO netear a netear cruzado.</para>
    ///
    /// <para><b>Invariante preservada</b>: como cada porcion de multa ≤ el cap de su linea, se cumple
    /// <c>RefundCap + PenaltyAmount == capBeforePenalty</c>, que es justo lo que <see cref="AssignRefundCapsAsync"/>
    /// usa para descontar el pool del operador en cancelaciones parciales sucesivas.</para>
    ///
    /// <para><b>Idempotencia</b>: corre exactamente una vez por confirmacion. La confirmacion es una transicion
    /// de una sola via (Estimated→Confirmed): la Precondicion 6 de <see cref="ConfirmPenaltyAsync"/> rebota (409)
    /// si la penalidad ya estaba confirmada, asi que el recalculo nunca se reaplica sobre un cap ya neto.</para>
    ///
    /// <para><b>ADR-044 T1 (2026-07-10)</b>: <paramref name="targetSupplierId"/> permite imputar la multa a UN
    /// operador ESPECIFICO en vez de siempre al operador principal del BC (<c>bc.SupplierId</c>) — el bug M2
    /// del rediseño de multas, donde las multas de operadores SECUNDARIOS se perdian porque este metodo estaba
    /// hardcodeado a <c>bc.SupplierId</c>. Null (default) preserva el comportamiento historico. Ademas, desde
    /// esta tanda, marca <see cref="BookingCancellationLine.PenaltyStatus"/> = <c>Confirmed</c> en las lineas de
    /// ese operador (sin importar el concepto): es lo que permite que el motor de la ND (ADR-044 T3a,
    /// <c>BuildCancellationDebitNoteItemsAsync</c>) arme un renglon por cada operador confirmado, y que el
    /// read-model muestre el paso correcto por operador.</para>
    /// </summary>
    /// <param name="conceptOverride">
    /// ADR-044 T1 (2026-07-10): concepto fiscal a usar para decidir si se netea el RefundCap (pass-through) o no
    /// (agency-owned). Null (default) = usar el del BC padre (<c>bc.ConceptKind</c>), comportamiento historico.
    /// Se pasa EXPLICITO para un operador SECUNDARIO: como su confirmacion ya NO pisa <c>bc.ConceptKind</c> (ese
    /// snapshot describe al PRINCIPAL), sin este override el neteo del secundario leeria el concepto del principal
    /// y podria netear (o no) por el motivo equivocado. Para el operador principal se pasa su concepto efectivo,
    /// que en ese punto es identico a <c>bc.ConceptKind</c> — byte-identico al comportamiento previo.
    /// </param>
    // internal (no private) para que los tests unit ejerciten el reparto directamente, igual que AssignRefundCapsAsync.
    internal async Task AllocateConfirmedPenaltyToLinesAsync(
        BookingCancellation bc,
        decimal confirmedPenaltyAmount,
        string? requestedPenaltyCurrency,
        CancellationToken ct,
        int? targetSupplierId = null,
        CancellationConceptKind? conceptOverride = null,
        // ADR-044 T2 Addendum (2026-07-10): quien confirma, para dejarlo en el cargo automatico que este metodo
        // crea por detras (BookingCancellationLineOperatorCharge). Opcional con default "System" para no romper
        // firmas de tests que llamaban este metodo antes de esta tanda sin pasar usuario.
        string userId = "System",
        string? userName = null,
        // ADR-044 T3b Decision 1 (2026-07-10): factura elegida por el usuario al CONFIRMAR la multa (viene de
        // ConfirmPenaltyRequest.TargetInvoicePublicId), para cuando la reserva tiene 2+ facturas activas y no hay
        // autocompletado posible. Opcional: default null preserva el comportamiento previo (auto-resuelve con 1
        // sola factura activa, o queda sin resolver con 2+ -> revision manual). Los call sites del path Dia-0
        // (ConfirmCancellationRequest) y de CorrectPenaltyAsync no traen este dato todavia y siguen pasando null.
        Guid? requestedTargetInvoicePublicId = null)
    {
        // Sin multa positiva no hay nada que netear (el request ya valida > 0, esto es defensivo).
        if (confirmedPenaltyAmount <= 0m) return;

        var effectiveSupplierId = targetSupplierId ?? bc.SupplierId;
        var effectiveConcept = conceptOverride ?? bc.ConceptKind;

        // Lineas TRACKED del operador resuelto (por defecto, el principal del BC — comportamiento historico).
        // Include Supplier: lo necesita el gate CommissionOnly de abajo (Decision A del Addendum T2) para el
        // fallback vivo cuando la linea todavia no tiene SupplierInvoicingModeAtEvent (lineas legacy).
        var operatorLines = await _db.BookingCancellationLines
            .Include(l => l.Supplier)
            .Where(l => l.BookingCancellationId == bc.Id && l.SupplierId == effectiveSupplierId)
            .ToListAsync(ct);

        if (operatorLines.Count == 0) return; // BC legacy sin lineas, o sin lineas de ese operador: no-op.

        // ADR-044 T1: marcar PenaltyStatus=Confirmed en TODAS las lineas de este operador, sin importar el
        // concepto (pass-through o agency-owned) — esto es lo que activa el candado multi-operador de la ND.
        // Se hace ANTES del guard de concepto de abajo (que solo decide si se netea el RefundCap) para que el
        // conteo de "operadores con multa confirmada" sea correcto tambien para conceptos agency-owned.
        if (operatorLines.Any(l => l.PenaltyStatus != PenaltyStatus.Confirmed))
        {
            foreach (var line in operatorLines)
                line.PenaltyStatus = PenaltyStatus.Confirmed;
        }

        // C2 (Pasos B/C, 2026-06-29) — fix de plata, concept-aware: la multa SOLO reduce el reembolso esperado
        // del operador cuando es una penalidad PASS-THROUGH (la retiene el operador, que devuelve NETO). Si la
        // penalidad es un cargo PROPIO de la agencia (agency-owned: AgencyManagementFee/AgencyCancellationFee/
        // seguros), el operador debe reembolsar el monto INTEGRO: ese fee es ingreso aparte de la agencia (se le
        // cobra al cliente con su propia ND), NO plata que el operador se queda. Reducir el RefundCap en ese caso
        // SUBESTIMARIA el "me tiene que devolver" del operador. Por eso, para agency-owned NO tocamos el
        // RefundCap ni PenaltyAmount de las lineas (asi la linea "Multa retenida" del circuito tampoco aparece,
        // porque su gate exige PenaltyAmount > 0 + ConceptKind pass-through). Espejo del gate de
        // OperatorRefundService:404-408 (que mira bc.ConceptKind, el padre). ADR-044 T1: usamos effectiveConcept
        // (override del operador RESUELTO) en vez de bc.ConceptKind directo, para que un operador secundario netee
        // por SU concepto y no por el del principal.
        if (effectiveConcept != CancellationConceptKind.OperatorPenaltyPassThrough)
        {
            _logger.LogInformation(
                "FASE0/C2: BC {BcPublicId} con penalidad agency-owned ({ConceptKind}): NO se reduce el RefundCap " +
                "del operador (debe reembolsar integro; el fee propio se cobra al cliente aparte).",
                bc.PublicId, effectiveConcept);
            return;
        }

        // ADR-044 T2 Addendum, Decision A (2026-07-10): gate CommissionOnly. Un operador intermediario (factura
        // DIRECTO al cliente final) estructuralmente NO tiene un RefundCap bruto que retener (SupplierService
        // nunca le genera "compra confirmada"): que este metodo le netee una multa retenida seria un cargo
        // automatico sobre un dato que no deberia existir. Se bloquea ANTES de crear ningun cargo/neteo (mismo
        // criterio que IsCommissionOnlyLiquidation del lado cliente, GR-003, aplicado aca al lado operador).
        if (AnyLineHasCommissionOnlyInvoicingMode(operatorLines))
            throw new BusinessInvariantViolationException(
                "Este operador solo cobra comisión: no retiene multas. Si te descontó algo, registralo como cargo " +
                "facturado aparte con su documento.",
                invariantCode: "INV-ADR044-T2-COMMISSIONONLY");

        // Guarda de IDEMPOTENCIA interna (hardening review 2026-06-28): si alguna linea de este operador YA
        // tiene PenaltyAmount cargado, la multa ya fue neteada en una corrida previa. Volver a netear recomputaria
        // capBeforePenalty desde un RefundCap YA reducido y restaria de nuevo, rompiendo la invariante
        // RefundCap + PenaltyAmount == capBeforePenalty. Las guardas externas (Precondicion 6 = 409 si
        // PenaltyStatus==Confirmed, la transicion de una via Drafted->Confirmed y xmin) ya impiden una segunda
        // llamada, pero hacemos el metodo seguro por si solo: llamarlo dos veces es un no-op.
        if (operatorLines.Any(l => l.PenaltyAmount.HasValue))
        {
            _logger.LogInformation(
                "FASE0: BC {BcPublicId} ya tiene la multa neteada en las lineas de este operador " +
                "(PenaltyAmount cargado). No se vuelve a netear (idempotente).",
                bc.PublicId);
            return;
        }

        // Moneda de la multa. Si no se puede determinar una sola sin ambiguedad, NO neteamos (anti cross-currency).
        var penaltyCurrency = ResolvePenaltyAllocationCurrency(operatorLines, requestedPenaltyCurrency);
        if (penaltyCurrency is null)
        {
            _logger.LogWarning(
                "FASE0: BC {BcPublicId} operador principal con servicios en varias monedas y sin PenaltyCurrency " +
                "explicita: no se puede netear la multa sin elegir moneda. NO se reduce el RefundCap (evita netear " +
                "cruzado ARS/USD). metric:operator_refund_penalty_currency_ambiguous",
                bc.PublicId);
            return;
        }

        // Solo las lineas cuya moneda de servicio == moneda de la multa (el reembolso esperado vive por moneda).
        var candidateLines = operatorLines
            .Where(l => string.Equals(
                Monedas.Normalizar(l.Currency), penaltyCurrency, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // capBeforePenalty de cada linea == su RefundCap actual (al confirmar, la multa todavia era null: nada la
        // habia descontado). Por eso usamos RefundCap como base del reparto.
        decimal totalCapBeforePenalty = candidateLines.Sum(l => l.RefundCap);
        if (totalCapBeforePenalty <= 0m)
        {
            // El operador no tenia reembolso esperado en esa moneda (no se le pago, o ya era 0). La multa no puede
            // bajar el reembolso por debajo de cero; no seteamos PenaltyAmount para preservar la invariante
            // RefundCap + PenaltyAmount == capBeforePenalty (que aca es 0 + 0 == 0).
            return;
        }

        // La multa neteada nunca supera lo pagado: si la multa > lo pagado, el reembolso cae a 0 (el operador no
        // devuelve menos que cero). El excedente NO genera "deuda del cliente hacia el operador" en este read-model.
        // La ND al cliente sigue usando el monto COMPLETO (bc.PenaltyAmountAtEvent), que NO se toca aca.
        decimal penaltyToApply = Math.Min(confirmedPenaltyAmount, totalCapBeforePenalty);

        // ADR-044 T3b Decision 1 (2026-07-10): a que factura de venta se traslada el cargo automatico que este
        // metodo crea mas abajo. Con 1 sola factura activa de la reserva (el 95%+ de los casos) se autocompleta
        // transparente, cero friccion. Con 2+ facturas activas queda en null: recien se resuelve mas adelante
        // via SetOperatorChargeTargetInvoiceAsync (la pantalla que elige es ADR-044 T4) — el motor de emision de
        // la ND rutea a revision manual mientras tanto (nunca adivina a que factura corresponde).
        var activeInvoicesForAutoTarget = await LoadActiveSaleInvoicesForReservaAsync(bc.ReservaId, ct);
        var autoTargetInvoiceId = ResolveAutoTargetInvoiceId(activeInvoicesForAutoTarget);

        // ADR-044 T3b Decision 1 (2026-07-10): con 2+ facturas activas el autocompletado de arriba da null. Si el
        // usuario ELIGIO una factura al confirmar la multa (ConfirmPenaltyRequest.TargetInvoicePublicId), la
        // validamos contra las facturas ACTIVAS de la reserva — misma validacion de membresia (mismo mensaje e
        // invariantCode) que ya usan AddOperatorChargeAsync y SetOperatorChargeTargetInvoiceAsync: nunca se acepta
        // a ciegas una factura ajena o ya muerta.
        if (autoTargetInvoiceId is null && requestedTargetInvoicePublicId.HasValue)
        {
            var chosenInvoice = activeInvoicesForAutoTarget
                .FirstOrDefault(i => i.PublicId == requestedTargetInvoicePublicId.Value);
            if (chosenInvoice is null)
                throw new BusinessInvariantViolationException(
                    "La factura elegida no es una factura de venta activa de esta reserva.",
                    invariantCode: "INV-ADR044-TARGETINVOICE-001");
            autoTargetInvoiceId = chosenInvoice.Id;
        }

        decimal allocatedSoFar = 0m;
        for (int i = 0; i < candidateLines.Count; i++)
        {
            var line = candidateLines[i];
            bool isLastLine = i == candidateLines.Count - 1;

            // Reparto proporcional al cap de cada linea. La ultima linea absorbe el residuo de redondeo para que
            // la suma de las porciones == penaltyToApply exacto.
            decimal share = isLastLine
                ? penaltyToApply - allocatedSoFar
                : Math.Round(
                    penaltyToApply * (line.RefundCap / totalCapBeforePenalty), 2, MidpointRounding.AwayFromZero);

            // Defensa dura: la porcion nunca supera el cap de la linea (preserva RefundCap + PenaltyAmount ==
            // capBeforePenalty) ni baja de cero.
            if (share > line.RefundCap) share = line.RefundCap;
            if (share < 0m) share = 0m;

            line.PenaltyAmount = share;
            line.RefundCap = line.RefundCap - share;

            // ADR-044 T2 Addendum (Decision B, 2026-07-10): el caso simple sigue siendo "2 clics" — el usuario
            // solo informa monto+moneda+concepto, y ACA ATRAS se crea UN cargo tipificado por detras
            // (Kind=AdministrativeFee, CollectionMode=Retenida, transparente para el usuario). Como es Fee+
            // Retenida, el eje CAJA (RetainedDeductionAmount) coincide EXACTO con el eje CLIENTE (PenaltyAmount)
            // para este camino automatico — la divergencia entre los dos solo aparece cuando se agrega un cargo
            // SECUNDARIO distinto (Tax/Withholding/FacturadaAparte) via el endpoint "agregar otro cargo".
            // Se crea SOLO si queda algo para registrar (share > 0): una linea con porcion 0 por redondeo/reparto
            // no tiene nada que documentar como cargo real.
            if (share > 0m)
            {
                _db.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
                {
                    BookingCancellationLine = line,
                    Kind = OperatorChargeKind.AdministrativeFee,
                    CollectionMode = PenaltyCollectionMode.Retenida,
                    Amount = share,
                    Currency = Monedas.Normalizar(line.Currency),
                    ConfirmedByUserId = userId,
                    ConfirmedByUserName = userName,
                    ConfirmedAt = DateTime.UtcNow,
                    TargetInvoiceId = autoTargetInvoiceId,
                });
                line.RetainedDeductionAmount = share;
            }

            // FIX D (2026-07-04): si la multa confirmada del operador se comio TODO el cap de la linea, ya no hay
            // reembolso pendiente por esa via -> None (coherente con el doc del enum). Si queda cap, sigue esperando
            // reembolso (PendingOperatorRefund, seteado al nacer el circuito en AssignRefundCapsAsync).
            if (line.RefundCap <= 0m)
                line.RefundStatus = BookingCancellationLineRefundStatus.None;

            allocatedSoFar += share;
        }
    }

    /// <summary>
    /// ADR-044 T2 Addendum, Decision A (2026-07-10): ¿ALGUNA de estas lineas resuelve a un operador
    /// <see cref="SupplierInvoicingMode.CommissionOnly"/> (intermediario)? Usa el fallback vivo
    /// <c>line.SupplierInvoicingModeAtEvent ?? line.Supplier.InvoicingMode</c> — idem
    /// <c>FiscalLiquidationCalculator.cs:61</c>. Se pregunta "alguna" (no "todas") para ser conservador: todas
    /// las lineas de un mismo <c>SupplierId</c> deberian resolver igual (mismo operador), pero si alguna
    /// divergiera por dato historico raro, se bloquea igual antes que netear mal.
    /// </summary>
    private static bool AnyLineHasCommissionOnlyInvoicingMode(IEnumerable<BookingCancellationLine> lines)
        => lines.Any(l =>
            (l.SupplierInvoicingModeAtEvent ?? l.Supplier?.InvoicingMode)
                == SupplierInvoicingMode.CommissionOnly);

    /// <summary>
    /// Detalle de un tope de reembolso restaurado al cerrar sin multa una penalidad confirmada (para el audit):
    /// que linea, cuanta multa se le devolvio al reembolso, y el cap resultante (viejo -> nuevo).
    /// </summary>
    internal sealed record PenaltyCapRestore(int LineId, decimal RestoredPenalty, decimal OldRefundCap, decimal NewRefundCap);

    /// <summary>
    /// ADR-044 T2 Addendum (menor 1, 2026-07-10): foto de un cargo de operador BORRADO al deshacer la
    /// confirmacion (para el detail JSON del audit del waive/correct, patron <c>previousDebitNoteStatus</c>).
    /// Deja la historia autocontenida: "esta reversa borro estos cargos" sin correlacionar con otros registros.
    /// </summary>
    internal sealed record DeletedOperatorChargeSnapshot(
        int LineId, string Kind, string CollectionMode, decimal Amount, string Currency, string? DocumentRef);

    /// <summary>
    /// ESPEJO de <see cref="AllocateConfirmedPenaltyToLinesAsync"/>: deshace la imputacion de la multa a las lineas
    /// del operador resuelto cuando se cierra sin multa una penalidad YA confirmada (fix "multa fantasma") o se va
    /// a corregir su monto/moneda. Por cada linea de ese operador con <c>PenaltyAmount</c> seteado: si la porcion
    /// era positiva le devuelve ese monto al <c>RefundCap</c> (RefundCap += PenaltyAmount); y en TODAS sus lineas
    /// (tenga o no <c>PenaltyAmount</c>) resetea <see cref="BookingCancellationLine.PenaltyStatus"/> a
    /// <c>Estimated</c> — el espejo exacto de lo que <see cref="AllocateConfirmedPenaltyToLinesAsync"/> confirma
    /// (ADR-044 T1). Asi "Reembolsos a cobrar" vuelve a mostrar el monto integro que el operador debe devolver
    /// (ya no retiene multa) Y el candado multi-operador de la ND deja de contar a este operador como confirmado.
    ///
    /// <para><b>Concepto agency-owned</b>: el Allocate NO habia reducido el <c>RefundCap</c> (el operador debia
    /// reembolsar integro), asi que aca no hay cap que restaurar — pero el <c>PenaltyStatus</c> SI se resetea
    /// igual (el Allocate lo confirma sin importar el concepto desde esta tanda).</para>
    ///
    /// <para><b>Por que <c>PenaltyAmount = null</c> y no 0, para TODA linea con valor (incluido un 0 residual)</b>:
    /// null es el estado canonico "sin penalidad" de la linea (su default). Ademas es lo que la guarda de idempotencia
    /// del Allocate (<c>operatorLines.Any(l =&gt; l.PenaltyAmount.HasValue)</c>) espera: si dejaramos un 0 (HasValue
    /// == true), un re-confirm posterior (revert-waive -&gt; Estimated -&gt; confirmar de nuevo) creeria que la multa
    /// "ya esta neteada" y NO volveria a reducir el cap -&gt; reembolso sobreestimado. Por eso el reset a null se hace
    /// aunque la porcion fuera 0 (y esa linea 0 no suma nada al cap ni entra al audit). La spec pedia
    /// "PenaltyAmount = 0"; se usa null a proposito por esta interaccion con el Allocate.</para>
    ///
    /// <para>NO hace <c>SaveChanges</c>: corre dentro de la unidad de trabajo del waive/correct (commit unico).</para>
    /// </summary>
    /// <param name="targetSupplierId">
    /// ADR-044 T1 (2026-07-10): operador cuyas lineas se revierten. Null (default) preserva el comportamiento
    /// historico (operador principal del BC, <c>bc.SupplierId</c>).
    /// </param>
    /// <param name="deletedChargesSink">
    /// ADR-044 T2 Addendum (menor 1, 2026-07-10): si el caller lo provee, se llena con la foto de los cargos
    /// BORRADOS (para el detail JSON de su audit). Null = el caller no la necesita (ej. tests). No cambia la
    /// logica de restauracion; es solo un canal de salida opcional para no cambiar el tipo de retorno.
    /// </param>
    // internal (no private) para que los tests unit ejerciten la restauracion directamente, igual que Allocate.
    internal async Task<List<PenaltyCapRestore>> ReverseConfirmedPenaltyFromLinesAsync(
        BookingCancellation bc, CancellationToken ct, int? targetSupplierId = null,
        List<DeletedOperatorChargeSnapshot>? deletedChargesSink = null)
    {
        var restored = new List<PenaltyCapRestore>();
        var effectiveSupplierId = targetSupplierId ?? bc.SupplierId;

        // ADR-044 T2 Addendum (2026-07-10): Include OperatorCharges — este metodo los BORRA (ver abajo). Es el
        // espejo COMPLETO del Allocate: deshacer una confirmacion deshace TAMBIEN los cargos que esa confirmacion
        // creo (el automatico Fee+Retenida Y cualquier cargo secundario agregado despues — Tax/Withholding/
        // FacturadaAparte), no solo el escalar. Decision documentada: un "deshacer" es total, no parcial; si
        // quedara un cargo secundario huerfano (de un operador ya "Estimated" de nuevo), el usuario lo reconstruye
        // al reconfirmar, evitando el riesgo mayor de un cargo fantasma que ningun escalar referencia mas.
        var operatorLines = await _db.BookingCancellationLines
            .Include(l => l.OperatorCharges)
            .Where(l => l.BookingCancellationId == bc.Id && l.SupplierId == effectiveSupplierId)
            .ToListAsync(ct);

        foreach (var line in operatorLines)
        {
            // ADR-044 T1: espejo del Allocate — SIEMPRE volvemos la linea a "pendiente de decidir", tenga o no
            // una porcion de multa neteada (para agency-owned nunca la tuvo, pero el Allocate SI la habia
            // marcado Confirmed). Sin este reset, el candado multi-operador de la ND seguiria contando a este
            // operador como confirmado despues de deshacer su confirmacion.
            line.PenaltyStatus = PenaltyStatus.Estimated;

            // ADR-044 T3a (2026-07-10): limpiar el marcador de "nota de debito complementaria a mano" si esta
            // linea lo tenia (caso b de la confirmacion escalonada). Deshacer la confirmacion borra ese estado:
            // la linea vuelve a Estimated y, si se reconfirma, entrara de nuevo por el flujo normal.
            if (line.DebitNoteStatus == DebitNoteStatus.ManualReview)
            {
                line.DebitNoteStatus = DebitNoteStatus.NotApplicable;
                line.DebitNoteArcaErrorMessage = null;
            }

            // ADR-044 T2 Addendum (Decision B1, 2026-07-10): borrar los cargos de esta linea (si los hay) ANTES de
            // decidir si hay que restaurar el cap — son ellos los que originaron RetainedDeductionAmount. Menor 1:
            // antes de borrarlos, snapshotearlos para el audit del caller (si lo pidio).
            if (line.OperatorCharges.Count > 0)
            {
                if (deletedChargesSink is not null)
                {
                    foreach (var charge in line.OperatorCharges)
                        deletedChargesSink.Add(new DeletedOperatorChargeSnapshot(
                            LineId: line.Id,
                            Kind: charge.Kind.ToString(),
                            CollectionMode: charge.CollectionMode.ToString(),
                            Amount: charge.Amount,
                            Currency: Monedas.Normalizar(charge.Currency),
                            DocumentRef: charge.DocumentRef));
                }
                _db.BookingCancellationLineOperatorCharges.RemoveRange(line.OperatorCharges);
            }

            // Solo restauramos el CAP de lineas que el Allocate haya marcado con una porcion RETENIDA
            // (RetainedDeductionAmount > 0 — el eje CAJA). Una linea con RetainedDeductionAmount 0 nunca redujo
            // el cap (agency-owned SIEMPRE cae aca, y tambien una linea que solo tuviera cargos Withholding/
            // FacturadaAparte, que por diseño no restan RefundCap).
            var restoredPenalty = line.RetainedDeductionAmount;
            if (restoredPenalty <= 0m)
            {
                // Igual hay que limpiar el eje CLIENTE si quedo cargado (agency-owned nunca lo carga, pero una
                // linea con solo Withholding/FacturadaAparte SI tiene PenaltyAmount > 0 sin haber tocado el cap).
                line.PenaltyAmount = null;
                line.RetainedDeductionAmount = 0m;
                continue;
            }

            var oldCap = line.RefundCap;

            // El operador ya no retiene la multa: debe reembolsar el monto integro otra vez.
            line.RefundCap = oldCap + restoredPenalty;

            // El circuito de reembolso del operador vuelve a esperar la plata (si aun no se recibio todo). Si el
            // Allocate lo habia dejado en None por cap 0, aca revive; si ya se cobro todo, Settled.
            if (line.RefundCap > 0m)
            {
                line.RefundStatus = line.ReceivedRefundAmount >= line.RefundCap
                    ? BookingCancellationLineRefundStatus.Settled
                    : BookingCancellationLineRefundStatus.PendingOperatorRefund;
            }

            restored.Add(new PenaltyCapRestore(line.Id, restoredPenalty, oldCap, line.RefundCap));

            // SIEMPRE resetear a null/0, es clave para el re-neteo futuro: la guarda de idempotencia del Allocate
            // es operatorLines.Any(l => l.PenaltyAmount.HasValue). Si dejaramos un valor residual, un re-confirm
            // posterior (revert-waive -> Estimated -> confirmar de nuevo) creeria que la multa "ya esta neteada"
            // y NO volveria a reducir el cap -> reembolso sobreestimado.
            line.PenaltyAmount = null;
            line.RetainedDeductionAmount = 0m;
        }

        return restored;
    }

    /// <summary>
    /// Determina la moneda en la que se netea la multa contra el reembolso del operador. Si el request trae
    /// <c>requestedPenaltyCurrency</c>, esa manda. Si no, se infiere de las lineas del operador SOLO si comparten
    /// una unica moneda; si el operador tiene servicios en VARIAS monedas y no se informo moneda, devuelve null
    /// (no se puede elegir sin ambiguedad y no se debe netear cruzado).
    /// </summary>
    private static string? ResolvePenaltyAllocationCurrency(
        List<BookingCancellationLine> operatorLines, string? requestedPenaltyCurrency)
    {
        if (!string.IsNullOrWhiteSpace(requestedPenaltyCurrency))
            return Monedas.Normalizar(requestedPenaltyCurrency);

        var distinctCurrencies = operatorLines
            .Select(l => Monedas.Normalizar(l.Currency))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinctCurrencies.Count == 1 ? distinctCurrencies[0] : null;
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
    /// <param name="actorUserId">
    /// (A3, review 2026-07-08) Actor REAL que dispara la emision (el que apreto "Confirmar multa" o "Reintentar"),
    /// para atribuir bien la ND y su auditoria. Antes se atribuia SIEMPRE a <c>bc.ConfirmedByUserId</c> (quien
    /// confirmo la ANULACION, no quien emite la ND) — el reviewer de seguridad lo marco como atribucion incorrecta.
    /// null (callbacks async de ARCA, sin usuario) -> cae al fallback historico <c>bc.ConfirmedByUserId</c>.
    /// </param>
    /// <param name="actorUserName">Nombre del actor real, par de <paramref name="actorUserId"/>.</param>
    private async Task TryEmitCancellationDebitNoteAsync(
        BookingCancellation bc, CancellationToken ct, string? actorUserId = null, string? actorUserName = null)
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

        // (1-ter) ADR-044 T3a (2026-07-10): el candado "ARREGLO 2" (2026-06-24) que mandaba a revision manual
        // CUALQUIER multi-operador confirmado se REEMPLAZA por la emision real multi-operador (ver el paso (3-ter)
        // mas abajo, DESPUES del gating): ahora arma UNA ND con un renglon por cargo del operador, siempre que
        // sea 1 sola factura activa y todos los cargos esten en la misma moneda que esa factura. El caso que
        // sigue yendo a revision manual (2+ facturas activas, cruce de moneda, o multi-operador legacy SIN
        // cargos tipificados) se sigue evaluando ahi, con su propio motivo especifico.

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
        //     El flag EnableMultiCurrencyInvoicing habilita la moneda extranjera dentro del gating (los guards
        //     de TC/moneda soportada viven adentro, mismo criterio que la NC total). Con OFF, la factura no-ARS
        //     vuelve a revision manual.
        var manualReason = EvaluateDebitNoteGating(
            bc, originatingInvoice, multiCurrencyInvoicingEnabled: settings.EnableMultiCurrencyInvoicing);
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

        // (3-bis) BUG HISTORICO (desde el commit original de ADR-013, encontrado en produccion
        //     2026-07-08): CreateInvoiceRequest.ReservaId espera el PublicId (GUID) de la reserva,
        //     porque EntityReferenceResolver.ResolveRequiredIdAsync SOLO sabe parsear un GUID
        //     (adentro usa Guid.TryParse; si no matchea, devuelve null y el resolvedor tira
        //     KeyNotFoundException). Aca se estaba mandando el Id INTERNO (un int, ej. "31") como
        //     si fuera el PublicId. El resolvedor jamas lo entendio: TODA emision automatica de
        //     ND por multa terminaba en KeyNotFoundException -> catch -> ManualReview, sin
        //     excepcion, desde que existe esta feature. Por eso la ficha quedaba con "Corregir
        //     monto y moneda" para siempre aunque todo estuviera bien cargado.
        //
        //     Fix: resolvemos el PublicId REAL de la reserva contra la base antes de armar el
        //     request. Si por algun motivo no lo encontramos (no deberia pasar: la factura ya
        //     tiene ReservaId cargado), no explotamos: ruteamos a revision manual con un motivo
        //     claro, igual que el resto de los guards de esta funcion.
        var reservaPublicId = await _db.Reservas
            .AsNoTracking()
            .Where(r => r.Id == originatingInvoice.ReservaId!.Value)
            .Select(r => (Guid?)r.PublicId)
            .FirstOrDefaultAsync(ct);
        if (reservaPublicId is null)
        {
            _logger.LogError(
                "ADR-013: BC {BcPublicId} no se pudo resolver el PublicId de la reserva {ReservaId} " +
                "para emitir la ND. Rutea a revision manual.",
                bc.PublicId, originatingInvoice.ReservaId);
            await RouteDebitNoteToManualReviewAsync(
                bc, "No se pudo resolver la reserva asociada para emitir la Nota de Debito.", ct);
            return;
        }

        // (3-ter) ADR-044 T3a (2026-07-10): arma los renglones de la ND. Reemplaza el candado "ARREGLO 2":
        // en vez de mandar a revision manual CUALQUIER multi-operador confirmado, arma la ND real con un
        // renglon por cargo cuando el caso lo permite (1 factura activa, cargos en la misma moneda que ella).
        // Sigue yendo a revision manual (con un motivo especifico y legible) cuando: hay 2+ facturas activas
        // (T3b, todavia no construido); algun cargo elegible esta en una moneda distinta a la de la factura
        // (idem, cruce de moneda es T3b); o el emisor es Responsable Inscripto y todavia no hay un valor
        // confirmado de alicuota para la porcion pass-through (ver OperationalFinanceSettings).
        var buildResult = await BuildCancellationDebitNoteItemsAsync(bc, originatingInvoice, settings, ct);
        if (buildResult.ManualReviewReason is not null)
        {
            _logger.LogInformation(
                "ADR-044 T3a: BC {BcPublicId} NO califica para ND automatica multi-operador ({Reason}). " +
                "Rutea a revision manual.",
                bc.PublicId, buildResult.ManualReviewReason);
            await RouteDebitNoteToManualReviewAsync(bc, buildResult.ManualReviewReason, ct);
            return;
        }
        if (buildResult.NothingToBill)
        {
            // La agencia absorbio TODOS los cargos elegibles (nada que trasladarle al cliente): no hay ND que
            // emitir. Estado final NotApplicable (no es un error ni queda pendiente de nada).
            bc.DebitNoteStatus = DebitNoteStatus.NotApplicable;
            await _auditService.LogBusinessEventAsync(
                action: AuditActions.BookingCancellationArcaSucceeded,
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    bc.PublicId,
                    debitNoteAction = "debit-note-absorbed-nothing-to-bill",
                }),
                userId: actorUserId ?? bc.ConfirmedByUserId ?? bc.DraftedByUserId,
                userName: actorUserName ?? bc.ConfirmedByUserName ?? bc.DraftedByUserName,
                ct: ct);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "metric:cancellation_debit_note_absorbed | BcPublicId={BcPublicId}", bc.PublicId);
            return;
        }

        // (4) Construir el request de la ND y emitir por el pipeline existente.
        //     - IsDebitNote=true + OriginalInvoiceId=factura RESUELTA -> el pipeline arma el <CbtesAsoc> y
        //       deriva CbteTipo=12 (ND C) con el fix M1 (§3.9). ADR-044 T3b Decision 1: la factura resuelta NO
        //       siempre es bc.OriginatingInvoice — con 2+ facturas activas puede ser la que eligieron los
        //       cargos via TargetInvoiceId (BuildCancellationDebitNoteItemsAsync ya la valido — B2).
        //     - Los renglones (1 o mas) los arma BuildCancellationDebitNoteItemsAsync. El total (sum de los
        //       renglones) es INDEPENDIENTE del refund (no participa de ninguna suma del refund).
        var penaltyAmount = buildResult.TotalAmount;
        var ndTargetInvoice = buildResult.ResolvedInvoice ?? originatingInvoice;
        var debitNoteRequest = new CreateInvoiceRequest
        {
            ReservaId = reservaPublicId.Value.ToString(),
            Concepto = 3, // Productos y Servicios (mismo default que la NC total).
            OriginalInvoiceId = ndTargetInvoice.PublicId.ToString(),
            IsCreditNote = false,
            IsDebitNote = true,
            Items = buildResult.Items!,
            Tributes = new List<InvoiceTributeDto>(),
            // ADR-012/013 (multimoneda ND, 2026-07-08): la ND HEREDA la moneda y el TC CONGELADOS de la
            // factura RESUELTA (regla firmada, ADR-012 §3.3, INAMOVIBLE: el TC del comprobante SIEMPRE es el
            // congelado del original que ajusta — nunca se recotiza, ni siquiera en el caso 2+ facturas). Para
            // una factura en pesos esto copia los defaults ("PES"/1) -> byte-identico al comportamiento de
            // siempre. Para una factura extranjera (ej. USD) copia "DOL" + el TC real, que el gating ya valido
            // como coherente (>0 y !=1) y como moneda soportada.
            //
            // CanMisMonExt (RG 5616): NO se setea aca ni se toca AfipService. CreatePendingInvoice congela
            // CanMisMonExt ESPEJANDO el valor de la factura original (es una ND: tiene OriginalInvoiceId), asi
            // la ND replica exactamente el "N"/"S"/null del comprobante asociado — mismo mecanismo que la NC.
            MonId = ndTargetInvoice.MonId,
            MonCotiz = ndTargetInvoice.MonCotiz,
            // Trazabilidad del TC: la ND pasa por CreateAsync -> ValidateMultiCurrencyInvoicingAsync, que para
            // moneda extranjera EXIGE fuente/fecha/justificacion del tipo de cambio (patron INV-120). Las
            // HEREDAMOS del comprobante RESUELTO (que ya las tenia obligatoriamente al emitirse en divisa), asi
            // la ND queda valuada con EXACTAMENTE la misma trazabilidad que la factura que ajusta. Para pesos
            // el original las tiene en null y la validacion ni las mira. Si por un dato legacy faltaran, la
            // validacion frena y el blindaje del caller rutea a revision manual (nunca un comprobante mal valuado).
            ExchangeRateSource = ndTargetInvoice.ExchangeRateSource,
            ExchangeRateFetchedAt = ndTargetInvoice.ExchangeRateFetchedAt,
            // N1 (review 2026-07-08): dejamos explicito en la justificacion que este TC es HEREDADO del
            // comprobante original (no recotizado), asi el contador ve de un vistazo por que la ND lleva ese TC.
            ExchangeRateJustification = string.IsNullOrWhiteSpace(ndTargetInvoice.ExchangeRateJustification)
                ? ndTargetInvoice.ExchangeRateJustification
                : $"ND por multa de anulación s/ comprobante original — TC heredado: {ndTargetInvoice.ExchangeRateJustification}",
        };

        // Emitir via el pipeline existente (CreateAsync -> CreatePendingInvoice +
        // ProcessInvoiceJob async). Reusamos toda la infra de emision/idempotencia/CAE.
        // A3 (2026-07-08): atribuimos la ND al ACTOR REAL que la dispara (confirmar / reintentar). Fallback al
        // confirmador de la anulacion solo para los callbacks async sin usuario (actorUserId == null).
        var emissionUserId = actorUserId ?? bc.ConfirmedByUserId;
        var emissionUserName = actorUserName ?? bc.ConfirmedByUserName;
        var debitNoteDto = await _invoiceService.CreateAsync(
            debitNoteRequest, emissionUserId, emissionUserName, ct);

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
        // A7 (2026-07-08): al re-encolar la ND, limpiamos el motivo de rechazo VIEJO de ARCA que hubiera quedado de
        // un intento fallido anterior. Sin esto, una ND que ahora vuelve a Pending seguiria arrastrando el texto de
        // error de la vez que fallo (dato viejo enganoso). El crudo ya quedo en el log/auditoria de aquella falla.
        bc.DebitNoteArcaErrorMessage = null;
        FreezeDebitNoteSnapshot(bc, ndTargetInvoice, penaltyAmount);

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
            // A3 (2026-07-08): auditoria atribuida al actor real (confirmar/reintentar); fallback al confirmador/
            // drafter de la anulacion solo para los callbacks async sin usuario.
            userId: actorUserId ?? bc.ConfirmedByUserId ?? bc.DraftedByUserId,
            userName: actorUserName ?? bc.ConfirmedByUserName ?? bc.DraftedByUserName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "metric:cancellation_debit_note_enqueued | BcPublicId={BcPublicId} DebitNoteInvoiceId={DebitNoteId} Penalty={Penalty}",
            bc.PublicId, debitNoteId.Value, penaltyAmount);
    }

    // =========================================================================
    // ADR-044 T3a (2026-07-10): renglones de la ND multi-operador (reemplaza "ARREGLO 2").
    // =========================================================================

    /// <summary>
    /// ADR-044 T3a/T3b: resultado de armar los renglones de la ND. Exactamente UNO de los 3 casos: (a)
    /// <see cref="Items"/> con contenido -> se puede emitir con ese total contra <see cref="ResolvedInvoice"/>;
    /// (b) <see cref="ManualReviewReason"/> con motivo -> revision manual (NO emitir); (c)
    /// <see cref="NothingToBill"/>=true -> la agencia absorbio todos los cargos elegibles, no hay nada que
    /// facturarle al cliente (no es un error).
    /// </summary>
    internal sealed record CancellationDebitNoteItemsResult(
        List<InvoiceItemDto>? Items, decimal TotalAmount, string? ManualReviewReason, bool NothingToBill,
        // ADR-044 T3b Decision 1: la factura CONTRA LA QUE se emite la ND. Con 1 sola factura activa (T3a) es
        // SIEMPRE bc.OriginatingInvoice; con 2+ facturas activas puede ser OTRA (la que eligieron los cargos via
        // TargetInvoiceId). El caller (TryEmitCancellationDebitNoteAsync) arma el CreateInvoiceRequest contra
        // ESTA factura, nunca a ciegas contra bc.OriginatingInvoice.
        Invoice? ResolvedInvoice = null)
    {
        public static CancellationDebitNoteItemsResult Manual(string reason) =>
            new(Items: null, TotalAmount: 0m, ManualReviewReason: reason, NothingToBill: false);

        public static CancellationDebitNoteItemsResult Absorbed() =>
            new(Items: null, TotalAmount: 0m, ManualReviewReason: null, NothingToBill: true);

        public static CancellationDebitNoteItemsResult Ready(List<InvoiceItemDto> items, decimal total, Invoice resolvedInvoice) =>
            new(items, total, ManualReviewReason: null, NothingToBill: false, ResolvedInvoice: resolvedInvoice);
    }

    /// <summary>
    /// ADR-044 T3a: arma los renglones (<see cref="InvoiceItemDto"/>) de la Nota de Debito por la multa de
    /// cancelacion. Reemplaza el candado "ARREGLO 2" (2026-06-24, que mandaba a revision manual CUALQUIER
    /// multi-operador confirmado) por la emision real: un renglon por CARGO tipificado del operador
    /// (<see cref="BookingCancellationLineOperatorCharge"/>), sea de uno o de varios operadores, siempre que la
    /// cancelacion afecte UNA sola factura activa y todos los cargos elegibles esten en la misma moneda que esa
    /// factura.
    ///
    /// <para><b>2 caminos, elegidos por si el BC ya tiene cargos tipificados (ADR-044 T2)</b>:
    /// <list type="number">
    /// <item><b>Concepto propio de la agencia</b> (<see cref="ConceptIsAgencyOwnedDebitNote"/>): la agencia
    /// nunca crea cargos del operador para este concepto (<c>AllocateConfirmedPenaltyToLinesAsync</c> corta
    /// antes). Se arma el UNICO renglon de siempre (<c>bc.PenaltyAmountAtEvent</c>, AlicuotaIvaId=3),
    /// BYTE-IDENTICO al comportamiento previo a esta tanda.</item>
    /// <item><b>Pass-through sin cargos (legacy)</b>: BC confirmado antes de ADR-044 T2, o cuyo cap del
    /// operador ya estaba en 0 al confirmar (el auto-cargo solo se crea si hay algo que retener). Mismo renglon
    /// unico de siempre. El unico guard nuevo: si hay 2+ operadores con multa CONFIRMADA y NINGUNO tiene cargos,
    /// sigue yendo a revision manual (mismo motivo que el "ARREGLO 2" original: sin cargos no hay de donde armar
    /// el desglose por operador).</item>
    /// <item><b>Pass-through con cargos</b>: UN renglon por cargo ELEGIBLE (<c>Kind != Withholding AND
    /// ClientTransferMode != Absorbed</c>) de TODAS las lineas confirmadas del BC (de cualquier operador), mas
    /// un renglon aparte por cada fee de gestion (<c>ClientTransferMode = WithManagementFee</c>).</item>
    /// </list></para>
    ///
    /// <para><b>Cuando rutea a revision manual (T3b, fuera de esta tanda)</b>: 2+ facturas activas en la
    /// cancelacion (ADR-042); algun cargo elegible en una moneda distinta a la de la factura (cruce de moneda);
    /// el emisor es Responsable Inscripto y todavia no hay una alicuota de IVA confirmada para la porcion
    /// pass-through (<c>OperationalFinanceSettings.CancellationDebitNoteRiPassThroughAlicuotaIvaId</c> en null);
    /// el total de los renglones supera el total de la factura (M2, espejo del gating de un solo operador); o el
    /// BC combina un concepto propio de la agencia con cargos de OTRO operador (mezcla que esta tanda no
    /// resuelve).</para>
    /// </summary>
    private async Task<CancellationDebitNoteItemsResult> BuildCancellationDebitNoteItemsAsync(
        BookingCancellation bc, Invoice originatingInvoice, OperationalFinanceSettings settings, CancellationToken ct)
    {
        if (ConceptIsAgencyOwnedDebitNote(bc.ConceptKind))
        {
            // El renglon de siempre vive en bc.PenaltyAmountAtEvent (sin cargos: Allocate nunca crea uno para
            // este concepto). Si ADEMAS otro operador de la MISMA cancelacion tiene SU PROPIA multa confirmada
            // (con o sin cargos tipificados: un BC legacy puede tener la linea confirmada sin haber pasado nunca
            // por Allocate), es una mezcla que esta tanda no resuelve: ante la duda, revision manual (no se
            // inventa un renglon hibrido). Mismo espiritu que el "ARREGLO 2" original, ahora acotado a este caso.
            var anyOtherSupplierConfirmed = await _db.BookingCancellationLines
                .AnyAsync(l => l.BookingCancellationId == bc.Id
                            && l.SupplierId != bc.SupplierId
                            && l.PenaltyStatus == PenaltyStatus.Confirmed, ct);
            if (anyOtherSupplierConfirmed)
                return CancellationDebitNoteItemsResult.Manual(
                    "La cancelación combina un cargo propio de la agencia con la multa de otro operador: por " +
                    "ahora esto se resuelve a mano.");

            return LegacySingleItem(bc, originatingInvoice);
        }

        // A partir de aca, bc.ConceptKind == OperatorPenaltyPassThrough: es el UNICO otro valor que emite ND
        // (ver ConceptEmitsDebitNote; el gating de arriba ya descarto los conceptos de seguro).
        var lines = await _db.BookingCancellationLines
            .Include(l => l.OperatorCharges)
            .Include(l => l.Supplier)
            .Where(l => l.BookingCancellationId == bc.Id)
            .ToListAsync(ct);

        var confirmedLines = lines.Where(l => l.PenaltyStatus == PenaltyStatus.Confirmed).ToList();
        var allCharges = confirmedLines
            .SelectMany(line => line.OperatorCharges.Select(charge => (Line: line, Charge: charge)))
            .ToList();

        if (allCharges.Count == 0)
        {
            // Legacy: ningun cargo tipificado (BC confirmado antes de ADR-044 T2, o cap del operador ya en 0 al
            // confirmar). Si hay 2+ operadores con multa confirmada, no hay de donde armar el desglose -> mismo
            // motivo que el "ARREGLO 2" original.
            var confirmedSupplierCount = confirmedLines.Select(l => l.SupplierId).Distinct().Count();
            if (confirmedSupplierCount > 1)
                return CancellationDebitNoteItemsResult.Manual(
                    $"La cancelación tiene multas confirmadas de {confirmedSupplierCount} operadores distintos " +
                    "y no hay el desglose de cargos necesario para armar la Nota de Débito automática. Se " +
                    "confirma y emite manualmente por ahora.");

            return LegacySingleItem(bc, originatingInvoice);
        }

        // ADR-044 T3b Decision 1 (2026-07-10): con 2+ facturas de venta activas (ADR-042), la ND ya NO se rutea
        // automaticamente a revision manual: se resuelve a que factura corresponden los cargos ELEGIBLES
        // (Kind != Withholding, no absorbidos — los unicos que producen un renglon) mirando su
        // TargetInvoiceId compartido. Con 1 sola factura activa, sigue siendo bc.OriginatingInvoice (T3a,
        // byte-identico).
        var activeInvoices = await LoadActiveSaleInvoicesForReservaAsync(bc.ReservaId, ct);
        var eligibleForInvoiceResolution = allCharges
            .Where(x => x.Charge.Kind != OperatorChargeKind.Withholding
                     && x.Charge.ClientTransferMode != ClientTransferMode.Absorbed)
            .ToList();

        Invoice resolvedInvoice;
        if (activeInvoices.Count <= 1)
        {
            resolvedInvoice = originatingInvoice;
        }
        else
        {
            var distinctTargetInvoiceIds = eligibleForInvoiceResolution
                .Select(x => x.Charge.TargetInvoiceId)
                .Distinct()
                .ToList();

            if (distinctTargetInvoiceIds.Count == 0 || distinctTargetInvoiceIds.Contains(null))
                return CancellationDebitNoteItemsResult.Manual(TargetInvoiceUnchosenManualReviewMessage);

            if (distinctTargetInvoiceIds.Count > 1)
                return CancellationDebitNoteItemsResult.Manual(
                    "La cancelación tiene cargos que corresponden a facturas distintas: por ahora se emite una " +
                    "Nota de Débito por vez, a mano.");

            // B2 (re-validacion al emitir, Addendum T3b): la factura elegida al confirmar el cargo tiene que
            // SEGUIR viva (con CAE) y ser miembro de las facturas activas ACTUALES de la reserva. Reintentos/
            // colas async pueden tardar dias; en el medio la factura pudo anularse.
            var candidateInvoiceId = distinctTargetInvoiceIds[0]!.Value;
            var candidateInvoice = activeInvoices.FirstOrDefault(i => i.Id == candidateInvoiceId);
            if (candidateInvoice is null)
                return CancellationDebitNoteItemsResult.Manual(
                    "La factura elegida para este cargo ya no está activa: revisalo antes de emitir.");

            // B2 (fiscal): con 2+ facturas activas, la factura resuelta puede NO ser bc.OriginatingInvoice (la
            // que ya paso el gating de arriba). Re-validamos sobre ELLA los 2 chequeos fiscales duros que el
            // gating ya le hizo a la principal: letra C y sin tributos provinciales.
            if (candidateInvoice.TipoComprobante is not (11 or 12))
                return CancellationDebitNoteItemsResult.Manual(
                    "La factura elegida para este cargo no es Factura C: por ahora esto se resuelve a mano.");
            var candidateTributesCount = await _db.Set<InvoiceTribute>()
                .CountAsync(t => t.InvoiceId == candidateInvoice.Id, ct);
            if (candidateTributesCount > 0)
                return CancellationDebitNoteItemsResult.Manual(
                    "La factura elegida para este cargo tiene tributos provinciales: por ahora esto se resuelve " +
                    "a mano.");

            // B2 (fiscal, moneda extranjera): mismos 2 guards que EvaluateDebitNoteGating le exige a la
            // principal — flag maestro de facturacion en divisa, y cotizacion coherente (>0 y !=1).
            var candidateIsForeign = !string.IsNullOrWhiteSpace(candidateInvoice.MonId)
                && !string.Equals(candidateInvoice.MonId, "PES", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidateInvoice.MonId, "ARS", StringComparison.OrdinalIgnoreCase);
            if (candidateIsForeign && !settings.EnableMultiCurrencyInvoicing)
                return CancellationDebitNoteItemsResult.Manual(
                    "La factura elegida para este cargo está en moneda extranjera y la facturación en moneda " +
                    "extranjera no está habilitada: por ahora esto se resuelve a mano.");
            if (IsForeignInvoiceWithoutReliableArcaData(candidateInvoice))
                return CancellationDebitNoteItemsResult.Manual(
                    "La factura elegida para este cargo quedó con una cotización no confiable: por ahora esto " +
                    "se resuelve a mano.");

            resolvedInvoice = candidateInvoice;
        }

        // Condicion fiscal de la agencia CONGELADA al confirmar la cancelacion (nunca la de HOY: reinterpretar
        // un evento fiscal ya ocurrido con un dato que cambio despues seria incoherente con el resto del modulo).
        var emitterCondition = TaxConditionNormalizer.Normalize(bc.FiscalSnapshot?.AgencyTaxConditionAtEvent);

        var invoiceIsArs = string.IsNullOrWhiteSpace(resolvedInvoice.MonId) ||
                           string.Equals(resolvedInvoice.MonId, "PES", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(resolvedInvoice.MonId, "ARS", StringComparison.OrdinalIgnoreCase);
        var invoiceCurrencyArca = NormalizeCurrencyToArcaOrNull(resolvedInvoice.MonId)
            ?? (invoiceIsArs ? "PES" : null);

        var items = new List<InvoiceItemDto>();
        var absorbedCount = 0;
        decimal total = 0m;

        // S4 (bloqueante security, 2026-07-10) — DOS FASES. NO mutamos charge.Definitive* DENTRO del loop: si un
        // cargo POSTERIOR ruteara a revision manual, RouteDebitNoteToManualReviewAsync hace SaveChanges y
        // persistiria la mutacion de una ND que NUNCA salio (y el motor FX despues la tomaria como real).
        // Recolectamos las asignaciones pendientes en memoria y las aplicamos TODAS juntas recien cuando el build
        // completo dio Ready (justo antes de emitir). Asi ninguna Definitive* se persiste si el build aborta.
        var pendingDefinitiveRates =
            new List<(BookingCancellationLineOperatorCharge Charge, decimal Rate, ExchangeRateSource? Source,
                      DateTime? ChargeDayDate, string? Justification)>();

        // Orden deterministico: por operador y despues por orden de creacion del cargo.
        foreach (var (line, charge) in allCharges.OrderBy(x => x.Line.SupplierId).ThenBy(x => x.Charge.Id))
        {
            if (charge.Kind == OperatorChargeKind.Withholding)
                continue; // credito fiscal de la agencia: nunca se traslada al cliente.

            if (charge.ClientTransferMode == ClientTransferMode.Absorbed)
            {
                absorbedCount++;
                continue; // la agencia decidio no trasladarlo: sin renglon (el cargo ya quedo persistido como rastro).
            }

            // ADR-044 T3b Decision 2 (2026-07-10): si el cargo esta en la MISMA moneda que su factura destino,
            // sin cambios (T3a). Si difiere, se convierte con el TC DEFINITIVO = TC del DIA DEL CARGO del operador
            // (M1 lectura (i), CONFIRMADO por Gaston 2026-07-10): NO se recotiza al dia de emision, se promociona
            // el estimado (que ya es el TC del dia del cargo) copiando su VALOR y su FECHA. Sin conversion posible
            // (par de monedas no soportado, TC no confiable, o sin TC cargado) -> revision manual, nunca se
            // adivina un numero. NO hay tope de antiguedad: bajo la lectura (i) el TC es legitimamente del dia del
            // cargo aunque hayan pasado semanas.
            var chargeCurrencyArca = NormalizeCurrencyToArcaOrNull(charge.Currency);
            decimal amountInInvoiceCurrency;
            if (chargeCurrencyArca is not null && invoiceCurrencyArca is not null &&
                string.Equals(chargeCurrencyArca, invoiceCurrencyArca, StringComparison.OrdinalIgnoreCase))
            {
                amountInInvoiceCurrency = charge.Amount;
            }
            else if (chargeCurrencyArca is null || invoiceCurrencyArca is null)
            {
                return CancellationDebitNoteItemsResult.Manual(
                    $"La multa de {SafeSupplierName(line)} está en una moneda no reconocida: por ahora esto se " +
                    "resuelve a mano.");
            }
            else if (charge.EstimatedExchangeRateToClientInvoiceCurrency is null)
            {
                return CancellationDebitNoteItemsResult.Manual(
                    $"La multa de {SafeSupplierName(line)} está en una moneda distinta a la de su factura: falta " +
                    "cargar el tipo de cambio para convertirla. Cargalo antes de emitir.");
            }
            else
            {
                var chargeDayRate = charge.EstimatedExchangeRateToClientInvoiceCurrency.Value;

                // S1/F1 (bloqueante security): banda de sanidad. Un TC <= 0 o == 1 es el "default peligroso" (se
                // olvido de cargar la cotizacion): no se puede convertir plata con el. Mismo criterio que
                // IsForeignCurrencyInvoiceWithoutReliableRate. (Esta guarda QUEDA; el tope de antiguedad F2 se
                // eliminó bajo la lectura M1 (i).)
                if (IsUnreliableExchangeRate(chargeDayRate))
                    return CancellationDebitNoteItemsResult.Manual(
                        "El tipo de cambio cargado no es válido (parece sin completar): corregilo antes de emitir.");

                var converted = ConvertArsUsdAmount(
                    charge.Amount, chargeCurrencyArca, invoiceCurrencyArca, chargeDayRate);
                if (converted is null)
                    return CancellationDebitNoteItemsResult.Manual(
                        $"La multa de {SafeSupplierName(line)} está en una moneda que todavía no se puede " +
                        "convertir automáticamente: por ahora esto se resuelve a mano.");

                amountInInvoiceCurrency = converted.Value;

                // S4: NO mutamos el cargo aca. Recolectamos la promocion estimado->definitivo para aplicarla
                // recien si el build COMPLETO dio Ready (ver el bloque al final del metodo). La FECHA definitiva
                // es la del ESTIMADO (dia del cargo, M1 (i)), no la de emision.
                pendingDefinitiveRates.Add((
                    charge, chargeDayRate, charge.EstimatedExchangeRateSource,
                    charge.EstimatedExchangeRateAt, charge.EstimatedExchangeRateJustification));
            }

            var passThroughAlicuota = ResolvePassThroughAlicuotaIvaIdOrNull(
                emitterCondition, settings.CancellationDebitNoteRiPassThroughAlicuotaIvaId);
            if (passThroughAlicuota is null)
                return CancellationDebitNoteItemsResult.Manual(
                    "Todavía no está confirmada la alícuota de IVA para trasladarle este tipo de cargo al " +
                    "cliente: quedó para revisión manual.");

            // DATA-EXPOSURE (decidido por Gaston 2026-07-10): el comprobante del pasajero SÍ nombra al mayorista
            // en el renglon pass-through (el cliente ya sabe con que operador viajaba). Description con nombre del
            // operador. El detalle por operador tambien vive en la ficha/auditoria.
            items.Add(new InvoiceItemDto
            {
                Description = $"Penalidad de {SafeSupplierName(line)} por cancelación s/Fc " +
                              $"{resolvedInvoice.PuntoDeVenta:00000}-{resolvedInvoice.NumeroComprobante:00000000}.",
                Quantity = 1,
                UnitPrice = amountInInvoiceCurrency,
                Total = amountInInvoiceCurrency,
                AlicuotaIvaId = passThroughAlicuota.Value,
            });
            total += amountInInvoiceCurrency;

            if (charge.ClientTransferMode == ClientTransferMode.WithManagementFee)
            {
                var managementFeeAlicuota = ResolveAgencyOwnedAlicuotaIvaIdOrNull(emitterCondition);
                if (managementFeeAlicuota is null)
                    return CancellationDebitNoteItemsResult.Manual(
                        "No se pudo determinar la condición fiscal de la agencia para cobrar el cargo de " +
                        "gestión: quedó para revisión manual.");

                // El fee de gestion es SIEMPRE propio de la agencia, cargado en la moneda de la factura (nunca
                // necesita conversion: no es un monto embebido del operador).
                var feeAmount = charge.ManagementFeeAmount!.Value; // CHECK SQL lo garantiza > 0 con este modo.
                items.Add(new InvoiceItemDto
                {
                    Description = $"Cargo de gestión de la agencia por cancelación s/Fc " +
                                  $"{resolvedInvoice.PuntoDeVenta:00000}-{resolvedInvoice.NumeroComprobante:00000000}.",
                    Quantity = 1,
                    UnitPrice = feeAmount,
                    Total = feeAmount,
                    AlicuotaIvaId = managementFeeAlicuota.Value,
                });
                total += feeAmount;
            }
        }

        if (absorbedCount > 0)
        {
            _logger.LogInformation(
                "ADR-044 T3a: BC {BcPublicId} absorbe {Count} cargo(s) del operador (no se trasladan al " +
                "cliente). metric:cancellation_debit_note_charge_absorbed",
                bc.PublicId, absorbedCount);
        }

        if (items.Count == 0)
        {
            // Si hubo al menos un cargo absorbido, es una decision real de la agencia: nada que facturar.
            // Si no (todos los cargos eran Withholding, dato atipico sin un solo cargo pass-through creado),
            // no hay de donde armar el desglose: cae al mismo camino legacy que "sin cargos".
            return absorbedCount > 0
                ? CancellationDebitNoteItemsResult.Absorbed()
                : LegacySingleItem(bc, originatingInvoice);
        }

        // M2 (espejo del gating de un solo operador): el total nunca supera el total de la factura RESUELTA
        // (no siempre bc.OriginatingInvoice: con 2+ facturas activas puede ser otra).
        if (total > resolvedInvoice.ImporteTotal)
            return CancellationDebitNoteItemsResult.Manual(
                "El total de los cargos trasladados supera el total de la factura original: queda para " +
                "revisión manual.");

        // S4 (FASE 2): el build COMPLETO dio Ready — recien AHORA fijamos el TC DEFINITIVO de TODOS los cargos
        // que necesitaron conversion. Antes de este punto no se toco ningun charge.Definitive*, asi que un build
        // que aborto a Manual jamas persiste una promocion fantasma. El estimado queda intacto como rastro
        // (nunca se pisa). M1 lectura (i) (CONFIRMADO por Gaston 2026-07-10): el VALOR y la FECHA definitivos son
        // los del ESTIMADO (TC del dia del cargo del operador), NO el TC/fecha del dia de emision — la ND traslada
        // el cargo al TC del dia en que el operador cobro, aunque la emision sea semanas despues.
        foreach (var (charge, rate, source, chargeDayDate, justification) in pendingDefinitiveRates)
        {
            charge.DefinitiveExchangeRateAtNdEmission = rate;
            charge.DefinitiveExchangeRateSource = source;
            charge.DefinitiveExchangeRateAt = chargeDayDate;
            charge.DefinitiveExchangeRateJustification = justification;
        }

        return CancellationDebitNoteItemsResult.Ready(items, total, resolvedInvoice);
    }

    /// <summary>
    /// S1/F1 (gate T3b, 2026-07-10): un tipo de cambio &lt;= 0 o == 1 es el "default peligroso" (la cotizacion
    /// quedo sin cargar). Mismo criterio que <see cref="IsForeignCurrencyInvoiceWithoutReliableRate"/> — no se
    /// puede convertir plata con un TC asi.
    /// </summary>
    private static bool IsUnreliableExchangeRate(decimal rate) => rate <= 0m || rate == 1m;

    /// <summary>
    /// ADR-044 T3b Decision 2 (2026-07-10): convierte un monto entre ARS y USD con la convencion FIJA del
    /// sistema (TC = unidades de ARS por 1 USD, misma orientacion que <c>Payment.ExchangeRate</c>/
    /// <c>Invoice.MonCotiz</c>). Devuelve <c>null</c> si el par de monedas no es ARS/USD (el sistema no opera
    /// otras monedas hoy, ver <see cref="Monedas.Soportadas"/>) o si el TC no es confiable (&lt;= 0 o == 1,
    /// <see cref="IsUnreliableExchangeRate"/>): el caller trata <c>null</c> como "no se puede convertir" ->
    /// revision manual, nunca un numero inventado. (El caller ademas chequea el TC ANTES para dar un mensaje
    /// especifico de "corregilo"; esta guarda es defensa en profundidad.)
    /// </summary>
    private static decimal? ConvertArsUsdAmount(
        decimal amount, string fromCurrencyArca, string toCurrencyArca, decimal rate)
    {
        if (IsUnreliableExchangeRate(rate)) return null;

        bool fromIsUsd = string.Equals(fromCurrencyArca, "DOL", StringComparison.OrdinalIgnoreCase);
        bool fromIsArs = string.Equals(fromCurrencyArca, "PES", StringComparison.OrdinalIgnoreCase);
        bool toIsUsd = string.Equals(toCurrencyArca, "DOL", StringComparison.OrdinalIgnoreCase);
        bool toIsArs = string.Equals(toCurrencyArca, "PES", StringComparison.OrdinalIgnoreCase);

        if (fromIsUsd && toIsArs) return Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero);
        if (fromIsArs && toIsUsd) return Math.Round(amount / rate, 2, MidpointRounding.AwayFromZero);
        return null;
    }

    // ============================================================
    // ADR-044 Fix B (2026-07-13): resolver la conversion de una multa declarada en una moneda
    // distinta a la de la factura, en el momento de CORREGIRLA. La idea (opcion (c) del diseno):
    // convertir ANTES de guardar, dejando intacto el guard de coherencia de la emision, para que
    // sea IMPOSIBLE emitir una ND en la escala equivocada.
    // ============================================================

    /// <summary>Que decidio la resolucion de conversion de la multa (ADR-044 Fix B).</summary>
    internal enum PenaltyConversionOutcome
    {
        /// <summary>La multa ya esta en la moneda de la factura: se usa tal cual, sin TC (comportamiento de hoy).</summary>
        SameCurrency,

        /// <summary>Caso A: se convirtio a la moneda de la factura con el TC provisto.</summary>
        Converted,

        /// <summary>Caso A pero falta la fecha o el TC (o el TC es no confiable): 400, corregir el dato.</summary>
        NeedsExchangeRate,

        /// <summary>Caso B: alguna linea del operador esta en otra moneda: no se puede convertir con un solo TC -> revision manual.</summary>
        NotConvertible,
    }

    /// <summary>
    /// Resultado de <see cref="ResolveDeclaredPenaltyConversion"/>. En <see cref="PenaltyConversionOutcome.SameCurrency"/>
    /// y <see cref="PenaltyConversionOutcome.Converted"/> trae el monto/moneda EFECTIVOS a guardar; en los otros dos
    /// trae solo el <see cref="Reason"/> (mensaje limpio para el usuario, sin jerga).
    /// </summary>
    internal readonly record struct DeclaredPenaltyConversion(
        PenaltyConversionOutcome Outcome,
        decimal EffectiveAmount,
        string EffectiveCurrencyIso,
        decimal? DeclaredOriginalAmount,
        string? DeclaredOriginalCurrencyIso,
        decimal? ExchangeRate,
        ExchangeRateSource? ExchangeRateSource,
        DateTime? ExchangeRateAt,
        string? ExchangeRateJustification,
        string? Reason);

    /// <summary>
    /// ADR-044 Fix B (seguridad, 2026-07-14): PISO de cordura del tipo de cambio ARS-por-1-USD. No es una
    /// cotizacion exacta (a proposito NO se acopla al snapshot BNA, que puede no existir): es un limite AMPLIO
    /// para atajar un dedazo grosero. Un TC fraccionario (ej. 0,5) pasa el filtro de
    /// <see cref="IsUnreliableExchangeRate"/> (que solo rechaza &lt;=0 o ==1) pero no es un valor real.
    /// </summary>
    private const decimal MinSaneExchangeRate = 1m;

    /// <summary>
    /// ADR-044 Fix B (seguridad, 2026-07-14): TECHO de cordura del tipo de cambio. Un TC absurdo (ej. 10^9)
    /// pasaria <see cref="IsUnreliableExchangeRate"/> y produciria un monto convertido sin sentido que las
    /// columnas M2 (que solo guardan el original) no revierten. 1.000.000 ARS por USD es un techo holgadisimo
    /// frente a cualquier cotizacion real previsible; por encima es casi seguro un error de tipeo.
    /// </summary>
    private const decimal MaxSaneExchangeRate = 1_000_000m;

    /// <summary>
    /// ADR-044 Fix B (2026-07-13): decide si la multa de una correccion se guarda tal cual, se convierte a la
    /// moneda de la factura, o no se puede resolver. Es PURA (no toca la base) y por eso se testea sola.
    ///
    /// <para><b>Regla del disparador</b> (lo que el reviewer de arquitectura corrigio): la conversion NO se
    /// dispara por "moneda de la multa != moneda de la factura", sino por el eje que gobierna el neteo del
    /// reembolso del operador: la moneda de las LINEAS del operador. Dos casos:</para>
    /// <list type="bullet">
    ///   <item><b>Caso A (convertible)</b>: TODAS las lineas del operador ya estan en la moneda de la factura.
    ///     Se convierte la multa declarada a esa moneda con el TC provisto. Al netear, las lineas ya hablan la
    ///     moneda convertida -> coherente.</item>
    ///   <item><b>Caso B (no convertible)</b>: alguna linea del operador esta en otra moneda (ej. el operador
    ///     internacional retuvo USD sobre un servicio USD, con el cliente facturado en pesos). Un solo TC no
    ///     puede resolver a la vez el renglon en pesos de la ND (lado cliente) y el cap en dolares del operador
    ///     (lado proveedor): se rutea a revision manual, sin convertir ni tocar nada.</item>
    /// </list>
    /// </summary>
    /// <param name="operatorLines">Lineas del operador principal del BC (las que gobiernan el neteo del reembolso).</param>
    /// <param name="declaredCurrencyIso">Moneda declarada de la multa, ya normalizada a ISO ("ARS"/"USD").</param>
    /// <param name="invoiceCurrencyIso">Moneda de la factura destino, en ISO ("ARS"/"USD").</param>
    /// <param name="declaredAmount">Monto declarado de la multa, en su moneda original.</param>
    /// <param name="exchangeRate">TC provisto por el usuario (ARS por 1 USD). Requerido en Caso A cruzado.</param>
    /// <param name="exchangeRateSource">Origen del TC. Null se trata como Manual (exige justificacion).</param>
    /// <param name="exchangeRateDate">Fecha del TC (dia en que el operador cobro). Requerida en Caso A cruzado.</param>
    /// <param name="exchangeRateJustification">Justificacion del TC. Obligatoria cuando el origen es Manual (INV-120).</param>
    internal static DeclaredPenaltyConversion ResolveDeclaredPenaltyConversion(
        IReadOnlyCollection<BookingCancellationLine> operatorLines,
        string declaredCurrencyIso,
        string invoiceCurrencyIso,
        decimal declaredAmount,
        decimal? exchangeRate,
        ExchangeRateSource? exchangeRateSource,
        DateTime? exchangeRateDate,
        string? exchangeRateJustification)
    {
        var declared = Monedas.Normalizar(declaredCurrencyIso);
        var invoice = Monedas.Normalizar(invoiceCurrencyIso);

        // Mismo-moneda: comportamiento de hoy. No se pide TC ni se llenan columnas de conversion.
        if (string.Equals(declared, invoice, StringComparison.OrdinalIgnoreCase))
            return SameCurrencyConversion(declaredAmount, declared);

        // Cruce de moneda. ¿Todas las lineas del operador ya estan en la moneda de la factura? (Caso A)
        bool todasLasLineasEnLaMonedaDeLaFactura = operatorLines.Count > 0 &&
            operatorLines.All(l => string.Equals(
                Monedas.Normalizar(l.Currency), invoice, StringComparison.OrdinalIgnoreCase));
        if (!todasLasLineasEnLaMonedaDeLaFactura)
            return NotConvertibleConversion(
                "Esta multa está en una moneda distinta a la de la factura y a la de lo que el operador tiene " +
                "que devolver, así que no se puede convertir automáticamente. La tiene que revisar una persona.");

        // Caso A: convertible. Exigimos fecha + TC validos (defensa server-side, no confiar en el front).
        if (exchangeRateDate is null)
            return NeedsExchangeRateConversion(
                "Falta la fecha en que el operador cobró la multa para poder convertirla a la moneda de la factura.");

        // Defensa en profundidad (seguridad 2026-07-14): la fecha del TC no puede ser FUTURA (el front ya lo
        // bloquea; lo revalidamos aca). Un TC "del futuro" no existe todavia. Comparamos por DIA de calendario en
        // UTC, mismo criterio que ConfirmPenaltyAsync con la fecha del operador (:6038).
        if (exchangeRateDate.Value.Date > DateTime.UtcNow.Date)
            return NeedsExchangeRateConversion(
                "La fecha del tipo de cambio no puede ser futura. Revisala.");

        if (exchangeRate is null || IsUnreliableExchangeRate(exchangeRate.Value))
            return NeedsExchangeRateConversion(
                "Falta un tipo de cambio válido para convertir la multa a la moneda de la factura.");

        // Techo/piso de cordura del TC (seguridad 2026-07-14): IsUnreliableExchangeRate solo ataja <=0 o ==1; un
        // TC absurdo (10^9) o fraccionario (0,5) pasa igual y daria un monto convertido sin sentido que M2 no
        // revierte. Lo acotamos a un rango AMPLIO y razonable (NO acoplado al snapshot BNA, que puede faltar):
        // fuera de [MinSaneExchangeRate, MaxSaneExchangeRate] es casi seguro un dedazo, no una cotizacion real.
        if (exchangeRate.Value < MinSaneExchangeRate || exchangeRate.Value > MaxSaneExchangeRate)
            return NeedsExchangeRateConversion(
                "El tipo de cambio que pusiste no parece un valor real. Revisalo.");

        // El TC cargado a mano exige una razon escrita (misma regla que el resto de los TC manuales, INV-120).
        // Si no vino una fuente, la tratamos como Manual (el caso mas exigente): asi nunca se guarda un TC a mano
        // sin justificacion por el solo hecho de que el front no mando el origen.
        var effectiveSource = exchangeRateSource ?? Domain.Entities.ExchangeRateSource.Manual;
        if (effectiveSource == Domain.Entities.ExchangeRateSource.Manual
            && string.IsNullOrWhiteSpace(exchangeRateJustification))
            return NeedsExchangeRateConversion(
                "Indicá por qué usás ese tipo de cambio para convertir la multa.");

        // Conversion con la maquinaria existente (misma que usa la emision de cargos T3b). Trabaja en el eje ARCA.
        var declaredArca = ArcaCurrencyMapper.TryMap(declared);
        var invoiceArca = ArcaCurrencyMapper.TryMap(invoice);
        if (declaredArca is null || invoiceArca is null)
            return NotConvertibleConversion(
                "Esta multa está en una moneda que no se puede convertir automáticamente. La tiene que revisar una persona.");

        var converted = ConvertArsUsdAmount(declaredAmount, declaredArca, invoiceArca, exchangeRate.Value);
        if (converted is null || converted.Value <= 0m)
            return NeedsExchangeRateConversion(
                "No se pudo convertir la multa a la moneda de la factura con ese tipo de cambio. Revisá el dato.");

        // La fecha del TC va a una columna timestamptz: Postgres/Npgsql EXIGE Kind=Utc. Un date-picker del front
        // suele mandar la fecha sin zona (Kind=Unspecified) o local; como esto representa un DIA de calendario (el
        // dia en que el operador cobro), la fijamos como UTC sin correr el reloj, para no mover el dia ni romper el
        // INSERT en produccion. (InMemory no valida Kind, pero prod si.)
        var exchangeRateAtUtc = exchangeRateDate.Value.Kind == DateTimeKind.Utc
            ? exchangeRateDate.Value
            : DateTime.SpecifyKind(exchangeRateDate.Value, DateTimeKind.Utc);

        return new DeclaredPenaltyConversion(
            Outcome: PenaltyConversionOutcome.Converted,
            EffectiveAmount: converted.Value,
            EffectiveCurrencyIso: invoice,
            DeclaredOriginalAmount: declaredAmount,
            DeclaredOriginalCurrencyIso: declared,
            ExchangeRate: exchangeRate.Value,
            ExchangeRateSource: effectiveSource,
            ExchangeRateAt: exchangeRateAtUtc,
            ExchangeRateJustification: exchangeRateJustification?.Trim(),
            Reason: null);
    }

    private static DeclaredPenaltyConversion SameCurrencyConversion(decimal amount, string currencyIso) =>
        new(PenaltyConversionOutcome.SameCurrency, amount, currencyIso,
            null, null, null, null, null, null, null);

    private static DeclaredPenaltyConversion NeedsExchangeRateConversion(string reason) =>
        new(PenaltyConversionOutcome.NeedsExchangeRate, 0m, string.Empty,
            null, null, null, null, null, null, reason);

    private static DeclaredPenaltyConversion NotConvertibleConversion(string reason) =>
        new(PenaltyConversionOutcome.NotConvertible, 0m, string.Empty,
            null, null, null, null, null, null, reason);

    /// <summary>
    /// ADR-044 Fix B (2026-07-13): moneda de la factura destino en ISO ("ARS"/"USD"), a partir de su
    /// <c>MonId</c> (que se guarda en formato ARCA "PES"/"DOL", o vacio = pesos por convencion legacy).
    /// </summary>
    private static string ResolveInvoiceCurrencyIso(string? invoiceMonId)
    {
        // "DOL" -> USD; "PES"/vacio/no reconocido -> ARS (regla legacy: sin moneda == pesos).
        var invoiceArca = NormalizeCurrencyToArcaOrNull(invoiceMonId);
        return string.Equals(invoiceArca, "DOL", StringComparison.OrdinalIgnoreCase)
            ? Monedas.USD
            : Monedas.ARS;
    }

    /// <summary>
    /// ADR-044 T3a: el UNICO renglon de siempre (comportamiento previo a esta tanda, byte-identico). Se usa
    /// cuando no hay cargos tipificados de donde armar el desglose por operador, o cuando el concepto es propio
    /// de la agencia (que nunca crea cargos del operador).
    /// </summary>
    private static CancellationDebitNoteItemsResult LegacySingleItem(BookingCancellation bc, Invoice originatingInvoice)
    {
        var penaltyAmount = bc.PenaltyAmountAtEvent!.Value; // el gating de arriba ya garantizo > 0
        var items = new List<InvoiceItemDto>
        {
            new()
            {
                Description = $"Penalidad por cancelacion s/Fc " +
                              $"{originatingInvoice.PuntoDeVenta:00000}-{originatingInvoice.NumeroComprobante:00000000}.",
                Quantity = 1,
                UnitPrice = penaltyAmount,
                Total = penaltyAmount,
                AlicuotaIvaId = 3, // 0% / no gravado -> C sin IVA discriminado. Byte-identico al comportamiento previo.
            },
        };
        return CancellationDebitNoteItemsResult.Ready(items, penaltyAmount, originatingInvoice);
    }

    /// <summary>Nombre del operador para la descripcion de un renglon, sin dejar un hueco vacio si faltara.</summary>
    private static string SafeSupplierName(BookingCancellationLine line) =>
        string.IsNullOrWhiteSpace(line.Supplier?.Name) ? "el operador" : line.Supplier!.Name.Trim();

    /// <summary>
    /// ADR-044 T3a: alicuota de IVA (codigo ARCA) para un renglon PASS-THROUGH (la multa del operador
    /// replicada tal cual, sin agregarle nada). Monotributo/Exento: SIEMPRE 3 (0%), verificado para cualquier
    /// concepto (ver la spec fiscal firmada de T3, punto 5). Responsable Inscripto: SIN firma contable — solo
    /// se usa si el admin ya cargo el valor confirmado en Configuracion; sin ese valor, null (el caller debe
    /// bloquear la emision y rutear a revision manual). Cualquier otra condicion (Consumidor Final/Extranjero/
    /// desconocida): null, conservador — no deberia darse (el emisor es siempre la agencia).
    /// </summary>
    internal static int? ResolvePassThroughAlicuotaIvaIdOrNull(
        TaxConditionCanonical emitterCondition, int? riPassThroughAlicuotaIvaIdSetting) =>
        emitterCondition switch
        {
            TaxConditionCanonical.Monotributista => 3,
            TaxConditionCanonical.Exento => 3,
            TaxConditionCanonical.ResponsableInscripto => riPassThroughAlicuotaIvaIdSetting,
            _ => null,
        };

    /// <summary>
    /// ADR-044 T3a: alicuota de IVA (codigo ARCA) para un renglon PROPIO de la agencia (fee de gestion,
    /// concepto <see cref="CancellationConceptKind.AgencyManagementFee"/>). Monotributo/Exento: 3 (0%).
    /// Responsable Inscripto: 5 (21%), YA FIRMADO (R2 contador matriculado 2026-06-01, art.61 DR IVA + DAT
    /// 44/01) — a diferencia del pass-through, este SI tiene un valor operativo confirmado de antemano.
    /// Cualquier otra condicion: null, conservador.
    /// </summary>
    internal static int? ResolveAgencyOwnedAlicuotaIvaIdOrNull(TaxConditionCanonical emitterCondition) =>
        emitterCondition switch
        {
            TaxConditionCanonical.Monotributista => 3,
            TaxConditionCanonical.Exento => 3,
            TaxConditionCanonical.ResponsableInscripto => 5,
            _ => null,
        };

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
    /// Regla fiscal cerrada (firmada): una penalidad del OPERADOR cobrada al cliente como
    /// pass-through TAMBIEN emite una Nota de Debito. La agencia replica la cadena del operador
    /// (le cobra al cliente la multa que el operador le aplico a la agencia), PERO esa plata NO
    /// es ingreso gravado propio de la agencia: la ND sale como concepto NO gravado (item 0% /
    /// AlicuotaIvaId=3, igual que ya arma <see cref="TryEmitCancellationDebitNoteAsync"/>).
    ///
    /// <para><b>Diferencia con <see cref="ConceptIsAgencyOwnedDebitNote"/></b> (clave fiscal):
    /// "emite ND" NO es lo mismo que "es ingreso propio de la agencia". Los dos conceptos
    /// AgencyManagementFee/AgencyCancellationFee son ingreso propio gravado Y emiten ND;
    /// OperatorPenaltyPassThrough emite ND pero NO es ingreso propio (es plata del operador). Por
    /// eso la guarda de PERMISO elevado (clasificar como ingreso propio) sigue mirando
    /// <see cref="ConceptIsAgencyOwnedDebitNote"/>, no esta. Una ND pass-through NO requiere ese
    /// permiso porque no declara ingreso gravado de la agencia.</para>
    ///
    /// <para>Los conceptos de seguro (RealInsurancePremium, AgencyCancellationCoverage,
    /// AgencyInsuranceCommission) siguen SIN emitir ND automatica (tratamiento de IVA distinto,
    /// no cerrado): caen a revision manual via el gating.</para>
    /// </summary>
    internal static bool ConceptEmitsDebitNote(CancellationConceptKind concept) =>
        ConceptIsAgencyOwnedDebitNote(concept) ||
        concept == CancellationConceptKind.OperatorPenaltyPassThrough;

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
        bool debitNoteFeatureEnabled,
        // ADR-014 (fix integracion 2026-06-02 + fix clobber 2026-06-02): SOLO el path
        // DIFERIDO (ConfirmPenaltyAsync) pasa esto en true. En el diferido, el usuario que
        // confirma la penalidad el Dia N YA paso el permiso elevado (Precondicion 3). Si el
        // BC todavia NO tiene clasificador registrado, ese usuario ES el clasificador
        // autoritativo del concepto y hay que sellar su rastro AUNQUE el concepto no cambie
        // (sin esto, un BC sembrado con el mismo concepto que trae el confirm dejaria
        // ConceptClassifiedByUserId en NULL y el gating B3 lo ruteria a revision manual en
        // vez de emitir la ND).
        //
        // OJO (anti-clobber): este modo NO debe pisar un clasificador YA registrado (ej. el
        // usuario A que clasifico el concepto en el Dia 0 via ConfirmAsync). El confirmador
        // del Dia N tiene su PROPIA columna de auditoria (PenaltyConfirmedBy*), asi que
        // colapsar ambos roles en ConceptClassifiedBy* destruiria el dato de A. Por eso el
        // sellado forzado es CONDICIONAL a que no haya clasificador previo. El path sincrono
        // Dia 0 lo deja en false (default) y conserva su semantica "sellar solo si cambia".
        bool sealClassifierAuditWhenMissing = false)
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
                "No tenés permiso para clasificar la penalidad como ingreso propio de la agencia " +
                "(emite una Nota de Débito). Pedíselo a un administrador.",
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
        //     el concepto. Sellamos los 3 campos (ConceptClassifiedByUserId/Name/At) cuando:
        //
        //       - el concepto CAMBIA respecto del valor actual (semantica original,
        //         comportamiento del path sincrono Dia 0); o
        //       - estamos en modo forzado (path diferido Dia N) Y todavia NO hay un
        //         clasificador registrado.
        //
        //     El segundo caso cierra el bug del flujo diferido: cuando el BC trae el mismo
        //     concepto que el confirm (ej. seed con ConceptKind=AgencyManagementFee sin
        //     auditoria previa), conceptChanged=false pero igual hay que sellar al confirmador
        //     como clasificador, porque si no ConceptClassifiedByUserId quedaria NULL y el
        //     gating (B3) ruteria a revision manual en vez de emitir la ND.
        //
        //     La condicion "&& ConceptClassifiedByUserId is null" es el anti-clobber: si el
        //     concepto NO cambia pero YA hay un clasificador (ej. el usuario A que clasifico
        //     en el Dia 0), NO lo pisamos con el confirmador del Dia N. El confirmador ya tiene
        //     su propia columna (PenaltyConfirmedBy*, mas abajo); colapsar ambos roles aca
        //     destruiria el dato de A. Y el gating B3 igual pasa porque el campo es != null.
        var conceptChanged = bc.ConceptKind != requestedConcept;
        if (conceptChanged)
        {
            bc.ConceptKind = requestedConcept;
        }
        if (conceptChanged ||
            (sealClassifierAuditWhenMissing && bc.ConceptClassifiedByUserId is null))
        {
            bc.ConceptClassifiedByUserId = userId;
            bc.ConceptClassifiedByUserName = userName;
            bc.ConceptClassifiedAt = DateTime.UtcNow;
        }

        // (4) Finalidad de la ND. Si el usuario la informo, la respetamos. Si no:
        //
        //     - Cargo propio de la agencia (agency-owned): defaulteamos a PenaltyOrCancellationCharge
        //       (comportamiento historico — el unico caso que el MVP automatiza).
        //
        //     - Penalidad pass-through del operador: TAMBIEN emite ND (regla fiscal firmada), pero solo
        //       defaulteamos la finalidad cuando la penalidad se esta CONFIRMANDO en esta misma captura
        //       (PenaltyStatus=Confirmed). El path diferido (ConfirmPenaltyAsync) llega siempre con
        //       Confirmed, asi que la ND pass-through obtiene su finalidad y puede emitir. En el Dia 0
        //       sin confirmacion (Estimated) NO pre-estampamos la finalidad: no hay ND inminente y
        //       conservamos el comportamiento previo (DebitNotePurpose queda null).
        if (classification.DebitNotePurpose.HasValue)
        {
            bc.DebitNotePurpose = classification.DebitNotePurpose.Value;
        }
        else if (bc.DebitNotePurpose is null &&
                 (ConceptIsAgencyOwnedDebitNote(requestedConcept) ||
                  (ConceptEmitsDebitNote(requestedConcept) &&
                   classification.PenaltyStatus == PenaltyStatus.Confirmed)))
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
                "Débito en juego. Cambiar el concepto ahora podría producir un doble cobro. " +
                "Anulá la Nota de Débito antes de reclasificar.",
                invariantCode: "INV-ADR013-002");
        }
    }

    // internal (no private) para que los tests unit puedan validar el gating sin DB:
    // el proyecto tiene InternalsVisibleTo("TravelApi.Tests"). Es una funcion pura.
    internal static string? EvaluateDebitNoteGating(
        BookingCancellation bc,
        Invoice originatingInvoice,
        // ADR-012/013 (multimoneda ND, 2026-07-08): estado del flag EnableMultiCurrencyInvoicing.
        // Lo inyecta el caller de produccion (TryEmit lee OperationalFinanceSettings y lo pasa NOMBRADO); la
        // funcion sigue PURA/testeable sin DB. Con el flag OFF (tambien el DEFAULT), una factura en moneda
        // extranjera vuelve a rutear a revision manual: es el comportamiento conservador previo a este cambio,
        // byte-identico para todo el gating ARS (donde el flag ni se mira). NO es un flag nuevo: es el MISMO
        // que ya gobierna la NC total en InvoiceService.
        bool multiCurrencyInvoicingEnabled = false)
    {
        // Concepto: emiten ND tanto el cargo propio de la agencia (gravado) COMO la penalidad
        // pass-through del operador (no gravada, se replica al cliente). Solo los conceptos de
        // seguro caen a revision manual (tratamiento de IVA no cerrado). Regla fiscal firmada:
        // ver ConceptEmitsDebitNote.
        if (!ConceptEmitsDebitNote(bc.ConceptKind))
            return $"Concepto {bc.ConceptKind} no emite Nota de Debito automatica (revision manual).";

        // NOTA (regla fiscal firmada): el pass-through del operador YA NO se rutea a manual aca.
        // Antes (ADR-013 original) un operador con PenaltyOwnership=Operator bloqueaba la ND
        // porque se asumia que la agencia no replicaba la multa al cliente. La regla cerrada dice
        // lo contrario: la penalidad del operador SI se le cobra al cliente con una ND (como
        // concepto no gravado). Por eso ya no rechazamos por PenaltyOwnership=Operator.

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

        // ===================== MONEDA DE LA ND (dos controles independientes) =====================
        // La ND HEREDA la moneda y el TC congelado de la factura original (mismo criterio ADR-012 §3.3 que la
        // NC total: el set factura+NC+ND habla la misma moneda y el mismo tipo de cambio). Antes de dejarla
        // emitir hacemos DOS chequeos distintos:
        //   (A) COHERENCIA: la moneda DECLARADA de la multa (lo que el usuario dijo que retuvo el operador)
        //       debe coincidir con la moneda de la factura. Si no, la ND saldria por el numero equivocado.
        //   (B) CAPACIDAD de emitir en divisa: si la factura es extranjera, exigimos los guards multimoneda.

        // La factura en pesos = MonId "PES" o vacio (convencion legacy). "ARS" tambien, por las dudas.
        var invoiceIsArs = string.IsNullOrWhiteSpace(originatingInvoice.MonId) ||
                           string.Equals(originatingInvoice.MonId, "PES", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(originatingInvoice.MonId, "ARS", StringComparison.OrdinalIgnoreCase);

        // (A) COHERENCIA declarado-vs-factura (B1 security 2026-07-08). Comparamos en el eje ARCA usando
        //     ArcaCurrencyMapper (NUNCA comparacion de strings directa: la declarada viene en ISO "USD"/"ARS"
        //     y la factura en ARCA "DOL"/"PES"; "USD" != "DOL" como string aunque sean la misma moneda).
        //     - Declarada NULL: para factura en pesos es seguro (la ND sale en pesos igual, sin riesgo de
        //       escala); para factura extranjera NO adivinamos -> revision manual.
        //     - Declarada != moneda de la factura: NO emitimos (numero equivocado). NO convertimos con TC.
        var declaredPenaltyArca = NormalizeCurrencyToArcaOrNull(bc.PenaltyCurrencyAtEvent);
        var invoiceArca = NormalizeCurrencyToArcaOrNull(originatingInvoice.MonId) ?? (invoiceIsArs ? "PES" : null);
        if (declaredPenaltyArca is null)
        {
            if (!invoiceIsArs)
                return $"No quedó registrado en qué moneda se cargó la multa y la factura original está en " +
                       $"{MonedaLabel(originatingInvoice.MonId)}: queda para revisión manual.";
        }
        else if (!string.Equals(declaredPenaltyArca, invoiceArca, StringComparison.OrdinalIgnoreCase))
        {
            return $"La multa se cargó en {MonedaLabel(bc.PenaltyCurrencyAtEvent)} pero la factura original está " +
                   $"en {MonedaLabel(originatingInvoice.MonId)}: lo tiene que revisar una persona.";
        }

        // (B) CAPACIDAD de emitir en divisa. Una factura en pesos sigue el camino de siempre. Una factura
        //     extranjera solo se automatiza con los MISMOS guards multimoneda que la NC total
        //     (InvoiceService.ProcessAnnulmentJob); ante cualquier duda -> revision manual (conservador).
        if (!invoiceIsArs)
        {
            // Guard 1: flag maestro. Con OFF, la moneda extranjera vuelve a revision manual (byte-identico a
            // antes de este cambio). Sin flag no emitimos comprobantes valuados en divisa.
            if (!multiCurrencyInvoicingEnabled)
                return $"La factura original está en {MonedaLabel(originatingInvoice.MonId)} y la facturación en " +
                       "moneda extranjera no está habilitada: queda para revisión manual.";

            // Guard 2: moneda soportada por el catalogo ARCA que sabemos emitir (mismo criterio que la NC
            // total). Una moneda fuera del catalogo (ej. "EUR" legacy) la rebotaria ARCA -> revision manual.
            // NO reflejamos el codigo desconocido al usuario (data-exposure).
            if (!ArcaCurrencyMapper.IsValidArcaCurrencyCode(originatingInvoice.MonId))
                return "La factura original está en una moneda que todavía no se puede facturar automáticamente: " +
                       "queda para revisión manual.";

            // Guard 3: cotizacion coherente (> 0 y != 1). Un TC <= 0 o == 1 en una factura extranjera es un
            // dato corrupto (valuar un dolar como un peso). Mismo candado de incoherencia que la NC total.
            // NO mostramos el numero crudo al usuario (data-exposure); el crudo queda en el log/entidad.
            if (originatingInvoice.MonCotiz <= 0m || originatingInvoice.MonCotiz == 1m)
                return $"La factura original está en {MonedaLabel(originatingInvoice.MonId)} pero su cotización " +
                       "quedó mal cargada: queda para revisión manual.";

            // Pasa los guards: la ND se emite en la moneda extranjera heredando MonId/MonCotiz del original.
        }

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
    /// B1 (security 2026-07-08): normaliza una moneda al codigo ARCA ("PES"/"DOL"), venga en ISO ("ARS"/"USD")
    /// o ya en ARCA ("PES"/"DOL"). Es el puente para comparar la moneda DECLARADA de la multa (ISO, la manda el
    /// front) contra la moneda de la factura (ARCA) en el MISMO eje. Devuelve <c>null</c> si esta vacia o si la
    /// moneda no es reconocida (ej. "EUR" legacy): el caller trata el null como "no comparable" -> revision manual.
    /// </summary>
    internal static string? NormalizeCurrencyToArcaOrNull(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return null;

        // TryMap traduce ISO -> ARCA ("USD"->"DOL", "ARS"->"PES"). Si ya viene en ARCA, TryMap da null y
        // caemos al chequeo de codigo ARCA valido.
        var fromIso = ArcaCurrencyMapper.TryMap(currency);
        if (fromIso is not null)
            return fromIso;

        return ArcaCurrencyMapper.IsValidArcaCurrencyCode(currency)
            ? ArcaCurrencyMapper.NormalizeArcaCurrencyCode(currency)
            : null;
    }

    /// <summary>
    /// Etiqueta de moneda para el USUARIO final, sin codigos tecnicos (data-exposure). Acepta ISO o ARCA:
    /// "PES"/"ARS"/vacio -> "pesos" (vacio = pesos por convencion legacy), "DOL"/"USD" -> "dolares (US$)",
    /// cualquier otra -> "una moneda no reconocida" (no filtramos el codigo crudo al usuario).
    /// </summary>
    internal static string MonedaLabel(string? currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
            return "pesos"; // MonId vacio = pesos (convencion legacy del dominio).

        var code = currencyCode.Trim();
        if (string.Equals(code, "PES", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "ARS", StringComparison.OrdinalIgnoreCase))
            return "pesos";
        if (string.Equals(code, "DOL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "USD", StringComparison.OrdinalIgnoreCase))
            return "dólares (US$)";

        return "una moneda no reconocida";
    }

    /// <summary>
    /// ADR-025 (DT.7): true si la factura original esta en moneda extranjera pero el snapshot fiscal NO
    /// tiene una cotizacion confiable (rate &lt;= 0 o == 1). Espeja el guard del path de NC parcial
    /// (F2.5, <c>:3644</c>). Una factura en pesos (CurrencyAtEvent ARS/null o MonId PES/vacio) nunca dispara.
    /// </summary>
    private static bool IsForeignCurrencyInvoiceWithoutReliableRate(BookingCancellation bc)
    {
        // Moneda del evento: del snapshot si esta, sino de la factura. ARS / PES / vacio = pesos -> no aplica.
        string currency = bc.FiscalSnapshot?.CurrencyAtEvent ?? bc.OriginatingInvoice?.MonId ?? "ARS";
        bool isForeign =
            !string.IsNullOrWhiteSpace(currency)
            && !string.Equals(currency, "ARS", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(currency, "PES", StringComparison.OrdinalIgnoreCase);
        if (!isForeign) return false;

        // Cotizacion confiable = > 0 y != 1. El 1 es el default peligroso ("se me olvido la cotizacion").
        decimal rate = bc.FiscalSnapshot?.ExchangeRateAtOriginalInvoice ?? 0m;
        return rate <= 0m || rate == 1m;
    }

    /// <summary>
    /// ADR-042 §3.5 step 1 (2026-07-01): lista TODAS las facturas de venta vivas con CAE de la reserva y
    /// valida CADA UNA (todo-o-nada al frente). Si alguna factura extranjera tiene cotizacion sospechosa
    /// (TC&lt;=0 o ==1) o moneda no soportada, tira INV-156 y NO se emite ninguna NC. Devuelve la lista para
    /// crear las hijas y encolar una anulacion por factura. Mismo filtro de "factura de venta viva" que
    /// DraftAsync (excluye NC/ND y filas fantasma sin CAE).
    /// </summary>
    private async Task<List<Invoice>> ResolveAndPreflightInvoicesToAnnulAsync(int reservaId, CancellationToken ct)
    {
        var activeInvoices = await _db.Invoices
            .Where(i => i.ReservaId == reservaId
                     && i.AnnulmentStatus != AnnulmentStatus.Succeeded
                     && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante)
                     && !LiveInvoiceDebitNoteTypes.Contains(i.TipoComprobante)
                     && !string.IsNullOrEmpty(i.CAE))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        if (activeInvoices.Count == 0)
            throw new BusinessInvariantViolationException(
                "La reserva no tiene una factura activa para anular.",
                invariantCode: "INV-100");

        // Pre-flight por factura: si UNA falla, no se emite NINGUNA (todo-o-nada al frente, premisa #4 del ADR).
        foreach (var invoice in activeInvoices)
        {
            if (IsForeignInvoiceWithoutReliableArcaData(invoice))
            {
                _logger.LogCritical(
                    "ADR-042 PRE-FLIGHT abort: factura {InvoiceId} en moneda {MonId} con cotizacion {MonCotiz} " +
                    "no confiable/no soportada. No se emite NINGUNA NC de la reserva {ReservaId}.",
                    invoice.Id, invoice.MonId, invoice.MonCotiz, reservaId);

                throw new BusinessInvariantViolationException(
                    "Una de las facturas de la reserva esta en moneda extranjera sin una cotizacion confiable " +
                    "(saldria con cotizacion 1, error fiscal). No se emite ninguna nota de credito. Gestionala " +
                    "manualmente: revisa/recarga la cotizacion de esa factura antes de anular.",
                    invariantCode: "INV-156");
            }
        }

        return activeInvoices;
    }

    /// <summary>
    /// ADR-042 §3.5: true si la factura esta en moneda extranjera pero su propio dato ARCA no es emitible
    /// (moneda no soportada, o cotizacion &lt;= 0 o == 1). Es el guard POR FACTURA (usa el MonId/MonCotiz de la
    /// factura, no el snapshot del BC) que generaliza <see cref="IsForeignCurrencyInvoiceWithoutReliableRate"/>
    /// a todas las facturas. Espeja el guard de <c>EnqueueAnnulmentAsync</c> (defensa en profundidad).
    /// </summary>
    private static bool IsForeignInvoiceWithoutReliableArcaData(Invoice invoice)
    {
        bool isForeign =
            !string.IsNullOrWhiteSpace(invoice.MonId)
            && !string.Equals(invoice.MonId, "PES", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(invoice.MonId, "ARS", StringComparison.OrdinalIgnoreCase);
        if (!isForeign) return false;

        // Moneda extranjera: la tratamos como no emitible si no es un codigo ARCA soportado o el TC es dudoso.
        if (!ArcaCurrencyMapper.IsValidArcaCurrencyCode(invoice.MonId)) return true;
        return invoice.MonCotiz <= 0m || invoice.MonCotiz == 1m;
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
        // Moneda del evento: NO pisamos la moneda DECLARADA que ya persistio el confirm (B1 security 2026-07-08).
        // El confirm guarda en PenaltyCurrencyAtEvent la moneda en la que el usuario dijo que el operador retuvo
        // la multa; el gating YA validó que coincida con la moneda de la factura antes de dejar emitir. Pisarla
        // aca borraria esa evidencia. Solo la completamos si quedo VACIA (confirmaciones viejas sin moneda
        // declarada): en ese caso la derivamos de la factura que la ND ajusta, en ISO ("ARS"/"USD") para ser
        // consistentes con el formato que graba el confirm. (Con el gating nuevo este fallback solo se da en
        // facturas en pesos: la divisa sin moneda declarada ya ruteo a revision manual y no llega hasta aca.)
        if (string.IsNullOrWhiteSpace(bc.PenaltyCurrencyAtEvent))
            bc.PenaltyCurrencyAtEvent = ArcaCurrencyMapper.ToIso(originatingInvoice.MonId) ?? Monedas.ARS;
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

    /// <summary>
    /// BLINDAJE (fix 2026-07-01): red de seguridad para cuando la EMISION de la Nota de Debito falla DESPUES de
    /// que la multa ya quedo confirmada (durable). Deja el BC en un estado CONSISTENTE y recuperable — la ND en
    /// <see cref="DebitNoteStatus.ManualReview"/> con un aviso legible — para que la bandeja de NDs por revisar
    /// (y el endpoint retry-debit-note) la puedan destrabar, en vez de que el request explote con un 500 y la
    /// reserva quede a medias.
    ///
    /// <para><b>Descarta el estado parcial</b>: el intento de emision pudo dejar cambios a medio aplicar en el
    /// ChangeTracker (por ejemplo un link a una ND que no llego a persistir). El estado DURABLE ya esta en la
    /// base; limpiamos el tracker y releemos el BC para no arrastrar ese estado parcial (mismo patron
    /// ChangeTracker.Clear que otros flujos de recuperacion).</para>
    ///
    /// <para><b>Solo el caso "ND no vinculada"</b>: si un intento SI alcanzo a vincular una ND (link no nulo), NO
    /// la tocamos — la bandeja la recupera por su estado real (Pending/Failed). Solo marcamos revision manual el
    /// caso link-nulo, que ademas la bandeja ya levanta por <c>PenaltyStatus=Confirmed + DebitNoteInvoiceId=null</c>
    /// independientemente del <c>DebitNoteStatus</c>.</para>
    /// </summary>
    private async Task MarkDebitNoteEmissionForManualReviewAsync(int bookingCancellationId, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();

        var bc = await _db.BookingCancellations.FirstOrDefaultAsync(b => b.Id == bookingCancellationId, ct);
        if (bc is null) return;

        // Si la ND SI quedo vinculada antes del fallo, la bandeja la recupera por su estado real: no la pisamos.
        if (bc.DebitNoteInvoiceId.HasValue) return;

        try
        {
            bc.DebitNoteStatus = DebitNoteStatus.ManualReview;
            bc.DebitNoteArcaErrorMessage =
                "La multa quedo confirmada, pero la Nota de Debito no se pudo emitir automaticamente. " +
                "Quedo pendiente de emision manual.";
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception persistError)
        {
            // Si ni siquiera podemos marcar la revision (base caida, etc.), NO rompemos la respuesta de exito: el
            // BC igual queda recuperable por la bandeja (PenaltyStatus=Confirmed + link nulo la levanta, sin
            // depender del DebitNoteStatus). Solo se pierde la señal fina en la ficha.
            _logger.LogError(persistError,
                "No se pudo marcar la ND en revision manual para BC {BcId}; queda igual recuperable por la bandeja.",
                bookingCancellationId);
        }
    }

    /// <summary>
    /// ADR-044 T3a (2026-07-10, fix B1 confirmacion escalonada, caso (b)): un operador SECUNDARIO confirmo su multa
    /// DESPUES de que la Nota de Debito del principal ya estaba emitida/en vuelo. Ese cargo NO cabe en un comprobante
    /// ya emitido, asi que en vez de perderlo en silencio lo dejamos VISIBLE: marcamos las lineas de ESE operador
    /// con <see cref="DebitNoteStatus.ManualReview"/> + un aviso claro. El read-model
    /// (<see cref="GetOperatorPenaltySituationsAsync"/>) levanta ese marcador y muestra el paso "resolver a mano
    /// (nota de debito complementaria)" para ese operador, sin tocar el estado de la ND del BC padre (que es del
    /// principal y esta bien).
    ///
    /// <para><b>Por que a nivel LINEA y no en el BC padre</b>: el BC padre tiene UN solo slot de ND, que describe
    /// al principal (su ND ya salio). El estado "necesita resolucion manual" es de ESTE operador secundario puntual,
    /// asi que vive en sus lineas — la unica fuente por operador. Deja rastro de auditoria del cargo huerfano.</para>
    /// </summary>
    private async Task MarkSecondaryChargeAsComplementaryManualAsync(
        BookingCancellation bc, int targetSupplierId, string userId, string? userName, CancellationToken ct)
    {
        // Mensaje al USUARIO (aparece en la ficha via el read-model): en criollo, sin jerga, explica el paso.
        const string message =
            "La nota de débito al cliente ya se emitió antes de confirmar este cargo. " +
            "Este cargo adicional se resuelve a mano (nota de débito complementaria).";

        var lines = await _db.BookingCancellationLines
            .Where(l => l.BookingCancellationId == bc.Id && l.SupplierId == targetSupplierId)
            .ToListAsync(ct);
        foreach (var line in lines)
        {
            line.DebitNoteStatus = DebitNoteStatus.ManualReview;
            line.DebitNoteArcaErrorMessage = message;
        }
        await _db.SaveChangesAsync(ct);

        // Auditoria del cargo que quedo para nota de debito complementaria (traza para el contador).
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.BookingCancellationArcaSucceeded,
            entityName: AuditActions.BookingCancellationEntityName,
            entityId: bc.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                bc.PublicId,
                debitNoteAction = "secondary-charge-needs-complementary-debit-note",
                supplierId = targetSupplierId,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        _logger.LogWarning(
            "metric:cancellation_secondary_charge_orphaned | BcPublicId={BcPublicId} Supplier={SupplierId} | " +
            "La ND del principal ya estaba emitida/en vuelo al confirmar este operador; su cargo queda para nota de debito complementaria manual.",
            bc.PublicId, targetSupplierId);
    }

    public async Task OnArcaFailedAsync(int originatingInvoiceId, string? afipErrorMessage, CancellationToken ct)
    {
        // ADR-042 §3.5.1 (2026-07-01): delega al nucleo comun. Actualiza la fila hija de esta factura a
        // Failed y, si ya no quedan Pending, transiciona el BC a ArcaRejected (revision + retry). Las NC que
        // salieron OK NO se revierten. En BCs legacy sin hijas cae al comportamiento historico.
        await HandleArcaAnnulmentCallbackAsync(
            originatingInvoiceId, succeeded: false, creditNoteInvoiceId: 0,
            afipErrorMessage: afipErrorMessage, ct: ct);
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
                // Transición + rastro + descarte de la marca por el PUNTO ÚNICO de transición (path FC1.3 Fase 1, NC total real).
                await ReservaStatusTransitioner.ApplyAsync(
                    _db, bc.Reserva, EstadoReserva.PendingOperatorRefund, "Forward",
                    resolverUserId, resolverUserName,
                    "Cancelacion (ADR-002 / FC1.3 Fase 1): aprobada, a la espera del reembolso del operador.", ct);

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
        {
            // FUGA B1 data-exposure (2026-07-03): el mensaje viaja al usuario via SanitizedConflict —
            // NO nombrar el flag interno. El detalle tecnico va al log.
            _logger.LogWarning("Anulacion rechazada: EnableNewCancellationFlow=false en este ambiente.");
            throw new InvalidOperationException(
                "La anulación de reservas no está disponible en este momento. Consultá con administración.");
        }
    }

    /// <summary>
    /// ADR-015 Fase 1: infiere el UNICO operador (Supplier) que gobierna el evento
    /// fiscal de la cancelacion. Reune los SupplierId DISTINTOS de las 6 fuentes de
    /// servicios de la reserva (la tabla generica "Servicios" + las 5 tablas tipadas)
    /// y decide segun cuantos operadores distintos hay:
    ///
    /// <list type="bullet">
    ///   <item>1 operador  -> se autorresuelve (caso que esta feature desbloquea).</item>
    ///   <item>0 operadores -> error: la reserva no tiene servicios con operador.</item>
    ///   <item>2 o mas      -> INV-152: cancelacion multi-operador todavia no soportada.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Por que bloqueamos multi-operador (no es una limitacion tecnica)</b>: el
    /// operador elegido FIJA el regimen fiscal de la Nota de Credito (Monotributo vs
    /// Responsable Inscripto) y su <c>PenaltyOwnership</c>. Elegir mal el operador en
    /// una reserva con varios = NC con regimen fiscal equivocado. Resolver ese caso
    /// con un selector seguro es trabajo de Fase 2 (ver ADR-015).
    /// </para>
    ///
    /// <para>
    /// <b>No-regresion del path generico</b>: una reserva que hoy se cancela via la
    /// tabla generica con 1 operador sigue resolviendo EXACTAMENTE ese operador,
    /// porque el conjunto distinto de un solo SupplierId es ese mismo SupplierId.
    /// </para>
    /// </summary>
    private async Task<int> InferSingleSupplierIdAsync(Reserva reserva, CancellationToken ct)
    {
        var distinctSupplierIds = await GetDistinctSupplierIdsAsync(reserva.Id, ct);

        if (distinctSupplierIds.Count == 0)
            throw new InvalidOperationException(
                $"La reserva {reserva.NumeroReserva} no tiene servicios con operador asignado. " +
                "Se requiere al menos un servicio con operador para registrar la cancelacion.");

        if (distinctSupplierIds.Count > 1)
        {
            // Metrica/diagnostico: registramos el bloqueo multi-operador para que
            // soporte pueda entender por que una reserva no se deja cancelar (que
            // operadores se detectaron). Los SupplierId son ints, no son PII.
            // El prefijo "metric:" sigue el mismo patron que cancellation_drafted
            // (lo extrae el parser de logs como serie temporal).
            _logger.LogInformation(
                "metric:cancellation_blocked_multi_operator | ReservaId={ReservaId} SupplierIds={SupplierIds}",
                reserva.Id,
                string.Join(",", distinctSupplierIds));

            throw new BusinessInvariantViolationException(
                "Esta reserva tiene servicios de varios operadores. La cancelacion de " +
                "reservas con varios operadores todavia no esta disponible. " +
                "Gestionala manualmente por ahora.",
                invariantCode: "INV-152");
        }

        return distinctSupplierIds[0];
    }

    /// <summary>
    /// ADR-015 Fase 1: junta los SupplierId DISTINTOS de todos los servicios de la
    /// reserva. Hay 6 fuentes porque los servicios conviven en 2 modelos: la tabla
    /// generica historica (<c>ServicioReserva</c>, con SupplierId NULLABLE) y las 5
    /// tablas tipadas (Hotel/Vuelo/Transfer/Paquete/Asistencia, todas con SupplierId
    /// NOT NULL). Los servicios tipados NO escriben una fila espejo en la generica,
    /// asi que hay que consultar cada tabla por separado.
    ///
    /// <para>
    /// Usamos LINQ sobre las propiedades C# (no SQL crudo) para no acoplarnos a los
    /// nombres de columna legacy: la propiedad <c>ReservaId</c> de las tablas tipadas
    /// mapea a la columna fisica <c>TravelFileId</c> en AppDbContext, pero EF Core
    /// resuelve eso por nosotros.
    /// </para>
    ///
    /// <para>
    /// El dedupe por SupplierId vive aca: un mismo operador en 2 hoteles cuenta 1 vez.
    /// </para>
    /// </summary>
    /// <summary>
    /// ADR-025 (DT.2.1 / DT.3.2): construye UNA LINEA por servicio con operador de la reserva. Reemplaza
    /// el viejo <see cref="InferSingleSupplierIdAsync"/> (que bloqueaba multi-operador con INV-152): ahora
    /// una reserva con varios operadores produce varias lineas, una por servicio/operador, todas hijas del
    /// mismo BC (la cara fiscal hacia el cliente sigue siendo UNICA en el padre).
    ///
    /// <para>Recorre las 5 tablas tipadas + la generica (mismo universo que
    /// <see cref="GetDistinctSupplierIdsAsync"/>) y arma una linea por cada servicio que tenga operador.
    /// Cada linea congela <c>(ServiceTable, ServiceId)</c>, <c>SupplierId</c>, <c>Currency</c> y
    /// <c>LineSaleAmount</c>. <paramref name="onlyServiceTable"/>/<paramref name="onlyServiceId"/> filtran
    /// a UN servicio puntual (cancelacion parcial); null = todos los servicios (cancelacion total).</para>
    ///
    /// <para>Tira si no hay ningun servicio con operador (no se puede cancelar lo que no tiene proveedor),
    /// igual que el viejo metodo.</para>
    /// </summary>
    private async Task<List<BookingCancellationLine>> BuildCancellationLinesAsync(
        Reserva reserva,
        BookingCancellationLineScope scope,
        CancellationToken ct,
        CancellableServiceTable? onlyServiceTable = null,
        int? onlyServiceId = null,
        // R1 (plata viva): el guard de "ancla del receivable" llama con false. Una reserva (o servicio) SIN
        // operador no tiene plata pagada al operador -> no hay receivable que anclar -> no hay fuga -> no se
        // bloquea. En ese caso devolvemos la lista vacia (el guard suma RefundCap=0) en lugar de lanzar. El path
        // REAL de cancelacion (que SI necesita al menos una linea para registrar el evento) deja el default true.
        bool throwIfNoOperatorService = true)
    {
        var lines = new List<BookingCancellationLine>();
        // NetCost de cada linea, paralelo a `lines` (mismo indice): se usa para topear el RefundCap por su
        // costo (no se puede devolver mas de lo que costo el servicio). No vive en la entidad: es insumo de
        // calculo del cap, no estado persistido.
        var lineNetCosts = new List<decimal>();
        bool wantsOne = onlyServiceTable.HasValue && onlyServiceId.HasValue;

        // ADR-044 T5 Addendum, Revision 2, fix B1(b) (2026-07-11, corregido C1): tras una cancelacion PARCIAL
        // previa, el servicio ya cancelado NO debe volver a aportar linea/RefundCap cuando se arma una
        // anulacion TOTAL siguiente (doble computo de cap + deuda fantasma del operador). El filtro se aplica
        // UNICAMENTE en alcance Full (equivalente a !wantsOne): el build de UNA sola linea (Scope=Partial,
        // wantsOne=true) construye la linea del servicio que se ACABA DE MARCAR cancelado un paso antes
        // (RecordPartialCancellationLineAsync) — si filtraramos ahi tambien, `builtLines` quedaria vacia y la
        // cancelacion parcial entera rompe (`builtLines[0]` tira). El mapeo de estado reusa EXACTAMENTE el
        // mismo helper que ServiceResolutionRules.IsCancelled (WorkflowStatusHelper): no es una regla nueva,
        // es la MISMA regla aplicada sobre la proyeccion liviana (Status) en vez de la entidad completa.
        bool excludeAlreadyCancelled = !wantsOne;

        // Helper local: agrega una linea si el servicio tiene operador, pasa el filtro de "solo este" y (en
        // alcance Full) todavia no esta cancelado.
        void AddLine(
            CancellableServiceTable table, int serviceId, int? supplierId, string? currency,
            decimal salePrice, decimal netCost, bool isCancelled)
        {
            if (supplierId is null) return;                       // servicio sin operador no genera linea (ni deuda)
            if (wantsOne && (table != onlyServiceTable!.Value || serviceId != onlyServiceId!.Value)) return;
            if (excludeAlreadyCancelled && isCancelled) return;    // B1(b): ya tiene su propia linea Partial

            lines.Add(new BookingCancellationLine
            {
                SupplierId = supplierId.Value,
                ServiceTable = table,
                ServiceId = serviceId,
                Scope = scope,
                Currency = string.IsNullOrWhiteSpace(currency) ? Monedas.ARS : currency!,
                LineSaleAmount = salePrice,
                // RefundCap se completa abajo (CalculateRefundCapsAsync): necesita el pool pagado al operador,
                // que se conoce recien con todas las lineas armadas.
            });
            lineNetCosts.Add(netCost);
        }

        var hotels = await _db.HotelBookings.AsNoTracking()
            .Where(h => h.ReservaId == reserva.Id)
            .Select(h => new { h.Id, h.SupplierId, h.Currency, h.SalePrice, h.NetCost, h.Status })
            .ToListAsync(ct);
        foreach (var h in hotels)
            AddLine(CancellableServiceTable.Hotel, h.Id, h.SupplierId, h.Currency, h.SalePrice, h.NetCost,
                isCancelled: WorkflowStatusHelper.MapGenericStatus(h.Status ?? string.Empty) == WorkflowStatuses.Cancelado);

        var flights = await _db.FlightSegments.AsNoTracking()
            .Where(f => f.ReservaId == reserva.Id)
            .Select(f => new { f.Id, f.SupplierId, f.Currency, f.SalePrice, f.NetCost, f.Status })
            .ToListAsync(ct);
        foreach (var f in flights)
            // El aereo mapea por codigo IATA (MapFlightStatus), no por el mapeo generico de texto libre.
            AddLine(CancellableServiceTable.Flight, f.Id, f.SupplierId, f.Currency, f.SalePrice, f.NetCost,
                isCancelled: WorkflowStatusHelper.MapFlightStatus(f.Status ?? string.Empty) == WorkflowStatuses.Cancelado);

        var transfers = await _db.TransferBookings.AsNoTracking()
            .Where(t => t.ReservaId == reserva.Id)
            .Select(t => new { t.Id, t.SupplierId, t.Currency, t.SalePrice, t.NetCost, t.Status })
            .ToListAsync(ct);
        foreach (var t in transfers)
            AddLine(CancellableServiceTable.Transfer, t.Id, t.SupplierId, t.Currency, t.SalePrice, t.NetCost,
                isCancelled: WorkflowStatusHelper.MapGenericStatus(t.Status ?? string.Empty) == WorkflowStatuses.Cancelado);

        var packages = await _db.PackageBookings.AsNoTracking()
            .Where(p => p.ReservaId == reserva.Id)
            .Select(p => new { p.Id, p.SupplierId, p.Currency, p.SalePrice, p.NetCost, p.Status })
            .ToListAsync(ct);
        foreach (var p in packages)
            AddLine(CancellableServiceTable.Package, p.Id, p.SupplierId, p.Currency, p.SalePrice, p.NetCost,
                isCancelled: WorkflowStatusHelper.MapGenericStatus(p.Status ?? string.Empty) == WorkflowStatuses.Cancelado);

        var assistances = await _db.AssistanceBookings.AsNoTracking()
            .Where(a => a.ReservaId == reserva.Id)
            .Select(a => new { a.Id, a.SupplierId, a.Currency, a.SalePrice, a.NetCost, a.Status })
            .ToListAsync(ct);
        foreach (var a in assistances)
            AddLine(CancellableServiceTable.Assistance, a.Id, a.SupplierId, a.Currency, a.SalePrice, a.NetCost,
                isCancelled: WorkflowStatusHelper.MapGenericStatus(a.Status ?? string.Empty) == WorkflowStatuses.Cancelado);

        // Generico: SupplierId nullable; los que no tienen operador no generan linea.
        var generics = await _db.Servicios.AsNoTracking()
            .Where(s => s.ReservaId == reserva.Id && s.SupplierId != null)
            .Select(s => new { s.Id, s.SupplierId, s.Currency, s.SalePrice, s.NetCost, s.Status })
            .ToListAsync(ct);
        foreach (var s in generics)
            AddLine(CancellableServiceTable.Generic, s.Id, s.SupplierId, s.Currency, s.SalePrice, s.NetCost,
                isCancelled: WorkflowStatusHelper.MapGenericStatus(s.Status ?? string.Empty) == WorkflowStatuses.Cancelado);

        if (lines.Count == 0)
        {
            // Sin operador no hay receivable: el guard R1 (throwIfNoOperatorService=false) recibe la lista vacia y
            // decide "no bloquear" (cap 0), en vez de romper una anulacion/cancelacion legitima de un servicio o
            // reserva sin proveedor asignado.
            if (!throwIfNoOperatorService)
                return lines;

            if (wantsOne)
                throw new InvalidOperationException(
                    $"El servicio indicado de la reserva {reserva.NumeroReserva} no existe o no tiene operador asignado.");

            throw new InvalidOperationException(
                $"La reserva {reserva.NumeroReserva} no tiene servicios con operador asignado. " +
                "Se requiere al menos un servicio con operador para registrar la cancelacion.");
        }

        // SEC-B2: completar el RefundCap de cada linea (lo pagado al operador por ese servicio menos su
        //         penalidad). Sin esto el cap queda en 0 y RefundStatus=Settled nunca es alcanzable.
        await AssignRefundCapsAsync(reserva.Id, lines, lineNetCosts, ct);

        // ADR-044 T2 Addendum, Decision A (2026-07-10): snapshot del modo de facturacion del operador de CADA
        // linea, UNA sola vez, al construirla (mismo momento en que arriba se fijo el default de ConceptKind).
        // Una consulta batch por los SupplierId distintos evita N+1.
        await StampSupplierInvoicingModeAtEventAsync(lines, ct);

        return lines;
    }

    /// <summary>
    /// ADR-044 T2 Addendum, Decision A (2026-07-10): congela <see cref="BookingCancellationLine.SupplierInvoicingModeAtEvent"/>
    /// con el modo de facturacion VIGENTE del operador de cada linea, en el momento en que la linea se construye.
    ///
    /// <para><b>Por que congelar y no leer en vivo</b>: una vez que hubo movimientos de plata reales sobre una
    /// linea (multa retenida, reembolso recibido, cargo facturado aparte), si el admin cambia
    /// <c>Supplier.InvoicingMode</c> DESPUES, una lectura 100% en vivo reinterpretaria el extracto historico sin
    /// que haya cambiado ningun dato real. El snapshot evita eso — misma filosofia que <c>FiscalSnapshot</c>.</para>
    ///
    /// <para>Toda lectura de gate por modo de facturacion usa el patron
    /// <c>line.SupplierInvoicingModeAtEvent ?? line.Supplier.InvoicingMode</c> (fallback vivo), asi que una linea
    /// que por algun motivo no pasara por aca (no deberia) sigue funcionando igual, solo sin el snapshot.</para>
    /// </summary>
    private async Task StampSupplierInvoicingModeAtEventAsync(List<BookingCancellationLine> lines, CancellationToken ct)
    {
        if (lines.Count == 0) return;

        var distinctSupplierIds = lines.Select(l => l.SupplierId).Distinct().ToList();
        var invoicingModeBySupplierId = await _db.Suppliers
            .AsNoTracking()
            .Where(s => distinctSupplierIds.Contains(s.Id))
            .Select(s => new { s.Id, s.InvoicingMode })
            .ToDictionaryAsync(s => s.Id, s => s.InvoicingMode, ct);

        foreach (var line in lines)
        {
            if (invoicingModeBySupplierId.TryGetValue(line.SupplierId, out var invoicingMode))
                line.SupplierInvoicingModeAtEvent = invoicingMode;
        }
    }

    /// <summary>
    /// SEC-B2 (ADR-025 §3.2 / INV-126): asigna el <see cref="BookingCancellationLine.RefundCap"/> de cada
    /// linea = lo pagado al operador por ese servicio, topeado por el costo del servicio (NetCost), menos la
    /// penalidad de la linea, nunca negativo.
    ///
    /// <para><b>Granularidad real del dato</b>: los pagos a proveedor (<see cref="SupplierPayment"/>) se
    /// imputan a nivel operador+reserva (hay <c>ReservaId</c>/<c>ServicioReservaId</c>, pero NO un link por
    /// servicio TIPADO). No existe "pagado por ESTE hotel". Por eso el cap se calcula con un <b>pool por
    /// operador</b>: lo pagado a ese operador imputable a esta reserva, en cada moneda, repartido entre sus
    /// lineas topeando cada una por su propio NetCost. Coherente con la decision #2 (el cap se opera AGREGADO
    /// por operador): la suma de caps de un operador nunca supera lo que se le pago.</para>
    ///
    /// <para><b>Penalidad</b>: al armar las lineas (draft) la penalidad todavia no esta confirmada
    /// (<c>PenaltyStatus=Estimated</c>, <c>PenaltyAmount=null</c>), asi que aca el cap queda en su valor BRUTO
    /// (capBeforePenalty = lo pagado topeado por costo). El descuento de la multa NO ocurre en este metodo: se
    /// aplica cuando el operador confirma la penalidad, en <see cref="AllocateConfirmedPenaltyToLinesAsync"/>
    /// (FASE 0, 2026-06-28) — ese metodo setea <c>PenaltyAmount</c> y recalcula <c>RefundCap = capBeforePenalty −
    /// multa</c>, por moneda, nunca negativo. La linea de abajo (<c>cap = capBeforePenalty − PenaltyAmount</c>)
    /// solo resta una penalidad ya cargada y es defensiva: en el draft normal <c>PenaltyAmount</c> es null (no
    /// resta nada). NO confiar en este metodo para el neteo de la multa: la fuente de verdad es la confirmacion.</para>
    ///
    /// <para><b>Cancelaciones parciales sucesivas</b> (bug-fix): este metodo recibe SOLO la(s) linea(s) que se
    /// cancelan en esta llamada. En el path parcial cada servicio se cancela por separado, asi que antes de
    /// repartir descontamos del pool lo que YA reservaron las lineas Partial persistidas del mismo
    /// operador/moneda en BCs no abortados de esta reserva. Sin esto, dos cancelaciones parciales del mismo
    /// operador reclamaban cada una el pool entero y la suma de caps superaba lo pagado al operador.</para>
    /// </summary>
    // internal (no private) para que los tests unit puedan ejercitar el reparto del cap directamente,
    // incluida la deduccion del pool ya consumido por lineas Partial previas (el camino end-to-end de
    // CancelServiceAsync no puede producir 2 lineas Partial sucesivas por el gap SEC-B1/B1b documentado).
    internal async Task AssignRefundCapsAsync(
        int reservaId,
        List<BookingCancellationLine> lines,
        List<decimal> lineNetCosts,
        CancellationToken ct)
    {
        // Operadores involucrados en estas lineas.
        var supplierIds = lines.Select(l => l.SupplierId).Distinct().ToList();

        // Pool pagado por (operador, moneda) imputable a esta reserva. Pagos vivos (el query filter
        // !IsDeleted ya excluye los soft-deleted). Para un pago cruzado (ImputedCurrency != Currency) usamos
        // el monto/moneda IMPUTADO (lo que efectivamente baja de la deuda en esa moneda); si no cruza, el
        // Amount sobre su Currency. Mismo criterio que SupplierDebtCalculator.
        var paymentRows = await _db.SupplierPayments
            .Where(p => p.ReservaId == reservaId && supplierIds.Contains(p.SupplierId))
            .Select(p => new { p.SupplierId, p.Amount, p.Currency, p.ImputedCurrency, p.ImputedAmount })
            .ToListAsync(ct);

        // pool[(supplierId, currency)] = monto disponible para devolver de ese operador en esa moneda.
        var pool = new Dictionary<(int supplierId, string currency), decimal>();
        foreach (var payment in paymentRows)
        {
            bool isCrossCurrency =
                !string.IsNullOrWhiteSpace(payment.ImputedCurrency)
                && !string.Equals(payment.ImputedCurrency, payment.Currency, StringComparison.OrdinalIgnoreCase);

            string currency = isCrossCurrency ? payment.ImputedCurrency! : payment.Currency;
            decimal amount = isCrossCurrency ? (payment.ImputedAmount ?? 0m) : payment.Amount;

            var key = (payment.SupplierId, currency);
            pool[key] = pool.TryGetValue(key, out var acc) ? acc + amount : amount;
        }

        // BUG-FIX (cancelaciones parciales sucesivas del mismo operador): el pool de arriba junta TODO lo
        // pagado al operador en la reserva, pero esta llamada solo trae la(s) linea(s) NUEVA(s) que se estan
        // cancelando ahora. En el path PARCIAL cada servicio se cancela en una llamada separada: si no
        // descontamos lo que YA reservaron las lineas Partial previas del mismo operador/moneda, cada
        // cancelacion reclama el pool entero y la suma de RefundCap termina superando lo realmente pagado al
        // operador (el cliente quedaria acreditado de mas en OperatorRefundService).
        //
        // Por eso descontamos del pool el "capBeforePenalty" que ya consumieron las lineas EXISTENTES (las
        // persistidas en BCs no abortados de esta reserva). Para una linea ya guardada, ese consumo es su
        // RefundCap MAS lo RETENIDO de su multa (ver el reparto de abajo, que consume capBeforePenalty y no el
        // cap neto). El path TOTAL no llega aca con lineas previas (arma todas juntas), asi que para Full esto
        // es no-op.
        //
        // ADR-044 T2 Addendum (2026-07-10, fix de plata encontrado por investigacion, mismo invariante B1 que
        // ADR-044 T2 corrige en los otros 3 sitios): antes de esta tanda esta reconstruccion usaba PenaltyAmount
        // (eje CLIENTE). Con T2, PenaltyAmount puede incluir montos Withholding/FacturadaAparte que NUNCA
        // salieron del pool (no redujeron RefundCap) — usar PenaltyAmount aca SOBRE-restaria el pool disponible
        // para lineas nuevas del mismo operador/moneda (cancelaciones parciales sucesivas quedarian con menos
        // cupo del que en realidad queda libre). RetainedDeductionAmount es el eje CAJA correcto: exactamente lo
        // que salio del pool.
        var existingLineConsumption = await _db.BookingCancellationLines
            .Where(l => l.BookingCancellation.ReservaId == reservaId
                     && l.BookingCancellation.Status != BookingCancellationStatus.Aborted
                     && supplierIds.Contains(l.SupplierId))
            .Select(l => new { l.SupplierId, l.Currency, l.RefundCap, l.RetainedDeductionAmount })
            .ToListAsync(ct);

        foreach (var existing in existingLineConsumption)
        {
            // capBeforePenalty reconstruido: lo que la linea persistida le saco al pool del operador/moneda.
            decimal alreadyConsumed = existing.RefundCap + existing.RetainedDeductionAmount;
            if (alreadyConsumed <= 0m) continue;

            var key = (existing.SupplierId, existing.Currency);
            if (pool.TryGetValue(key, out var available))
                pool[key] = Math.Max(0m, available - alreadyConsumed);
        }

        // Repartir el pool de cada operador entre sus lineas, topeando cada linea por su NetCost. El orden de
        // las lineas es el de construccion (estable); el reparto no "pierde" plata: lo que no entra por el
        // tope de costo de una linea queda disponible para las demas del mismo operador/moneda.
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            decimal serviceCost = lineNetCosts[i];
            var key = (line.SupplierId, line.Currency);

            decimal available = pool.TryGetValue(key, out var remaining) ? remaining : 0m;

            // El cap de la linea no puede superar ni lo pagado disponible ni lo que costo el servicio.
            decimal capBeforePenalty = Math.Min(available, serviceCost);
            if (capBeforePenalty < 0m) capBeforePenalty = 0m;

            // Penalidad ya confirmada (raro al draft): reduce lo que se devuelve. Nunca cap negativo.
            decimal penalty = line.PenaltyAmount ?? 0m;
            decimal cap = capBeforePenalty - penalty;
            if (cap < 0m) cap = 0m;

            line.RefundCap = cap;

            // FIX D (2026-07-04): aca nace el circuito de reembolso del operador de la linea. El RefundStatus
            // arranca coherente con el cap: cap > 0 significa "se le pago al operador y se espera que devuelva"
            // -> PendingOperatorRefund; cap 0 significa "no hay nada reembolsable" -> None. Antes esta linea
            // quedaba SIEMPRE en None (default), cuyo doc dice "no hay reintegro pendiente" — enganoso para una
            // linea que si esperaba plata. El unico lector de negocio (CloseReservaIfOperatorRefundComplete) solo
            // mira == Settled, asi que esto no cambia el cierre; corrige el significado del campo (bug latente).
            line.RefundStatus = cap > 0m
                ? BookingCancellationLineRefundStatus.PendingOperatorRefund
                : BookingCancellationLineRefundStatus.None;

            // Consumir del pool lo que esta linea reserva (capBeforePenalty, no el cap neto: la penalidad la
            // retiene el operador, no vuelve a estar disponible para otra linea).
            if (pool.ContainsKey(key))
                pool[key] = Math.Max(0m, remaining - capBeforePenalty);
        }
    }

    // (2026-06-26) Delega en el helper compartido SupplierDebtPersister.GetReservaSupplierIdsAsync: la misma
    // logica de "juntar los SupplierId distintos de los 6 tipos de servicio" la usa la anulacion con saldo a
    // favor (caso (3) del flujo "Anular reserva"). Se mantiene este metodo privado como atajo de instancia
    // (usa _db) para no tocar los callers internos (InferSingleSupplierIdAsync).
    private Task<List<int>> GetDistinctSupplierIdsAsync(int reservaId, CancellationToken ct)
        => TravelApi.Infrastructure.Reservations.SupplierDebtPersister.GetReservaSupplierIdsAsync(_db, reservaId, ct);

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
                "La suma de la liquidación fiscal no cuadra con el monto original. " +
                "No se emite la nota de crédito parcial; requiere revisión manual.",
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
                $"Por ahora la nota de crédito parcial solo está disponible en pesos (ARS) y dólares (USD), " +
                $"no en {currency}. La cancelación queda pendiente de revisión manual.");
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
                $"No se puede emitir la nota de crédito parcial en {currency} porque el tipo de cambio " +
                $"registrado no es válido. La cancelación queda pendiente de revisión manual.");
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
                "No se puede emitir la nota de crédito parcial porque la moneda no coincide con la de la " +
                "factura original. La cancelación queda pendiente de revisión manual.");
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
        // Transición + rastro + descarte de la marca por el PUNTO ÚNICO de transición (path F2.2, NC parcial real).
        await ReservaStatusTransitioner.ApplyAsync(
            _db, bc.Reserva, EstadoReserva.PendingOperatorRefund, "Forward",
            resolverUserId, resolverUserName,
            "Cancelacion (ADR-002 / F2.2 NC parcial): aprobada, a la espera del reembolso del operador.", ct);

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
            // ADR-042: hijas (una por factura -> su NC) + la Invoice NC de cada una (para el numero de comprobante).
            .Include(b => b.CreditNotes)
                .ThenInclude(c => c.CreditNoteInvoice)
            .FirstOrDefaultAsync(b => b.Id == bcId, ct);
        if (bc is null) return null;

        // ADR-042 (2026-07-01): armar los read-models multi-factura (lista de facturas de la reserva, estado
        // por NC, saldo a favor por moneda, canRetry). Se hace en helpers para no engordar el mapeo base.
        //
        // B1c (2026-07-02): para distinguir una hija Pending "procesando" (job de ARCA en vuelo) de una
        // "atascada" (Pending sin job), consultamos el AnnulmentStatus de las facturas origen de las hijas.
        // AnnulmentStatus == Pending = job vivo (no ofrecer retry); != Pending = sin job (atascada, ofrecer retry).
        var childOriginatingIds = bc.CreditNotes.Select(c => c.OriginatingInvoiceId).ToList();
        var inFlightOriginatingIds = childOriginatingIds.Count == 0
            ? new List<int>()
            : await _db.Invoices
                .AsNoTracking()
                .Where(i => childOriginatingIds.Contains(i.Id) && i.AnnulmentStatus == AnnulmentStatus.Pending)
                .Select(i => i.Id)
                .ToListAsync(ct);

        var saleInvoicesDto = await BuildSaleInvoicesDtoAsync(bc.Reserva.Id, ct);
        var creditNotesDto = BuildCreditNotesDto(bc);
        var canRetryCreditNotes = EvaluateCanRetryCreditNotes(bc, inFlightOriginatingIds);
        var clientCreditByCurrency = await BuildClientCreditByCurrencyAsync(bc.Id, ct);

        // ADR-014 (read-model, 2026-06-23): pista de UI para el boton "Confirmar multa del
        // operador". Refleja SOLO las precondiciones de ESTADO que valida ConfirmPenaltyAsync
        // (flag maestro, NC total con CAE, idempotencia). NO refleja el permiso ni el 4-eyes:
        // esos los resuelve confirm-penalty al ejecutar. El frontend lo usa para habilitar el
        // boton o, si esta bloqueado, mostrar el aviso correcto sin disparar una llamada que va
        // a rebotar. confirm-penalty revalida TODO server-side, asi que esto es solo una pista.
        var settings = await _settings.GetEntityAsync(ct);
        // La entidad ya esta cargada (con sus Includes): armamos los campos sueltos para la regla compartida.
        var penaltyFields = new PenaltyConfirmabilityFields(
            bc.Status, bc.CreditNoteInvoiceId, bc.PenaltyStatus, bc.DebitNoteInvoiceId, bc.DebitNoteStatus);
        var (canConfirmPenalty, confirmPenaltyBlockedReason) =
            EvaluateCanConfirmPenalty(penaltyFields, settings.EnableCancellationDebitNote);

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
            // B3 gemelo (2026-07-02): el campo del PADRE tambien va SANEADO. En multi-NC ArcaRejected,
            // ReevaluateBcCompleteness copia aca el error crudo de la hija fallida (posible XML/tecnico de ARCA);
            // sin sanear, el mismo ruido llegaria al front por el padre aunque el per-NC ya este saneado. El
            // crudo queda en la entidad (log/auditoria); al front va copy legible. Ver SanitizeArcaErrorForUser.
            ArcaErrorMessage = SanitizeArcaErrorForUser(bc.ArcaErrorMessage),
            // ADR-013/014: estado de la penalidad + de la ND, como string (igual que Status).
            PenaltyStatus = bc.PenaltyStatus.ToString(),
            DebitNoteStatus = bc.DebitNoteStatus.ToString(),
            // ADR-014 (read-model, 2026-06-23): pista de UI para el boton de confirmar multa.
            CanConfirmPenalty = canConfirmPenalty,
            ConfirmPenaltyBlockedReason = confirmPenaltyBlockedReason,
            // ADR-042 (2026-07-01): read-models multi-factura.
            SaleInvoices = saleInvoicesDto,
            CreditNotes = creditNotesDto,
            CanRetryCreditNotes = canRetryCreditNotes,
            ClientCreditByCurrency = clientCreditByCurrency,
        };
    }

    /// <summary>
    /// ADR-042 §3.7 (2026-07-01): lista de las facturas de venta vivas de la reserva para el aviso previo del
    /// panel de anular (tipo legible + numero + moneda ISO + monto). Mismo filtro de "factura de venta viva"
    /// que DraftAsync/pre-flight (excluye NC/ND y filas fantasma sin CAE).
    /// </summary>
    private async Task<List<CancellationSaleInvoiceDto>> BuildSaleInvoicesDtoAsync(int reservaId, CancellationToken ct)
    {
        var invoices = await _db.Invoices
            .AsNoTracking()
            .Where(i => i.ReservaId == reservaId
                     && i.AnnulmentStatus != AnnulmentStatus.Succeeded
                     && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante)
                     && !LiveInvoiceDebitNoteTypes.Contains(i.TipoComprobante)
                     && !string.IsNullOrEmpty(i.CAE))
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new { i.PublicId, i.TipoComprobante, i.PuntoDeVenta, i.NumeroComprobante, i.MonId, i.ImporteTotal })
            .ToListAsync(ct);

        return invoices.Select(i => new CancellationSaleInvoiceDto
        {
            // ADR-044 T4 (2026-07-10): el front necesita este id para poder ELEGIR la factura destino de un
            // cargo del operador (antes esta lista era solo informativa, ver el XML-doc del campo).
            PublicId = i.PublicId,
            ComprobanteLabel = FormatComprobanteLabel(i.TipoComprobante, i.PuntoDeVenta, i.NumeroComprobante),
            // Moneda en ISO legible para el front (ARS/USD). Fallback ARS para datos sin MonId (legacy).
            Currency = ArcaCurrencyMapper.ToIso(i.MonId) ?? "ARS",
            Amount = i.ImporteTotal,
        }).ToList();
    }

    /// <summary>
    /// ADR-042 §3.7 (2026-07-01): estado por NC (una por factura) a partir de las hijas. Moneda en ISO, numero
    /// del comprobante cuando ya salio, y el motivo de AFIP si fallo (info util para el vendedor, aprobado H2).
    /// </summary>
    private static List<BookingCancellationCreditNoteDto> BuildCreditNotesDto(BookingCancellation bc)
    {
        return bc.CreditNotes
            .OrderBy(c => c.Id)
            .Select(c => new BookingCancellationCreditNoteDto
            {
                Currency = ArcaCurrencyMapper.ToIso(c.ArcaCurrency) ?? "ARS",
                Status = c.Status.ToString(),
                NumeroComprobante = c.CreditNoteInvoice != null
                    ? FormatComprobanteLabel(c.CreditNoteInvoice.TipoComprobante, c.CreditNoteInvoice.PuntoDeVenta, c.CreditNoteInvoice.NumeroComprobante)
                    : null,
                // B3 (2026-07-02): solo cuando la NC fallo, y SANEADO. ARCA a veces devuelve XML/errores
                // tecnicos; el crudo queda solo en la entidad (log/auditoria). Al front va un motivo legible
                // por un vendedor (si el rechazo de AFIP es texto plano se muestra tal cual, aprobado H2; si es
                // ruido tecnico se reemplaza por un mensaje generico amable). Ver SanitizeArcaErrorForUser.
                ArcaErrorMessage = c.Status == BookingCancellationCreditNoteStatus.Failed
                    ? SanitizeArcaErrorForUser(c.ArcaErrorMessage)
                    : null,
            })
            .ToList();
    }

    /// <summary>
    /// B3 (ADR-042 §3.7): sanea el mensaje de error de ARCA para mostrarlo al vendedor. Delega en el helper
    /// compartido <see cref="ArcaErrorSanitizer"/> (2026-07-03): un rechazo de AFIP en texto plano se muestra
    /// tal cual (aprobado en H2); XML/excepciones/ruido tecnico -&gt; copy generico. El crudo queda en la
    /// entidad (log/auditoria). <c>internal</c> para testearlo directo desde TravelApi.Tests (InternalsVisibleTo).
    /// </summary>
    internal static string? SanitizeArcaErrorForUser(string? raw) => ArcaErrorSanitizer.SanitizeArcaError(raw);

    /// <summary>
    /// ADR-042 §3.7 (2026-07-01, B1c 2026-07-02): true si la anulacion quedo a medias y se puede reintentar SOLO
    /// las NC faltantes. Vale para: ArcaRejected (siempre que quede algo no-Succeeded), o AwaitingFiscalConfirmation
    /// con alguna hija Failed O una hija Pending ATASCADA (sin job de ARCA en vuelo). NO se ofrece retry cuando
    /// todas las Pending tienen su job vivo (estado "procesando"): reintentar ahi duplicaria jobs.
    /// </summary>
    /// <param name="inFlightOriginatingIds">Ids de facturas origen cuyo AnnulmentStatus == Pending (job vivo).</param>
    private static bool EvaluateCanRetryCreditNotes(BookingCancellation bc, IReadOnlyCollection<int> inFlightOriginatingIds)
    {
        bool hasNonSucceededChild = bc.CreditNotes.Any(c => c.Status != BookingCancellationCreditNoteStatus.Succeeded);
        if (!hasNonSucceededChild) return false;

        if (bc.Status == BookingCancellationStatus.ArcaRejected) return true;

        if (bc.Status == BookingCancellationStatus.AwaitingFiscalConfirmation)
        {
            bool hasFailed = bc.CreditNotes.Any(c => c.Status == BookingCancellationCreditNoteStatus.Failed);
            // Atascada = hija Pending cuyo job de anulacion NO esta en vuelo (AnnulmentStatus != Pending).
            bool hasStuckPending = bc.CreditNotes.Any(c =>
                c.Status == BookingCancellationCreditNoteStatus.Pending
                && !inFlightOriginatingIds.Contains(c.OriginatingInvoiceId));
            return hasFailed || hasStuckPending;
        }

        return false;
    }

    /// <summary>
    /// ADR-042 §3.3.2 (2026-07-01): saldo a favor del cliente por MONEDA generado por esta cancelacion. Suma
    /// el saldo remanente de los <c>ClientCreditEntry</c> del BC agrupado por moneda (nunca se suman monedas).
    /// </summary>
    private async Task<Dictionary<string, decimal>> BuildClientCreditByCurrencyAsync(int bcId, CancellationToken ct)
    {
        var rows = await _db.ClientCreditEntries
            .AsNoTracking()
            .Where(e => e.BookingCancellationId == bcId && e.RemainingBalance > 0m)
            .GroupBy(e => e.Currency)
            .Select(g => new { Currency = g.Key, Total = g.Sum(e => e.RemainingBalance) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Currency, r => r.Total, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ADR-042 (2026-07-01): etiqueta legible de un comprobante para el front ("Factura B 0001-00012345"),
    /// sin exponer IDs internos. El numero se formatea PtoVta(4)-Numero(8) como en los comprobantes de ARCA.
    /// </summary>
    private static string FormatComprobanteLabel(int tipoComprobante, int puntoDeVenta, long numeroComprobante)
    {
        string tipoLabel = tipoComprobante switch
        {
            1 => "Factura A",
            6 => "Factura B",
            11 => "Factura C",
            51 => "Factura M",
            3 => "Nota de credito A",
            8 => "Nota de credito B",
            13 => "Nota de credito C",
            53 => "Nota de credito M",
            2 => "Nota de debito A",
            7 => "Nota de debito B",
            12 => "Nota de debito C",
            52 => "Nota de debito M",
            _ => "Comprobante",
        };
        return $"{tipoLabel} {puntoDeVenta:D4}-{numeroComprobante:D8}";
    }
}
