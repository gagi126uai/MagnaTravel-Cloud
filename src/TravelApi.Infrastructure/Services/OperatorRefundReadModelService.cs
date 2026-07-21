using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations; // SupplierCancellationCircuitReader (formula compartida del receivable)
using TravelApi.Infrastructure.Services.Reservations; // CostMasking

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-041 TANDA 4 (2026-06-28): implementacion del read-model de "reembolsos a cobrar del operador".
/// Ver <see cref="IOperatorRefundReadModelService"/>. SOLO LECTURA: arma las filas a partir de las
/// <see cref="BookingCancellation"/> que estan esperando (o ya se dieron por perdidas esperando) el reintegro.
///
/// <para><b>Por que se proyecta en memoria</b>: el universo de cancelaciones abiertas es chico (back-office) y
/// agrupar las lineas por operador + sumar el reembolso pendiente por moneda es mucho mas legible en LINQ-to-objects
/// que en una GROUP BY traducible. Cargamos solo las cancelaciones en los 2 estados relevantes con sus lineas.</para>
/// </summary>
public class OperatorRefundReadModelService : IOperatorRefundReadModelService
{
    // Ventana de aviso "por vencer": si al plazo le faltan <= estos dias, el semaforo pasa a DueSoon. No hay
    // setting dedicado todavia; 7 dias es un valor operativo razonable (una semana para reclamar al operador).
    private const int DueSoonWindowDays = 7;

    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly IUserPermissionResolver? _permissionResolver;

    public OperatorRefundReadModelService(
        AppDbContext db,
        IHttpContextAccessor? httpContextAccessor = null,
        IUserPermissionResolver? permissionResolver = null)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _permissionResolver = permissionResolver;
    }

    public async Task<IReadOnlyList<OperatorRefundPendingItemDto>> GetSupplierPendingRefundsAsync(
        int supplierId, CancellationToken ct)
    {
        var canSeeCost = await CanSeeCostAsync(ct);
        var cancellations = await LoadOpenCancellationsAsync(
            filterSupplierId: supplierId, ct: ct);
        var serviceCountsAsDebt = await LoadServiceDebtCountingAsync(cancellations, ct);

        var items = new List<OperatorRefundPendingItemDto>();
        foreach (var bc in cancellations)
        {
            // Solo las lineas de ESTE operador (una cancelacion multi-operador tiene lineas de varios).
            var supplierLines = bc.Lines.Where(l => l.SupplierId == supplierId).ToList();
            var item = BuildItem(bc, supplierId, supplierLines, serviceCountsAsDebt, canSeeCost);
            if (item != null) items.Add(item);
        }

        return SortBySeverity(items);
    }

    public async Task<IReadOnlyList<OperatorRefundPendingItemDto>> GetAllPendingRefundsAsync(CancellationToken ct)
    {
        var canSeeCost = await CanSeeCostAsync(ct);
        var cancellations = await LoadOpenCancellationsAsync(filterSupplierId: null, ct: ct);
        var serviceCountsAsDebt = await LoadServiceDebtCountingAsync(cancellations, ct);

        var items = new List<OperatorRefundPendingItemDto>();
        foreach (var bc in cancellations)
        {
            // Una fila por operador: agrupamos las lineas por SupplierId. Asi cada operador que debe reembolsar en
            // esta cancelacion aparece como su propia cuenta por cobrar.
            var bySupplier = bc.Lines
                .Where(l => l.SupplierId != 0)
                .GroupBy(l => l.SupplierId);

            foreach (var group in bySupplier)
            {
                var item = BuildItem(bc, group.Key, group.ToList(), serviceCountsAsDebt, canSeeCost);
                if (item != null) items.Add(item);
            }
        }

        return SortBySeverity(items);
    }

    /// <summary>
    /// Tanda P2 "circuito proveedor" (2026-07-22): reembolsos YA REGISTRADOS de un operador (una fila por
    /// <c>OperatorRefundAllocation</c>). Ver <see cref="IOperatorRefundReadModelService.GetSupplierRegisteredRefundsAsync"/>.
    ///
    /// <para><b>Por que se proyecta directo a SQL (a diferencia del read-model de pendientes de arriba)</b>: aca no
    /// hay que agrupar lineas ni derivar semaforos — es una proyeccion 1 a 1 de la allocation con sus 2 joins
    /// (reserva, cliente). Se puede traducir entero a SQL, asi que paginamos en la base (mismo patron que
    /// <c>GetSupplierAccountPaymentsAsync</c>) en vez de traer todo a memoria.</para>
    ///
    /// <para><b>Sin filtro de query filter</b>: <c>OperatorRefundAllocation</c> NO tiene <c>HasQueryFilter</c> de
    /// soft-delete en <c>AppDbContext</c> (verificado) — el "borrado" de una allocation es el flag propio
    /// <c>IsVoided</c>, no un soft-delete global. Por eso esta consulta trae las deshechas SIN necesitar
    /// <c>IgnoreQueryFilters</c>: no hay filtro que las esconda.</para>
    /// </summary>
    public async Task<PagedResponse<OperatorRefundRegisteredItemDto>> GetSupplierRegisteredRefundsAsync(
        int supplierId, OperatorRefundRegisteredQuery query, CancellationToken ct)
    {
        var canSeeCost = await CanSeeCostAsync(ct);

        var allocationsQuery = _db.OperatorRefundAllocations
            .AsNoTracking()
            .Where(a => a.Refund.SupplierId == supplierId)
            .Select(a => new OperatorRefundRegisteredItemDto
            {
                PublicId = a.PublicId,
                RefundReceivedPublicId = a.Refund.PublicId,
                ReservaPublicId = a.BookingCancellation.Reserva.PublicId,
                NumeroReserva = a.BookingCancellation.Reserva.NumeroReserva,
                ClienteNombre = a.BookingCancellation.Customer.FullName,
                ClientePublicId = a.BookingCancellation.Customer.PublicId,
                // La allocation no tiene columna de moneda propia: hereda la del ingreso padre (ver el
                // comentario de OperatorRefundAllocation en el dominio). La normalizamos DESPUES de traer la
                // pagina (ver el loop de abajo): Monedas.Normalizar no es un metodo que EF pueda traducir a
                // SQL dentro del Select, y este mismo patron (normalizar en memoria, no en el arbol de
                // expresion) ya lo usa el read-model de pendientes de arriba.
                Currency = a.Refund.Currency,
                NetAmount = a.NetAmount,
                RegisteredAt = a.CreatedAt,
                IsVoided = a.IsVoided,
                VoidedAt = a.VoidedAt,
                VoidedReason = a.VoidedReason,
            });

        allocationsQuery = ApplyOperatorRefundRegisteredOrdering(allocationsQuery, query);
        var page = await allocationsQuery.ToPagedResponseAsync(query, ct);

        foreach (var item in page.Items)
        {
            // Paridad con el read-model de pendientes (Monedas.Normalizar): blinda monedas legacy raras
            // (minusculas, con espacios) y deja todo en el mismo formato ISO que espera el frontend.
            item.Currency = Monedas.Normalizar(item.Currency);

            // Saneo de datos LEGACY (gate de exposicion de datos, 2026-07-22): antes de este fix,
            // OperatorRefundService.ReassociateAllocationAsync guardaba el motivo del soft-void con el
            // prefijo en ingles "Reassociate: ". Esas filas YA estan escritas en la base con ese texto; el
            // fix de origen (prefijo "Corrección de reserva: ") solo aplica hacia adelante. Lo saneamos aca,
            // en memoria despues de traer la pagina, para que la pantalla NUNCA muestre jerga de codigo aunque
            // el dato sea viejo — sin tener que correr una migracion de datos para esto.
            const string legacyReassociatePrefix = "Reassociate: ";
            if (item.VoidedReason != null && item.VoidedReason.StartsWith(legacyReassociatePrefix, StringComparison.Ordinal))
            {
                item.VoidedReason = "Corrección de reserva: " + item.VoidedReason[legacyReassociatePrefix.Length..];
            }
        }

        // ADR-017 F1b: NetAmount es plata del lado costo (lo que el operador termino devolviendo). Sin
        // cobranzas.see_cost se enmascara a 0; el resto de la fila (reserva, cliente, fecha, deshecho) sigue
        // visible igual que en el resto de la cuenta del proveedor.
        if (!canSeeCost)
        {
            foreach (var item in page.Items)
            {
                item.NetAmount = 0m;
                item.AmountsMasked = true;
            }
        }

        return page;
    }

    /// <summary>Orden de "reembolsos ya registrados": por defecto mas nuevas primero (lo recien cargado arriba).</summary>
    private static IQueryable<OperatorRefundRegisteredItemDto> ApplyOperatorRefundRegisteredOrdering(
        IQueryable<OperatorRefundRegisteredItemDto> query, OperatorRefundRegisteredQuery request)
    {
        var desc = request.IsSortDescending();
        return desc
            ? query.OrderByDescending(item => item.RegisteredAt).ThenByDescending(item => item.PublicId)
            : query.OrderBy(item => item.RegisteredAt).ThenBy(item => item.PublicId);
    }

    /// <summary>
    /// Carga en batch (6 queries por tabla, no N+1) si el servicio de cada linea todavia cuenta como compra
    /// confirmada del operador. Reusa el MISMO batch-loader del extracto (<see cref="SupplierCancellationCircuitReader"/>)
    /// para que la solapa "Reembolsos" y el "me tiene que devolver" del extracto salgan del mismo calculo.
    /// </summary>
    private Task<Dictionary<(CancellableServiceTable Table, int ServiceId), bool>> LoadServiceDebtCountingAsync(
        List<BookingCancellation> cancellations, CancellationToken ct)
    {
        var allLines = cancellations.SelectMany(bc => bc.Lines).ToList();
        return SupplierCancellationCircuitReader.LoadServiceDebtCountingAsync(_db, allLines, ct);
    }

    /// <summary>
    /// Carga las cancelaciones que pueden aportar a la solapa "Reembolsos" con sus lineas, reserva, cliente y los
    /// operadores. Si <paramref name="filterSupplierId"/> viene, solo trae las que tienen al menos una linea de ese
    /// operador.
    ///
    /// <para><b>Alcance (RESTOS, 2026-07-03)</b>: ya NO cargamos solo los 2 estados "esperando/abandonada". Para
    /// que el TOTAL por moneda de la solapa cuadre por CONSTRUCCION con el "me tiene que devolver" del extracto
    /// (<see cref="SupplierCancellationCircuitReader.LoadAsync"/>, que barre TODAS las cancelaciones no abortadas),
    /// traemos toda cancelacion NO abortada que:
    /// <list type="bullet">
    ///   <item>este esperando o abandonada (fila activa, aunque el estimado de $0 por multa-cubre-todo), o</item>
    ///   <item>tenga alguna linea con RESIDUO (<c>RefundCap &gt; ReceivedRefundAmount</c>) — condicion NECESARIA
    ///     de un receivable vivo, asi que este pre-filtro nunca descarta una fila que sume al extracto.</item>
    /// </list>
    /// El pre-filtro por residuo es en SQL (barato); el receivable REAL (atado al servicio cancelado) se decide
    /// despues en memoria con la formula compartida, y las filas sin receivable real se ocultan en
    /// <see cref="BuildItem"/>. Asi acotamos el barrido sin arriesgar el cuadre.</para>
    /// </summary>
    private async Task<List<BookingCancellation>> LoadOpenCancellationsAsync(
        int? filterSupplierId, CancellationToken ct)
    {
        var query = _db.BookingCancellations
            .AsNoTracking()
            .Include(bc => bc.Reserva)
            .Include(bc => bc.Customer)
            .Include(bc => bc.Lines).ThenInclude(l => l.Supplier)
            .Where(bc => bc.Status != BookingCancellationStatus.Aborted)
            .Where(bc => bc.Status == BookingCancellationStatus.AwaitingOperatorRefund
                      || bc.Status == BookingCancellationStatus.AbandonedByOperator
                      || bc.Lines.Any(l => l.RefundCap > l.ReceivedRefundAmount));

        if (filterSupplierId.HasValue)
        {
            var sid = filterSupplierId.Value;
            query = query.Where(bc => bc.Lines.Any(l => l.SupplierId == sid));
        }

        return await query.ToListAsync(ct);
    }

    /// <summary>
    /// Arma una fila para una cancelacion y el conjunto de lineas de UN operador. Devuelve null si el conjunto no
    /// tiene operador resoluble (defensivo) o si la fila no corresponde mostrarla.
    ///
    /// <para><b>Que se muestra (RESTOS, 2026-07-03)</b>: la fila aparece si el operador todavia debe algo (residuo
    /// vivo, receivable &gt; 0 con la formula compartida del extracto) O si la cancelacion esta activamente
    /// esperando / abandonada (aunque el estimado sea $0 por multa-cubre-todo: es info util). Toda fila con
    /// receivable &gt; 0 se muestra, y las de $0 no suman -> el TOTAL por moneda de la solapa == "me tiene que
    /// devolver" del extracto por CONSTRUCCION.</para>
    ///
    /// <para>El estimado por moneda = <c>max(0, RefundCap - Recibido)</c> de las lineas cuyo servicio ya se cancelo
    /// (misma regla que el extracto), ENMASCARADO si el caller no puede ver costos.</para>
    /// </summary>
    private OperatorRefundPendingItemDto? BuildItem(
        BookingCancellation bc,
        int supplierId,
        List<BookingCancellationLine> supplierLines,
        IReadOnlyDictionary<(CancellableServiceTable Table, int ServiceId), bool> serviceCountsAsDebt,
        bool canSeeCost)
    {
        if (supplierLines.Count == 0) return null;

        var supplier = supplierLines.Select(l => l.Supplier).FirstOrDefault(s => s != null);
        if (supplier == null) return null;

        // Receivable REAL (sin enmascarar) con la MISMA formula que el extracto: decide si la fila se muestra y su
        // estado. El enmascarado se aplica despues, solo a los montos del desglose.
        decimal realReceivable = supplierLines.Sum(l =>
            SupplierCancellationCircuitReader.LiveReceivableForLine(l, bc, serviceCountsAsDebt, supplierId, logger: null));
        decimal realReceived = supplierLines.Sum(l => l.ReceivedRefundAmount);

        bool isActiveAwaiting = bc.Status == BookingCancellationStatus.AwaitingOperatorRefund
                             || bc.Status == BookingCancellationStatus.AbandonedByOperator;

        // Regla de visibilidad (ver el resumen del metodo): sin residuo vivo y sin estar esperando/abandonada, la
        // fila no aporta nada -> no se muestra. Nunca ocultamos una fila con receivable > 0 (cuadre garantizado).
        if (realReceivable <= 0m && !isActiveAwaiting) return null;

        var semaphore = DeriveSemaphore(bc.Status, bc.OperatorRefundDueBy, DateTime.UtcNow);
        var daysOverdue = ComputeDaysOverdue(bc.OperatorRefundDueBy, DateTime.UtcNow);

        // Desglose por moneda (cuenta del operador). Se agrupa por moneda NORMALIZADA (misma que el extracto, para
        // no partir "US$"/"USD" en dos buckets) y se computa con los montos REALES; el enmascarado se aplica al
        // final SOLO a los montos (no al motivo del $0). El desglose usa la MISMA regla de elegibilidad que el
        // receivable, asi el invariante "Estimado = Pagado - Multa - Recibido" cierra exacto.
        var estimatedByCurrency = supplierLines
            .GroupBy(l => Monedas.Normalizar(l.Currency))
            .Select(g => BuildEstimatedForCurrency(g.Key, g.ToList(), canSeeCost, serviceCountsAsDebt, supplierId, bc))
            .OrderBy(e => e.Currency, StringComparer.Ordinal)
            .ToList();

        return new OperatorRefundPendingItemDto
        {
            BookingCancellationPublicId = bc.PublicId,
            ReservaPublicId = bc.Reserva?.PublicId ?? Guid.Empty,
            NumeroReserva = bc.Reserva?.NumeroReserva ?? string.Empty,
            ClienteNombre = bc.Customer?.FullName ?? string.Empty,
            SupplierPublicId = supplier.PublicId,
            SupplierName = supplier.Name,
            Semaphore = semaphore,
            OperatorRefundDueBy = bc.OperatorRefundDueBy,
            DaysOverdue = daysOverdue,
            EstimatedRefundsByCurrency = estimatedByCurrency,
            AmountsMasked = !canSeeCost,
            // Decision 2 / P2: la multa de esta cancelacion sigue sin confirmar (ni Confirmed ni Waived). La
            // confirmacion vive en el PADRE (bc.PenaltyStatus); line.PenaltyStatus no se setea (ver el circuit
            // reader). NO es costo -> visible siempre.
            PenaltyPendingConfirmation = bc.PenaltyStatus == PenaltyStatus.Estimated,
            RowStatus = DeriveRowStatus(bc.Status, realReceived),
            // Capacidad REAL del endpoint de registrar reembolso recibido (INV-093): solo esos 2 estados lo aceptan.
            CanRegisterRefund = bc.Status == BookingCancellationStatus.AwaitingOperatorRefund
                             || bc.Status == BookingCancellationStatus.ClientCreditApplied,
            // FIX A (2026-07-04): habilita el botón "Registrar reembolso tardío" del front. Se puede reabrir una
            // cancelacion abandonada (siempre) o una CERRADA CON RESIDUO real (el operador devolvio de menos y
            // quedo plata viva por cobrar). Usamos el MISMO receivable que decide la visibilidad de la fila
            // (realReceivable, formula compartida del extracto), asi "cerrada con resto" y "reabrible" no divergen.
            CanReopenForLateRefund = bc.Status == BookingCancellationStatus.AbandonedByOperator
                                  || (bc.Status == BookingCancellationStatus.Closed && realReceivable > 0m),
        };
    }

    /// <summary>
    /// Cuenta del operador (2026-07-03, RESTOS): rotula la fila para el front. El orden importa: abandonada y
    /// cerrada mandan por estado; si no, "parcialmente devuelto" cuando ya entro algo; "esperando" cuando espera
    /// sin haber recibido nada; y "en proceso" para el resto (anulacion sin la NC confirmada todavia). Static +
    /// puro para testearlo sin DB.
    /// </summary>
    internal static OperatorRefundRowStatus DeriveRowStatus(BookingCancellationStatus status, decimal received)
    {
        if (status == BookingCancellationStatus.AbandonedByOperator)
            return OperatorRefundRowStatus.Abandoned;

        if (status == BookingCancellationStatus.Closed)
            return OperatorRefundRowStatus.ClosedWithResidue;

        if (received > 0m)
            return OperatorRefundRowStatus.PartiallyRefunded;

        if (status == BookingCancellationStatus.AwaitingOperatorRefund)
            return OperatorRefundRowStatus.AwaitingRefund;

        return OperatorRefundRowStatus.InProcess;
    }

    /// <summary>
    /// Cuenta del operador (2026-07-03): arma el desglose de UNA moneda para las lineas de UN operador.
    ///
    /// <para><b>Elegibilidad (RESTOS)</b>: cuando vienen <paramref name="serviceCountsAsDebt"/> y
    /// <paramref name="bc"/>, el desglose solo considera las lineas cuyo servicio YA dejo de contar como compra
    /// (== su caja cayo), con la MISMA regla que el extracto
    /// (<see cref="SupplierCancellationCircuitReader.IsReceivableEligible"/>). Una linea con servicio todavia vivo
    /// tiene cap pero receivable 0, y sumarla romperia el cierre. Sin esos parametros (tests unit del desglose)
    /// todas las lineas cuentan como elegibles.</para>
    ///
    /// <para><b>Invariante (verificado en codigo y tests)</b>:
    /// <c>EstimatedAmount == PaidToOperator - PenaltyRetained - AmountReceived</c>. Se cumple EXACTO por
    /// construccion sobre las lineas elegibles:
    /// <list type="bullet">
    ///   <item><c>PenaltyRetained = sum(line.RetainedDeductionAmount)</c> — la multa que YA redujo el RefundCap
    ///     (ADR-044 T2 Addendum: eje CAJA, columna fisica separada de <c>PenaltyAmount</c>/eje CLIENTE desde esta
    ///     tanda). Solo se setea para penalidad PASS-THROUGH confirmada, y el share se topea al cap
    ///     (<c>AllocateConfirmedPenaltyToLinesAsync</c>), asi que <c>RefundCap + RetainedDeductionAmount ==
    ///     capBeforePenalty</c> siempre (incluso cuando la multa se come todo -> RefundCap 0,
    ///     RetainedDeductionAmount == capBeforePenalty). Withholding/FacturadaAparte nunca entran aca.</item>
    ///   <item><c>PaidToOperator = sum(RefundCap) + PenaltyRetained == sum(capBeforePenalty)</c> — la base
    ///     reembolsable pagada (topeada al costo del servicio), NO el bruto pagado.</item>
    ///   <item><c>AmountReceived = sum(min(ReceivedRefundAmount, RefundCap))</c> — lo que el operador ya devolvio,
    ///     TOPEADO por linea a la base reembolsable. El tope existe porque <c>DistributeReceivedRefundToOperatorLines</c>
    ///     deja que la ULTIMA linea absorba reembolso por ENCIMA de su cap (para no perder plata recibida): en ese
    ///     caso el crudo <c>sum(ReceivedRefundAmount)</c> podria superar a PaidToOperator y la cuenta
    ///     "Pagaste - Multa - Ya te devolvio" del front quedaria visiblemente inconsistente (review backend
    ///     2026-07-03). El crudo sin topear sigue alimentando RowStatus/FullyRefunded; solo el DTO muestra el topeado.
    ///     En las filas con residuo (parcialmente devuelto / cerrado con resto) es &gt; 0.</item>
    ///   <item><c>EstimatedAmount = sum(max(0, RefundCap - ReceivedRefundAmount))</c>. Por linea,
    ///     <c>max(0, cap - recibido) == cap - min(recibido, cap)</c>, asi que <c>EstimatedAmount == sum(RefundCap)
    ///     - AmountReceived == PaidToOperator - PenaltyRetained - AmountReceived</c> EXACTO por construccion,
    ///     tambien con reembolso parcial o con sobre-reembolso en una linea.</item>
    /// </list></para>
    /// </summary>
    internal static OperatorRefundEstimatedAmountDto BuildEstimatedForCurrency(
        string currency, List<BookingCancellationLine> currencyLines, bool canSeeCost,
        IReadOnlyDictionary<(CancellableServiceTable Table, int ServiceId), bool>? serviceCountsAsDebt = null,
        int supplierId = 0,
        BookingCancellation? bc = null)
    {
        // Solo las lineas cuyo servicio ya se cancelo entran al desglose reembolsable (ver el resumen). Sin mapa
        // (tests unit del desglose) todas cuentan como elegibles.
        List<BookingCancellationLine> eligible = (serviceCountsAsDebt == null || bc == null)
            ? currencyLines
            : currencyLines
                .Where(l => SupplierCancellationCircuitReader.IsReceivableEligible(
                    l, bc, serviceCountsAsDebt, supplierId, logger: null))
                .ToList();

        // Multa retenida = lo que ya redujo el RefundCap. ADR-044 T2 Addendum (2026-07-10): usamos
        // line.RetainedDeductionAmount (eje CAJA fisico) en vez de line.PenaltyAmount (eje CLIENTE): desde esta
        // tanda PenaltyAmount puede incluir montos Withholding/FacturadaAparte que NUNCA redujeron el RefundCap,
        // asi que sumarlo aca sobreestimaria "lo que ya redujo el reembolso" y rompería la reconstruccion de
        // capBeforePenalty de abajo (ver invariante B1 del Addendum).
        decimal penaltyRetainedReal = eligible.Sum(l => l.RetainedDeductionAmount);
        // Reembolso ya recibido del operador (atribuido a estas lineas). El CRUDO decide el motivo del $0
        // (FullyRefunded); el MOSTRADO se topea por linea al cap para que la cuenta del front cierre exacta
        // aun con sobre-reembolso (la ultima linea puede absorber mas que su cap — ver el resumen).
        decimal receivedReal = eligible.Sum(l => l.ReceivedRefundAmount);
        decimal receivedShown = eligible.Sum(l => Math.Min(l.ReceivedRefundAmount, l.RefundCap));
        // Estimado vivo = suma del residuo por linea max(0, cap - recibido). El estimado nunca es negativo.
        decimal estimatedReal = eligible.Sum(l => Math.Max(0m, l.RefundCap - l.ReceivedRefundAmount));
        // Base reembolsable pagada (capBeforePenalty) = cap neto + multa retenida (reconstruccion exacta).
        decimal paidToOperatorReal = eligible.Sum(l => l.RefundCap) + penaltyRetainedReal;

        // Motivo del $0 (P4), derivado de los montos REALES (no del enmascarado). Null si hay algo para devolver.
        string? zeroReason = null;
        if (estimatedReal == 0m)
        {
            if (paidToOperatorReal == 0m)
                // No se pago nada al operador -> no hay base para devolver.
                zeroReason = nameof(OperatorRefundZeroReason.NothingPaidToOperator);
            else if (receivedReal > 0m)
                // Se pago y el operador ya devolvio todo lo reembolsable (no quedo residuo).
                zeroReason = nameof(OperatorRefundZeroReason.FullyRefunded);
            else
                // Se pago, no devolvio nada, y la multa retenida se quedo con todo lo reembolsable.
                zeroReason = nameof(OperatorRefundZeroReason.PenaltyCoversAll);
        }

        return new OperatorRefundEstimatedAmountDto
        {
            Currency = currency,
            // Montos = COSTO -> enmascarados a 0 sin cobranzas.see_cost (mismo patron que ya usaba EstimatedAmount).
            EstimatedAmount = canSeeCost ? estimatedReal : 0m,
            PaidToOperator = canSeeCost ? paidToOperatorReal : 0m,
            PenaltyRetained = canSeeCost ? penaltyRetainedReal : 0m,
            AmountReceived = canSeeCost ? receivedShown : 0m,
            // Security review (2026-07-03): el motivo es CUALITATIVO sobre costos ("PenaltyCoversAll" revela que
            // hubo multa >= lo pagado). Sin cobranzas.see_cost se enmascara TAMBIEN (null): ese usuario ya veia "—",
            // cero perdida funcional. Enmascarado completo en el borde del servidor, no confiamos en que el front tape.
            ZeroRefundReason = canSeeCost ? zeroReason : null,
        };
    }

    /// <summary>
    /// Deriva el semaforo. <c>AbandonedByOperator</c> siempre es Abandonado. Para las que esperan refund:
    /// sin plazo = A tiempo; vencido = Vencido; dentro de la ventana de aviso = Por vencer; resto = A tiempo.
    /// Static + puro para poder testearlo sin DB.
    /// </summary>
    internal static OperatorRefundPendingSemaphore DeriveSemaphore(
        BookingCancellationStatus status, DateTime? dueBy, DateTime nowUtc)
    {
        if (status == BookingCancellationStatus.AbandonedByOperator)
            return OperatorRefundPendingSemaphore.Abandoned;

        if (dueBy is null)
            return OperatorRefundPendingSemaphore.OnTime;

        if (nowUtc > dueBy.Value)
            return OperatorRefundPendingSemaphore.Overdue;

        if (nowUtc >= dueBy.Value.AddDays(-DueSoonWindowDays))
            return OperatorRefundPendingSemaphore.DueSoon;

        return OperatorRefundPendingSemaphore.OnTime;
    }

    /// <summary>Dias corridos desde que vencio el plazo (&gt;= 0). 0 si no hay plazo o todavia no vencio.</summary>
    internal static int ComputeDaysOverdue(DateTime? dueBy, DateTime nowUtc)
    {
        if (dueBy is null || nowUtc <= dueBy.Value) return 0;
        return Math.Max(0, (int)(nowUtc.Date - dueBy.Value.Date).TotalDays);
    }

    /// <summary>Ordena por severidad (Abandonado/Vencido primero) y, dentro, por dias vencido desc + numero de reserva.</summary>
    private static IReadOnlyList<OperatorRefundPendingItemDto> SortBySeverity(
        List<OperatorRefundPendingItemDto> items)
    {
        return items
            .OrderByDescending(i => (int)i.Semaphore)
            .ThenByDescending(i => i.DaysOverdue)
            .ThenBy(i => i.NumeroReserva, StringComparer.Ordinal)
            .ToList();
    }

    private Task<bool> CanSeeCostAsync(CancellationToken ct)
        => CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);
}
