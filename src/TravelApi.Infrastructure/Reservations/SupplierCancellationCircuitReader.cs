using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// Pasos B/C "cuenta del operador" (2026-06-29): lee, para UN operador, el "Circuito de cancelacion" derivado
/// del estado de sus cancelaciones — la MULTA que retuvo (pass-through confirmada), el REEMBOLSO que ya devolvio
/// y el RECEIVABLE pendiente "me tiene que devolver" (Y) — por moneda. Es la fuente UNICA de estos tres terminos:
/// la usan tanto el EXTRACTO del operador (<c>SupplierService.GetSupplierAccountStatementAsync</c>) como el
/// RECONCILER del saldo a favor (<c>SupplierCreditReconciler</c>), asi los dos numeros que el dueño VE y el pool
/// de plata que se MATERIALIZA salen del mismo calculo (no pueden divergir).
///
/// <para><b>Alcance temporal de cada termino</b> (diseño rev 2 §4.2 — esto es lo que evita re-mintear la fuga):
/// <list type="bullet">
/// <item><b>MultaRetenida</b> y <b>ReembolsoRecibido</b>: sobre TODAS las cancelaciones NO abortadas del operador,
/// incluida <c>Closed</c>. Son la contrapartida PERMANENTE del pago negativo que dejo la anulacion; si se fueran
/// al cerrar la BC, el pago seguiria negativo y el reconciler re-mintearia el sobrepago como saldo a favor.</item>
/// <item><b>Y (receivable)</b>: el receivable de operador todavia VIVO = <c>max(0, RefundCap - Recibido)</c> por
/// linea, contado SIEMPRE que la cancelacion ya tomo efecto (servicios cancelados -> caja negativa). Se auto-anula
/// cuando el operador reembolso todo; vale el residuo cuando reembolso de menos.</item>
/// </list></para>
///
/// <para><b>INVARIANTE DE RAIZ del scope de Y (bloqueante de review, plata viva, 2026-06-29)</b>: la caja del
/// operador se vuelve NEGATIVA cuando un servicio pagado DEJA DE CONTAR como compra confirmada al cancelarse (el
/// pago sigue, la compra cae). Para que la formula <c>Prepayment = max(0, -(Balance + Multa + Reembolso + Y))</c>
/// NUNCA mintee plata gastable, el receivable Y de una linea tiene que contar EXACTAMENTE cuando su servicio dejo de
/// respaldar el pago. Por eso NO inferimos "caja negativa" desde <c>bc.Status</c> (allow-list o deny-list por estado
/// SIEMPRE puede fallar un estado: p.ej. <c>Drafted</c> vale a la vez para un draft de cancelacion TOTAL con
/// servicios VIVOS —caja normal— y para un draft de cancelacion PARCIAL con su servicio YA cancelado —caja
/// negativa—). En su lugar atamos Y al MISMO predicado que produce la caja: el receivable de una linea cuenta SI Y
/// SOLO SI su servicio subyacente NO cuenta como compra confirmada
/// (<c>!WorkflowStatusHelper.CountsForSupplierDebtByType(tipo, estado)</c>, la misma fuente que
/// <c>SupplierDebtPersister</c>). Asi <b>(caja negativa por el servicio) ⟺ (Y cuenta el receivable de su linea)</b>
/// por CONSTRUCCION, en todos los estados presentes y futuros, sin enumerar <c>bc.Status</c> nunca mas.</para>
///
/// <para><b>Consecuencias verificadas</b>: total <c>ConfirmAsync</c> (servicio Cancelado -> cuenta), cancelacion
/// PARCIAL via <c>CancelServiceAsync</c> que deja la BC en <c>Drafted</c> con el servicio YA cancelado (cuenta, NO
/// mintea), <c>ArcaRejected</c> (cancelado -> cuenta), <c>Closed</c> sub-reembolsada (cancelado -> el residuo
/// <c>cap-recibido</c> cuenta como "me tiene que devolver"), <c>Closed</c> totalmente reembolsada (cancelado pero
/// <c>recibido==cap</c> -> Y=0), cierre sin multa (cancelado -> cuenta), y draft de cancelacion TOTAL NO confirmado
/// (servicios VIVOS -> NO cuenta, sin falso receivable). El cierre de la pata CLIENTE
/// (<c>OnAllCreditConsumedAsync</c>) NO salda la pata OPERADOR: mientras el servicio este cancelado y
/// <c>Recibido &lt; RefundCap</c>, el residuo sigue siendo receivable vivo, NO saldo a favor.</para>
///
/// <para><b>Destino contable del residuo no reembolsado</b>: la baja a perdida vs el reclamo es una decision de
/// NEGOCIO (follow-up del dueño). Aca el residuo se preserva como receivable VIVO y reversible, NUNCA como saldo a
/// favor consumible.</para>
///
/// <para><b>Atribucion por operador y moneda</b>: tanto el reembolso recibido como Y se leen de
/// <c>BookingCancellationLine.ReceivedRefundAmount</c> / <c>RefundCap</c> de las lineas de ESTE operador, agrupados
/// por <c>line.Currency</c> (la moneda del servicio cancelado). Es la atribucion por operador correcta (un ingreso
/// se reparte a las lineas del operador en <c>DistributeReceivedRefundToOperatorLines</c>), y mantiene el cuadre por
/// moneda con la multa: <c>AllocateConfirmedPenaltyToLinesAsync</c> solo setea <c>PenaltyAmount</c> en lineas cuya
/// <c>line.Currency</c> coincide con la moneda de la multa, asi que agrupar la multa por <c>line.Currency</c> es
/// identico a agruparla por <c>PenaltyCurrency</c> para toda linea que de verdad tenga multa, y cae en el MISMO
/// bucket de moneda que su Y (net-neutral por moneda).</para>
/// </summary>
internal static class SupplierCancellationCircuitReader
{
    /// <summary>Resultado: las lineas del circuito (ambos Kinds, todas las monedas) + el receivable Y por moneda.</summary>
    public sealed class Result
    {
        public IReadOnlyList<SupplierCircuitLine> CircuitLines { get; }
        public IReadOnlyDictionary<string, decimal> ReceivableByCurrency { get; }

        public Result(IReadOnlyList<SupplierCircuitLine> circuitLines, IReadOnlyDictionary<string, decimal> receivableByCurrency)
        {
            CircuitLines = circuitLines;
            ReceivableByCurrency = receivableByCurrency;
        }
    }

    /// <summary>
    /// Carga el circuito de cancelacion del operador. Trae las lineas de cancelacion del operador en BCs NO
    /// abortadas (con su BC padre y la reserva para el numero/fecha), y deriva en memoria las lineas del circuito
    /// y el receivable Y. El volumen por operador es chico (back-office), por eso se proyecta en memoria.
    /// </summary>
    public static async Task<Result> LoadAsync(AppDbContext db, int supplierId, CancellationToken ct, ILogger? logger = null)
    {
        // Lineas de ESTE operador en cancelaciones NO abortadas (incluida Closed): el reembolso recibido y la
        // multa retenida son contrapartida PERMANENTE del pago negativo, no desaparecen al cerrar la BC.
        var lines = await db.BookingCancellationLines
            .AsNoTracking()
            .Include(l => l.BookingCancellation).ThenInclude(bc => bc.Reserva)
            // ADR-044 T2 Addendum: necesitamos los cargos de la linea para el bloque "Cargo facturado aparte".
            .Include(l => l.OperatorCharges)
            .Where(l => l.SupplierId == supplierId
                     && l.BookingCancellation.Status != BookingCancellationStatus.Aborted)
            .ToListAsync(ct);

        // RAIZ del scope de Y (estructural, 2026-06-29): NO inferimos "caja negativa" desde bc.Status (cualquier
        // allow/deny-list por estado puede fallar — p.ej. Drafted vale a la vez para un draft total con servicios
        // VIVOS y para un draft parcial con su servicio YA cancelado). Atamos Y al MISMO predicado que usa
        // SupplierDebtPersister para decidir si un servicio cuenta como compra confirmada
        // (<see cref="WorkflowStatusHelper.CountsForSupplierDebtByType"/>). Una linea cuenta para Y SI Y SOLO SI su
        // servicio subyacente NO cuenta como deuda — que es la condicion EXACTA que hace que su pago quede sin
        // respaldo y la caja del operador caiga. Asi (caja negativa) ⟺ (Y cuenta) por CONSTRUCCION, en todos los
        // estados presentes y futuros, sin enumerar bc.Status. Cargamos el estado del servicio de cada linea en
        // batch por tabla (6 queries, no N+1).
        var serviceCountsAsDebt = await LoadServiceDebtCountingAsync(db, lines, ct);

        var circuitLines = new List<SupplierCircuitLine>();
        var receivableByCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var bc = line.BookingCancellation;
            var currency = Monedas.Normalizar(line.Currency);
            var reservaNumber = bc.Reserva?.NumeroReserva;

            // --- MULTA RETENIDA (pass-through confirmada) ---
            // ADR-044 T2 Addendum (2026-07-10): usamos line.RetainedDeductionAmount (eje CAJA, columna fisica de
            // la LINEA) en vez de line.PenaltyAmount + bc.PenaltyStatus/bc.ConceptKind (el snapshot del BC PADRE).
            // Motivo doble:
            //  (1) FIX de bug real para operadores SECUNDARIOS: bc.PenaltyStatus/bc.ConceptKind describen SIEMPRE
            //      al operador PRINCIPAL del BC (ADR-044 T1); un secundario con multa confirmada nunca pintaba
            //      "Multa retenida" en su propio extracto porque este gate miraba el padre. RetainedDeductionAmount
            //      ya es la fuente de verdad POR LINEA (se escribe en AllocateConfirmedPenaltyToLinesAsync,
            //      corre para cualquier operador resuelto, no solo el principal).
            //  (2) B1 del Addendum: PenaltyAmount (eje CLIENTE) puede incluir montos Withholding/FacturadaAparte
            //      que el operador NUNCA retuvo de la caja — pintarlos como "retenida" seria mostrar plata que el
            //      operador jamas se quedo. RetainedDeductionAmount ya excluye esos dos casos por construccion.
            decimal penaltyRetained = line.RetainedDeductionAmount;
            if (penaltyRetained > 0m)
            {
                circuitLines.Add(new SupplierCircuitLine(
                    Date: bc.PenaltyConfirmedAt ?? line.CreatedAt,
                    Kind: SupplierAccountStatementLineKinds.PenaltyRetained,
                    Description: "Multa retenida por el operador",
                    DocumentRef: reservaNumber,
                    Currency: currency,
                    Amount: penaltyRetained,
                    SourcePublicId: line.PublicId));
            }

            // --- CARGO FACTURADO APARTE (ADR-044 T2 Addendum) ---
            // El operador devuelve el reembolso INTEGRO (no retiene nada) pero factura este cargo con su propio
            // documento: es una DEUDA NUEVA de la agencia hacia el operador (no una retencion). Vive en el
            // circuito (no en la caja real todavia — ver limitacion documentada en OperatorChargeInvoiced) para
            // que "Le debo" lo refleje sin mezclarlo con la multa retenida.
            decimal operatorChargeInvoiced = line.OperatorCharges
                .Where(c => c.CollectionMode == PenaltyCollectionMode.FacturadaAparte)
                .Sum(c => c.Amount);
            if (operatorChargeInvoiced > 0m)
            {
                circuitLines.Add(new SupplierCircuitLine(
                    Date: line.OperatorCharges
                        .Where(c => c.CollectionMode == PenaltyCollectionMode.FacturadaAparte)
                        .Select(c => c.ConfirmedAt)
                        .DefaultIfEmpty(line.CreatedAt)
                        .Max(),
                    Kind: SupplierAccountStatementLineKinds.OperatorChargeInvoiced,
                    Description: "Cargo del operador facturado aparte",
                    DocumentRef: line.OperatorCharges
                        .FirstOrDefault(c => c.CollectionMode == PenaltyCollectionMode.FacturadaAparte)?.DocumentRef
                        ?? reservaNumber,
                    Currency: currency,
                    Amount: operatorChargeInvoiced,
                    SourcePublicId: line.PublicId));
            }

            // --- REEMBOLSO RECIBIDO ---
            // line.ReceivedRefundAmount ya neto de allocations anuladas (DrainReceivedRefundFromOperatorLines al
            // anular). Es la atribucion por operador del reembolso. Fecha representativa = alta de la linea (no
            // guardamos fecha de recibo por linea; el dato load-bearing es el monto, no la fecha del renglon).
            decimal received = line.ReceivedRefundAmount;
            if (received > 0m)
            {
                circuitLines.Add(new SupplierCircuitLine(
                    Date: line.CreatedAt,
                    Kind: SupplierAccountStatementLineKinds.RefundReceived,
                    Description: "Reembolso recibido del operador",
                    DocumentRef: reservaNumber,
                    Currency: currency,
                    Amount: received,
                    SourcePublicId: line.PublicId));
            }

            // --- RECEIVABLE Y (atado al servicio cancelado, no a bc.Status) ---
            // Y cuenta el receivable vivo (cap - recibido) SI Y SOLO SI el servicio de ESTA linea NO cuenta como
            // compra confirmada (== ya esta cancelado / dejo de respaldar el pago => la caja del operador cayo por
            // el). Si el servicio TODAVIA cuenta (draft total no confirmado: servicios vivos), NO contamos Y: no hay
            // receivable real y la caja no es negativa por esta linea. En una BC Closed sub-reembolsada el servicio
            // sigue cancelado, asi que el residuo (cap - recibido) sigue contando como "me tiene que devolver".
            // La formula vive en LiveReceivableForLine para compartirla EXACTA con el read-model de reembolsos
            // pendientes (OperatorRefundReadModelService), asi la solapa "Reembolsos" cuadra por CONSTRUCCION con
            // este "me tiene que devolver".
            decimal pending = LiveReceivableForLine(line, bc, serviceCountsAsDebt, supplierId, logger);
            if (pending > 0m)
            {
                receivableByCurrency.TryGetValue(currency, out var acc);
                receivableByCurrency[currency] = acc + pending;
            }
        }

        return new Result(circuitLines, receivableByCurrency);
    }

    /// <summary>
    /// Carga, en batch por tabla (6 queries, no N+1), si el servicio subyacente de cada linea CUENTA como compra
    /// confirmada del operador, usando el MISMO predicado que la deuda (<see cref="WorkflowStatusHelper.CountsForSupplierDebtByType"/>).
    /// La clave es (tabla, id del servicio). El centinela legacy (Generic, id=0) NO apunta a un servicio puntual:
    /// no entra en el diccionario y se resuelve aparte (<see cref="ResolveServiceCountsAsDebt"/>).
    /// </summary>
    internal static async Task<Dictionary<(CancellableServiceTable Table, int ServiceId), bool>> LoadServiceDebtCountingAsync(
        AppDbContext db, List<BookingCancellationLine> lines, CancellationToken ct)
    {
        var result = new Dictionary<(CancellableServiceTable, int), bool>();

        // Helper para las tablas tipadas: el "tipo" (label) decide si se mapea por codigo IATA (Vuelo) o generico.
        List<int> IdsFor(CancellableServiceTable table) => lines
            .Where(l => l.ServiceTable == table && l.ServiceId != 0)
            .Select(l => l.ServiceId).Distinct().ToList();

        var flightIds = IdsFor(CancellableServiceTable.Flight);
        if (flightIds.Count > 0)
        {
            var rows = await db.FlightSegments.AsNoTracking()
                .Where(s => flightIds.Contains(s.Id)).Select(s => new { s.Id, s.Status }).ToListAsync(ct);
            foreach (var r in rows)
                result[(CancellableServiceTable.Flight, r.Id)] = WorkflowStatusHelper.CountsForSupplierDebtByType("Vuelo", r.Status);
        }

        var hotelIds = IdsFor(CancellableServiceTable.Hotel);
        if (hotelIds.Count > 0)
        {
            var rows = await db.HotelBookings.AsNoTracking()
                .Where(s => hotelIds.Contains(s.Id)).Select(s => new { s.Id, s.Status }).ToListAsync(ct);
            foreach (var r in rows)
                result[(CancellableServiceTable.Hotel, r.Id)] = WorkflowStatusHelper.CountsForSupplierDebtByType("Hotel", r.Status);
        }

        var transferIds = IdsFor(CancellableServiceTable.Transfer);
        if (transferIds.Count > 0)
        {
            var rows = await db.TransferBookings.AsNoTracking()
                .Where(s => transferIds.Contains(s.Id)).Select(s => new { s.Id, s.Status }).ToListAsync(ct);
            foreach (var r in rows)
                result[(CancellableServiceTable.Transfer, r.Id)] = WorkflowStatusHelper.CountsForSupplierDebtByType("Traslado", r.Status);
        }

        var packageIds = IdsFor(CancellableServiceTable.Package);
        if (packageIds.Count > 0)
        {
            var rows = await db.PackageBookings.AsNoTracking()
                .Where(s => packageIds.Contains(s.Id)).Select(s => new { s.Id, s.Status }).ToListAsync(ct);
            foreach (var r in rows)
                result[(CancellableServiceTable.Package, r.Id)] = WorkflowStatusHelper.CountsForSupplierDebtByType("Paquete", r.Status);
        }

        var assistanceIds = IdsFor(CancellableServiceTable.Assistance);
        if (assistanceIds.Count > 0)
        {
            var rows = await db.AssistanceBookings.AsNoTracking()
                .Where(s => assistanceIds.Contains(s.Id)).Select(s => new { s.Id, s.Status }).ToListAsync(ct);
            foreach (var r in rows)
                result[(CancellableServiceTable.Assistance, r.Id)] = WorkflowStatusHelper.CountsForSupplierDebtByType("Asistencia", r.Status);
        }

        // Generico: el tipo lo da la propia fila (ServiceType), igual que en SupplierDebtPersister.
        var genericIds = IdsFor(CancellableServiceTable.Generic);
        if (genericIds.Count > 0)
        {
            var rows = await db.Servicios.AsNoTracking()
                .Where(s => genericIds.Contains(s.Id)).Select(s => new { s.Id, s.ServiceType, s.Status }).ToListAsync(ct);
            foreach (var r in rows)
                result[(CancellableServiceTable.Generic, r.Id)] = WorkflowStatusHelper.CountsForSupplierDebtByType(r.ServiceType, r.Status);
        }

        return result;
    }

    /// <summary>
    /// ¿El servicio de esta linea TODAVIA cuenta como compra confirmada del operador? (true = cuenta -> Y NO suma;
    /// false = no cuenta / ya cancelado -> Y suma el receivable). Para el centinela legacy (Generic, id=0, que no
    /// apunta a un servicio puntual porque el BC viejo cancelaba la reserva entera) usamos si la cancelacion ya tomo
    /// efecto (<c>ConfirmedWithClientAt</c>): en los BC legacy ese campo es fiel (no pasan por el flujo FC1.3). Si el
    /// servicio no se encuentra (borrado), tratamos que NO cuenta -> Y suma (direccion segura: nunca mintea).
    ///
    /// <para><b>R2 (endurecimiento, review 2026-06-30)</b>: el lado-CAJA (<see cref="SupplierDebtPersister"/> via
    /// <see cref="SupplierDebtCalculator"/>) cuenta una compra con DOS filtros: el predicado de estado del servicio
    /// (<see cref="WorkflowStatusHelper.CountsForSupplierDebtByType"/>, el que espejamos) Y ADEMAS que la reserva este
    /// en <see cref="SupplierDebtCalculator.ValidReservationStatuses"/>. Espejar ESE segundo filtro en la condicion de
    /// Y seria un ERROR (una reserva <c>Cancelled</c>/<c>PendingOperatorRefund</c> NO esta en ese set y es JUSTO donde
    /// Y debe contar: el operador debe el reembolso). Pero queda un corner de divergencia: si un servicio SIGUE
    /// contando por su estado (Confirmado) pero su reserva esta en un status que el lado-caja EXCLUYE, la compra cae de
    /// la caja igual y, sin esta guarda, Y quedaria excluido -> mint. Hoy es INALCANZABLE (las reservas con status
    /// excluido realmente alcanzables —Cancelled/PendingOperatorRefund— llegan con los servicios ya Cancelado, asi que
    /// el primer filtro ya da false y coincidimos). Para blindarlo a futuro SIN espejar el set: en ese corner
    /// SOBRE-DECLARAMOS Y (lo contamos, igual que cae la caja) y LOGUEAMOS la inconsistencia. Asi el unico modo de
    /// falla futuro es "sobre-declarar Y" (seguro, cuadra con la caja) o "log ruidoso", NUNCA "mintear saldo a favor".
    /// La guarda solo AGREGA Y-counting; nunca lo quita (no re-rompe el feature para Cancelled/PendingOperatorRefund).</para>
    /// </summary>
    /// <summary>
    /// ¿El servicio de ESTA linea ya dejo de contar como compra confirmada del operador? (true => su caja cayo por
    /// esta linea => su receivable Y cuenta; false => el servicio sigue vivo => Y no cuenta). Es el MISMO predicado
    /// que usa <see cref="LoadAsync"/>. Se expone para que el read-model de reembolsos pendientes
    /// (<c>OperatorRefundReadModelService</c>) filtre el desglose con la MISMA regla y cuadre por construccion.
    /// </summary>
    internal static bool IsReceivableEligible(
        BookingCancellationLine line,
        BookingCancellation bc,
        IReadOnlyDictionary<(CancellableServiceTable Table, int ServiceId), bool> serviceCountsAsDebt,
        int supplierId,
        ILogger? logger)
        => !ResolveServiceCountsAsDebt(line, serviceCountsAsDebt, bc, supplierId, logger);

    /// <summary>
    /// Receivable Y VIVO de UNA linea = <c>max(0, RefundCap − Recibido)</c> si su servicio ya no cuenta como compra
    /// (<see cref="IsReceivableEligible"/>); 0 si el servicio sigue vivo (draft total sin confirmar) o no quedo
    /// residuo. Es EXACTAMENTE la formula que suma <see cref="LoadAsync"/> al receivable por moneda — fuente UNICA
    /// para que "me tiene que devolver" (extracto) y la solapa "Reembolsos" (read-model) no puedan divergir.
    /// </summary>
    internal static decimal LiveReceivableForLine(
        BookingCancellationLine line,
        BookingCancellation bc,
        IReadOnlyDictionary<(CancellableServiceTable Table, int ServiceId), bool> serviceCountsAsDebt,
        int supplierId,
        ILogger? logger)
    {
        if (!IsReceivableEligible(line, bc, serviceCountsAsDebt, supplierId, logger))
            return 0m;

        decimal pending = line.RefundCap - line.ReceivedRefundAmount;
        return pending > 0m ? pending : 0m;
    }

    private static bool ResolveServiceCountsAsDebt(
        BookingCancellationLine line,
        IReadOnlyDictionary<(CancellableServiceTable Table, int ServiceId), bool> serviceCountsAsDebt,
        BookingCancellation bc,
        int supplierId,
        ILogger? logger)
    {
        bool countsAsDebt;

        // Centinela legacy: no hay servicio puntual; la "cancelacion total" tomo efecto cuando se confirmo con el
        // cliente (T0). null => aun no efectiva => sigue contando (true, Y no suma).
        if (line.ServiceTable == CancellableServiceTable.Generic && line.ServiceId == 0)
            countsAsDebt = bc.ConfirmedWithClientAt == null;
        else if (serviceCountsAsDebt.TryGetValue((line.ServiceTable, line.ServiceId), out var counts))
            countsAsDebt = counts;
        else
            // Servicio no encontrado (borrado): su compra no esta respaldando el pago -> no cuenta -> Y suma (seguro).
            countsAsDebt = false;

        if (!countsAsDebt)
            return false; // el servicio no cuenta -> Y suma (camino normal de una linea cancelada).

        // R2: el servicio SIGUE contando por su estado. Verificamos el SEGUNDO filtro del lado-caja: si la reserva
        // esta en un status que la caja EXCLUYE, la compra cae igual -> Y debe contar (sobre-declarar, seguro), no
        // excluirse. Es el unico corner donde excluir Y podria mintear. Hoy inalcanzable; lo hacemos ruidoso.
        var reservaStatus = bc.Reserva?.Status;
        if (reservaStatus != null
            && !SupplierDebtCalculator.ValidReservationStatuses.Contains(reservaStatus))
        {
            logger?.LogWarning(
                "metric:circuit_reader_debt_filter_divergence | SupplierId={SupplierId} BcPublicId={BcPublicId} " +
                "ServiceTable={ServiceTable} ServiceId={ServiceId} ReservaStatus={ReservaStatus} — el servicio cuenta " +
                "como compra por su estado pero la reserva esta en un status que la caja excluye; se cuenta Y " +
                "(sobre-declaracion segura) para NO mintear saldo a favor. Revisar el flujo que dejo esta combinacion.",
                supplierId, bc.PublicId, line.ServiceTable, line.ServiceId, reservaStatus);
            return false; // sobre-declarar Y (cuadra con la caja que ya cayo), nunca mintear.
        }

        return true; // servicio cuenta como compra y la reserva tambien -> sin receivable, Y no suma (correcto).
    }
}
