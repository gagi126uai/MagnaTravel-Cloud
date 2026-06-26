using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Time;

namespace TravelApi.Infrastructure.Services;

public class AlertService : IAlertService
{
    // === Auditoria ERP 2026-06-12 (items 5 y 8): ventanas de las 3 alarmas nuevas ===
    // Defaults elegidos y documentados (sin feature flag — son alarmas operativas siempre activas, igual
    // que los buckets financieros). NO se hicieron settings de DB a proposito: son umbrales operativos
    // estables; si el dueño quiere configurarlos, se agregan como settings despues sin cambiar el contrato.

    /// <summary>
    /// Pago al operador y time-limit aereo: avisar desde 3 dias antes del vencimiento. Vencidos
    /// (daysLeft &lt; 0) tambien avisan — un pago/emision vencido es lo MAS urgente, no se silencia.
    /// </summary>
    private const int OperatorDeadlineAlertDays = 3;

    /// <summary>
    /// Pasaporte: regla tipica de vigencia — muchos destinos exigen que el pasaporte siga vigente 6 meses
    /// DESPUES del viaje. Avisamos si el pasaporte vence dentro de los 6 meses posteriores al inicio del viaje.
    /// </summary>
    private const int PassportValidityMonthsAfterTrip = 6;

    /// <summary>
    /// Q9 (2026-06-24): anticipacion en dias del aviso "presupuesto/cotizacion por caducar". Avisamos
    /// cuando a una pre-venta le faltan &lt;= N dias para caducar a Perdido (la caducidad la maneja el job
    /// G6 con BudgetExpirationDays/QuotationExpirationDays). NO se hizo un setting de DB nuevo (regla del
    /// proyecto: no agregar mas llaves); 3 es el mismo default operativo que OperatorDeadlineAlertDays.
    ///
    /// DECISION A CONFIRMAR CON GASTON: si quiere otro numero de dias de anticipacion (o que sea
    /// configurable), se ajusta aca o se promueve a setting despues sin cambiar el contrato.
    /// </summary>
    private const int PreSaleExpiryAlertDays = 3;

    /// <summary>
    /// (2026-06-26): ventana de la alarma "el operador no reembolso". Solo se avisan las cancelaciones cuya fecha
    /// limite de reembolso (<c>OperatorRefundDueBy</c>) cayó dentro de los ultimos N dias. COTA NECESARIA porque
    /// <c>AbandonedByOperator</c> es TERMINAL (no transiciona): sin este tope, cada cancelacion abandonada
    /// aparecería en la campanita PARA SIEMPRE y se acumularían infinito. 90 dias (un trimestre) da margen
    /// razonable para reclamar al operador o dar la cancelacion por perdida; pasado eso, deja de molestar (el
    /// dato sigue en la cancelacion, solo no se alarma). Se elige constante (no setting): umbral operativo estable.
    /// </summary>
    private const int AbandonedOperatorRefundAlertDays = 90;

    private readonly AppDbContext _context;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;
    private readonly ILogger<AlertService> _logger;

    public AlertService(
        AppDbContext context,
        IOperationalFinanceSettingsService operationalFinanceSettingsService,
        ILogger<AlertService> logger)
    {
        _context = context;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        _logger = logger;
    }

    public async Task<object> GetAlertsAsync(AlertCallerContext caller, CancellationToken cancellationToken)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);

        // --- Buckets financieros (privacidad 2026-06-17): deuda de clientes vs deuda a proveedores ---
        // Antes (Fuga 2 ADR-017 §2.7 F1b) los DOS eran admin-only, todo-o-nada, sin camino por-vendedor.
        // Ahora cada uno respeta su propia regla (guia-ux-gaston.md, sin flag — fix de privacidad):
        //   - UrgentTrips ("viajo/termino y debe") = deuda del CLIENTE -> scope por DUEÑO: admin ve todas,
        //     el vendedor SOLO las de SUS reservas, no-admin sin identidad -> vacio (fail-closed). El monto
        //     que el cliente debe NO es costo: se muestra sin permiso de costo (regla 2026-06-09).
        //   - SupplierDebts ("le debemos al operador") = COSTO -> solo con cobranzas.see_cost (regla
        //     2026-06-05: sin ese permiso no se ven montos de costo/deuda en NINGUNA pantalla, avisos
        //     incluidos). Es deuda de la agencia entera, no se scopea por vendedor.
        // El gating sigue viviendo en el SERVER; el metodo aplica cada borde inline.
        var (urgentTrips, supplierDebts) = await ComputeFinancialBucketsAsync(caller, settings, cancellationToken);
        var financialCount = urgentTrips.Count + supplierDebts.Count;

        // --- Buckets gateados (cada uno detras de su flag) ---
        // ADR-019: el flag EnableServiceDeadlineAlerts ahora gatea el bucket UpcomingStarts ("Proximos
        // inicios"). El nombre interno NO se renombra (decision D7: renombrar = migracion + churn por
        // cero valor de usuario); el nombre de cara al dueño es solo el texto de la UI.
        var upcomingStartsActive = settings.EnableServiceDeadlineAlerts;
        // CostsToConfirm: gateado por el flag del catalogo + que el caller pueda ver costos (§2.8/D8b).
        var costsToConfirmActive = settings.EnableCatalogFindOrCreate && caller.CanSeeCost;

        // El "hoy" del corte es la fecha LOCAL de Argentina: comparar contra UtcNow.Date correria el dia
        // 3h antes (a las 21:00 ART ya seria "mañana"). Lo necesitan los buckets nuevos y las alarmas.
        var today = AgencyTimezone.TodayWallClockUtc();

        // === Auditoria ERP 2026-06-12 (items 5 y 8): 3 alarmas nuevas, SIEMPRE activas (sin flag) ===
        // Mismo gating de visibilidad que UpcomingStarts: admin ve todas, el vendedor solo SUS reservas,
        // no-admin sin identidad -> vacio (fail-closed). NO dependen de EnableServiceDeadlineAlerts: son
        // alarmas operativas propias, no la pill vieja. Solo aplican a reservas vivas y servicios/pax no
        // cancelados (cada compute lo aplica inline).
        var operatorPaymentDeadlines = await ComputeOperatorPaymentDeadlinesAsync(caller, today, cancellationToken);
        var ticketingDeadlines = await ComputeTicketingDeadlinesAsync(caller, today, cancellationToken);
        var passportExpiries = await ComputePassportExpiriesAsync(caller, cancellationToken);
        // ADR-027 (hallazgo #10): reservas "confirmadas con cambios" sin revisar. Mismo gating que las otras.
        var confirmedWithChanges = await ComputeConfirmedWithChangesAsync(caller, cancellationToken);
        // ADR-033 (E6/B3): reservas "esperando refund del operador" con saldo a favor que quedo sin consumir.
        var stuckOperatorRefunds = await ComputeStuckOperatorRefundsAsync(caller, cancellationToken);
        // Q9 (2026-06-24): presupuestos/cotizaciones por caducar (a <= N dias de pasar a Perdido por el job G6).
        var expiringPreSales = await ComputeExpiringPreSalesAsync(caller, settings, cancellationToken);
        // (2026-06-26): cancelaciones cuyo operador no reembolso (abandonadas por el job o vencidas sin cerrar).
        var abandonedOperatorRefunds = await ComputeAbandonedOperatorRefundsAsync(caller, today, cancellationToken);

        var hasNewAlarms = operatorPaymentDeadlines.Count > 0
                           || ticketingDeadlines.Count > 0
                           || passportExpiries.Count > 0
                           || confirmedWithChanges.Count > 0
                           || stuckOperatorRefunds.Count > 0
                           || expiringPreSales.Count > 0
                           || abandonedOperatorRefunds.Count > 0;

        // CAMINO BYTE-IDENTICO (default historico): si NINGUN bucket nuevo esta activo Y no hay NINGUNA
        // alarma nueva que mostrar, devolvemos el MISMO objeto anonimo de siempre (3 propiedades, mismo
        // orden) — /alerts no cambia para los consumidores actuales cuando no hay nada nuevo que avisar.
        if (!upcomingStartsActive && !costsToConfirmActive && !hasNewAlarms)
        {
            return new
            {
                UrgentTrips = urgentTrips,
                SupplierDebts = supplierDebts,
                TotalCount = financialCount
            };
        }

        var upcomingStarts = upcomingStartsActive
            ? await ComputeUpcomingStartsAsync(caller, settings.ServiceDeadlineAlertDays, today, cancellationToken)
            : new List<object>();

        var costsToConfirm = costsToConfirmActive
            ? await ComputeCostsToConfirmAsync(caller, cancellationToken)
            : new List<object>();

        // Objeto extendido: claves nuevas aditivas. Devolvemos un DTO TIPADO (NO un Dictionary<string,object>)
        // para que System.Text.Json aplique el PropertyNamingPolicy camelCase a las claves — un diccionario deja
        // las claves verbatim (PascalCase) y romperia a los consumidores que sirven el path con flag OFF. Ver
        // AlertsResponse. Cada bucket/alarma se incluye SOLO si tiene contenido o su gate esta activo (null = se
        // omite del JSON), misma presencia condicional que el path historico.
        var totalCount = financialCount
                         + upcomingStarts.Count + costsToConfirm.Count
                         + operatorPaymentDeadlines.Count + ticketingDeadlines.Count + passportExpiries.Count
                         + confirmedWithChanges.Count + stuckOperatorRefunds.Count + expiringPreSales.Count
                         + abandonedOperatorRefunds.Count;
        return new AlertsResponse(
            urgentTrips: urgentTrips,
            supplierDebts: supplierDebts,
            upcomingStarts: upcomingStartsActive ? upcomingStarts : null,
            // D1: la ventana viaja junto al bucket para que la pill por servicio (frontend) use el
            // mismo umbral. NO va en OperationalFlagsResponse (regla dura "solo booleanos").
            upcomingStartsWindowDays: upcomingStartsActive ? settings.ServiceDeadlineAlertDays : null,
            costsToConfirm: costsToConfirmActive ? costsToConfirm : null,
            totalCount: totalCount,
            // Alarmas nuevas: se omiten del JSON cuando estan vacias (null), igual presencia condicional.
            operatorPaymentDeadlines: operatorPaymentDeadlines.Count > 0 ? operatorPaymentDeadlines : null,
            ticketingDeadlines: ticketingDeadlines.Count > 0 ? ticketingDeadlines : null,
            passportExpiries: passportExpiries.Count > 0 ? passportExpiries : null,
            confirmedWithChanges: confirmedWithChanges.Count > 0 ? confirmedWithChanges : null,
            stuckOperatorRefunds: stuckOperatorRefunds.Count > 0 ? stuckOperatorRefunds : null,
            expiringPreSales: expiringPreSales.Count > 0 ? expiringPreSales : null,
            abandonedOperatorRefunds: abandonedOperatorRefunds.Count > 0 ? abandonedOperatorRefunds : null);
    }

    /// <summary>
    /// Buckets financieros (viajes urgentes con saldo del cliente + deudas a proveedores). Mismo criterio y
    /// mismo "hoy" (UtcNow.Date) de siempre; lo NUEVO (2026-06-17) es el gating por caller: UrgentTrips con
    /// scope por dueño, SupplierDebts detras del permiso de ver costos. Ver el comentario del call-site.
    /// </summary>
    private async Task<(IReadOnlyList<object> UrgentTrips, IReadOnlyList<object> SupplierDebts)>
        ComputeFinancialBucketsAsync(AlertCallerContext caller, OperationalFinanceSettings settings, CancellationToken cancellationToken)
    {
        var today = DateTime.UtcNow.Date;
        var threshold = today.AddDays(Math.Max(settings.UpcomingUnpaidReservationAlertDays, 1));

        // ADR-020 (2026-06-07): "viajes urgentes" = reservas activas con saldo pendiente. InManagement
        // (En gestion) reemplaza al viejo Sold. ADR-036 (2026-06-21): ToSettle murio (estado eliminado).
        //
        // Cubre TRES casos (auditoria de negocio 2026-06-12 item 6 "viajó y debe" + ADR-033 A3 "terminado y debe"):
        //  (A) viaje INMINENTE: salida en [hoy ... hoy + ventana]. El cliente todavia no viajo y debe.
        //  (B) viaje EN CURSO con saldo: Status == Traveling y el viaje ya arranco pero no termino.
        //      Antes este caso DESAPARECIA: el prefiltro StartDate >= hoy lo excluia apenas empezaba el
        //      viaje, y la deuda nunca cerraba sola. Ahora se incluye sin tope de ventana (un viaje en
        //      curso impago es lo MAS urgente). "No termino" = sin EndDate o EndDate >= hoy.
        //  (C) ADR-033 (A3/F6): TERMINADO y debe. Status == Closed (Finalizada) con saldo pendiente. Antes la
        //      deuda de una reserva finalizada quedaba INVISIBLE en alertas; ahora es deuda post-viaje real y
        //      sin tope de ventana (igual que el caso B). El front distingue el rotulo por el Status que ya
        //      viaja en la proyeccion ("terminado y debe" vs "en viaje y debe"); sin cambio de contrato.
        //
        // Visibilidad por DUEÑO (privacidad 2026-06-17, mismo borde que UpcomingStarts/CostsToConfirm):
        // admin ve todas; el vendedor SOLO sus reservas. Un no-admin SIN identidad (UserId null) corta a
        // vacio ANTES de tocar la query: sin esta guarda el predicado "ResponsibleUserId == caller.UserId"
        // se traduce a SQL como "ResponsibleUserId IS NULL" y filtraria todas las reservas sin dueño (leak).
        // El monto que el cliente debe NO es costo: se muestra sin permiso de costo (regla 2026-06-09).
        IReadOnlyList<object> urgentTrips = Array.Empty<object>();
        if (caller.IsAdmin || !string.IsNullOrEmpty(caller.UserId))
        {
            urgentTrips = await _context.Reservas
                .Where(f => f.Balance > 0 &&
                            (caller.IsAdmin || f.ResponsibleUserId == caller.UserId) &&
                            (
                                // (A) Inminente (cualquier estado activo, ventana futura).
                                ((f.Status == EstadoReserva.InManagement ||
                                  f.Status == EstadoReserva.Confirmed ||
                                  f.Status == EstadoReserva.Traveling) &&
                                 f.StartDate >= today &&
                                 f.StartDate <= threshold)
                                ||
                                // (B) En viaje y debe (sin tope de ventana, viaje no terminado).
                                (f.Status == EstadoReserva.Traveling &&
                                 (f.EndDate == null || f.EndDate >= today))
                                ||
                                // (C) Terminado y debe (sin tope de ventana).
                                (f.Status == EstadoReserva.Closed)
                            ))
                .Select(f => new
                {
                    f.PublicId,
                    f.NumeroReserva,
                    f.Name,
                    f.StartDate,
                    f.Balance,
                    // El front (PaymentsHomePage) ya recibe Status; hoy muestra un rotulo fijo "Urgente",
                    // pero al venir Status="Traveling" puede distinguir el caso "en viaje con saldo
                    // pendiente" sin cambios de contrato (campo ya presente, mismo shape).
                    f.Status,
                    PayerName = f.Payer != null ? f.Payer.FullName : "Sin Cliente"
                })
                .OrderBy(f => f.StartDate)
                .ToListAsync(cancellationToken);
        }

        // SupplierDebts = COSTO (deuda al operador): solo quien tiene cobranzas.see_cost (regla 2026-06-05;
        // el admin siempre lo tiene, lo resuelve el controller). Es deuda agregada de la agencia entera, no
        // se scopea por vendedor — el gate aca es el permiso de ver costos, no el dueño.
        IReadOnlyList<object> supplierDebts = Array.Empty<object>();
        if (caller.CanSeeCost)
        {
            supplierDebts = await _context.Suppliers
                .Where(s => s.CurrentBalance > 100 && s.IsActive)
                .Select(s => new
                {
                    s.PublicId,
                    s.Name,
                    s.CurrentBalance,
                    s.Phone
                })
                .OrderByDescending(s => s.CurrentBalance)
                .Take(10)
                .ToListAsync(cancellationToken);
        }

        return (urgentTrips, supplierDebts);
    }

    /// <summary>
    /// ADR-019 D2: bucket <c>UpcomingStarts</c> ("Proximos inicios") compute-on-read. UN aviso POR
    /// RESERVA cuyo primer servicio NO cancelado empieza dentro de [hoy ... hoy + ventana]: "⏰ Empieza
    /// el {dd/MM} (en {N} dias)" / "Empieza HOY" cuando <c>daysLeft == 0</c>. Reemplaza al bucket de
    /// fechas limite manuales de ADR-017 F1.4 (nunca prendido en prod).
    ///
    /// <para><b>Elegibilidad</b>: Status ∈ {InManagement, Confirmed, Traveling} (ADR-020: InManagement
    /// reemplaza al viejo Sold). Cotizacion/Presupuesto/Perdido NO avisan (no hay compromiso). <c>Traveling</c>
    /// entra porque el job de lifecycle promueve a las
    /// 00:00 ART y sin el, el aviso rojo "Empieza HOY" no se veria nunca (B2-nuevo); no necesita
    /// condicion extra: la ventana <c>hoy &lt;= firstStart</c> deja afuera sola a una reserva
    /// genuinamente en viaje (su primer inicio quedo en el pasado).</para>
    ///
    /// <para><b>SIN prefiltro de fecha sobre Reserva.StartDate (B1-bis — NO lo reintroduzcas)</b>: ese
    /// campo es editable a mano en ambas direcciones y borrable (<c>UpdateDatesAsync</c>), asi que
    /// cualquier prefiltro sobre el puede SILENCIAR avisos de reservas cuyos servicios si caen en
    /// ventana. La verdad sobre la ventana la dan SIEMPRE las fechas de los servicios
    /// (<see cref="UpcomingStartCalculator"/>); el prefiltro es solo Status + ownership.</para>
    ///
    /// <para><b>Visibilidad</b>: admin ve todas; el vendedor solo SUS reservas
    /// (<c>ResponsibleUserId == caller.UserId</c>), fail-closed sin UserId — identico a CostsToConfirm.</para>
    /// </summary>
    private async Task<List<object>> ComputeUpcomingStartsAsync(
        AlertCallerContext caller, int alertDays, DateTime today, CancellationToken ct)
    {
        // Seguridad: un no-admin SIN identidad (token sin claim NameIdentifier -> UserId null) no debe ver
        // avisos de NADIE. Sin esta guarda, el predicado "ResponsibleUserId == caller.UserId" se traduce a
        // SQL como "ResponsibleUserId IS NULL" y mostraria todas las reservas sin responsable asignado.
        if (!caller.IsAdmin && string.IsNullOrEmpty(caller.UserId))
            return new List<object>();

        // Prefiltro = Status elegible + ownership, NADA mas (B1-bis). Se trae junto con los datos que
        // necesita el item, incluido el titular (Q3): Payer.FullName -> primer Passenger por Id -> null.
        var candidates = await _context.Reservas
            .Where(r => (r.Status == EstadoReserva.InManagement
                         || r.Status == EstadoReserva.Confirmed
                         || r.Status == EstadoReserva.Traveling)
                        && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId))
            .Select(r => new
            {
                r.Id,
                r.PublicId,
                r.NumeroReserva,
                r.Name,
                PayerName = r.Payer != null ? r.Payer.FullName : null,
                FirstPassengerName = r.Passengers
                    .OrderBy(p => p.Id)
                    .Select(p => (string?)p.FullName)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return new List<object>();

        // Ventana inclusiva en ambos bordes: hoy <= firstStart <= hoy + X. No hay estado "vencido":
        // pasado el inicio, el aviso desaparece solo (el viaje-en-curso ya lo cubre urgentTrips).
        var windowEnd = today.AddDays(Math.Max(alertDays, 1));

        var candidateIds = candidates.Select(c => c.Id).ToList();
        var firstStartByReserva = await UpcomingStartCalculator.ComputeFirstStartsAsync(
            _context, candidateIds, maxStartDateInclusive: windowEnd, ct);

        // Descartes "Listo" (D3): el aviso se oculta SOLO si la fecha descartada coincide con el primer
        // inicio ACTUAL. Si el primer inicio cambio (se adelanto O se atraso), el aviso reaparece.
        var dismissedDateByReserva = await _context.UpcomingStartAlertDismissals
            .Where(d => candidateIds.Contains(d.ReservaId))
            .ToDictionaryAsync(d => d.ReservaId, d => d.DismissedFirstStartDate, ct);

        var alerts = new List<object>();
        // Orden estable: lo que empieza antes primero; a igual fecha, por numero de reserva.
        var candidatesInWindow = candidates
            .Where(c => firstStartByReserva.ContainsKey(c.Id))
            .OrderBy(c => firstStartByReserva[c.Id])
            .ThenBy(c => c.NumeroReserva, StringComparer.Ordinal);

        foreach (var candidate in candidatesInWindow)
        {
            var firstStart = firstStartByReserva[candidate.Id];

            // Borde inferior de la ventana: el primer inicio ya paso -> no avisa (cubre tambien el
            // caso Traveling genuino del B2-nuevo). El borde superior ya lo aplico el calculator.
            if (firstStart < today)
                continue;

            if (dismissedDateByReserva.TryGetValue(candidate.Id, out var dismissedDate)
                && dismissedDate.Date == firstStart.Date)
                continue; // descarte vigente: misma fecha que se apago con "Listo"

            // Titular para la linea 2 del aviso (Q3): Payer -> primer pasajero -> null (el front cae
            // al nombre de la reserva si viene null; nunca renderiza una linea rota).
            var holderName = !string.IsNullOrWhiteSpace(candidate.PayerName)
                ? candidate.PayerName
                : (!string.IsNullOrWhiteSpace(candidate.FirstPassengerName) ? candidate.FirstPassengerName : null);

            alerts.Add(new
            {
                ReservaPublicId = candidate.PublicId,
                NumeroReserva = candidate.NumeroReserva,
                Name = candidate.Name,
                HolderName = holderName,
                FirstStartDate = firstStart,
                // daysLeft == 0 => "Empieza HOY" (rojo). Ambas fechas son medianoche de pared Kind=Utc,
                // asi que la resta da dias enteros exactos.
                DaysLeft = (int)(firstStart - today).TotalDays
            });
        }

        return alerts;
    }

    /// <summary>
    /// ADR-017 F1.4 (§2.8, D8b): bucket <c>CostsToConfirm</c> compute-on-read. Lista los servicios marcados
    /// "costo a confirmar" (D7) para que alguien con permiso los revise/confirme. NO expone montos (solo
    /// reserva, tipo, etiqueta y razon). Gateado server-side por <c>cobranzas.see_cost</c> (decidido en el
    /// controller) + el flag del catalogo. Mismo filtro por caller que UpcomingStarts: admin todas, el
    /// resto solo las de SUS reservas.
    /// </summary>
    private async Task<List<object>> ComputeCostsToConfirmAsync(AlertCallerContext caller, CancellationToken ct)
    {
        // Seguridad (F1.4 review, defensa en profundidad): mismo borde que UpcomingStarts. Hoy el controller ya
        // pone CanSeeCost=false cuando el UserId es null (asi este bucket ni se calcula), pero NO confiamos en eso
        // aca: un no-admin sin identidad corta a vacio antes de filtrar por "ResponsibleUserId == UserId".
        if (!caller.IsAdmin && string.IsNullOrEmpty(caller.UserId))
            return new List<object>();

        var alerts = new List<object>();

        // Reserva "viva" a efectos de este bucket: no cancelada, no cerrada, no en espera de refund. Es
        // menos estricta que la elegibilidad de UpcomingStarts a proposito: un costo se puede confirmar aunque el viaje
        // ya este en curso (la confirmacion corrige el costo interno, no el comprobante — D8c). Los predicados
        // van INLINE en cada query porque EF no traduce helpers propios.

        // Hotel
        var hotel = await (
            from h in _context.HotelBookings
            join r in _context.Reservas on h.ReservaId equals r.Id
            where h.CostToConfirm
                  && r.Status != EstadoReserva.Cancelled && r.Status != EstadoReserva.Closed
                  && r.Status != EstadoReserva.PendingOperatorRefund
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new { r.PublicId, r.NumeroReserva, Label = h.HotelName, h.CostToConfirmReason }).ToListAsync(ct);
        alerts.AddRange(hotel.Select(x =>
            BuildCostToConfirmAlert(x.PublicId, x.NumeroReserva, "Hotel", x.Label, x.CostToConfirmReason)));

        // Aereo
        var flight = await (
            from f in _context.FlightSegments
            join r in _context.Reservas on f.ReservaId equals r.Id
            where f.CostToConfirm
                  && r.Status != EstadoReserva.Cancelled && r.Status != EstadoReserva.Closed
                  && r.Status != EstadoReserva.PendingOperatorRefund
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new { r.PublicId, r.NumeroReserva, f.ProductName, f.AirlineCode, f.FlightNumber, f.CostToConfirmReason }).ToListAsync(ct);
        // ADR-018: label = ProductName si la ficha "producto-primero" no cargo aerolinea/numero.
        alerts.AddRange(flight.Select(x =>
            BuildCostToConfirmAlert(x.PublicId, x.NumeroReserva, "Aereo",
                ServiceDisplayName.ForFlight(x.ProductName, x.AirlineCode, x.FlightNumber), x.CostToConfirmReason)));

        // Traslado
        var transfer = await (
            from t in _context.TransferBookings
            join r in _context.Reservas on t.ReservaId equals r.Id
            where t.CostToConfirm
                  && r.Status != EstadoReserva.Cancelled && r.Status != EstadoReserva.Closed
                  && r.Status != EstadoReserva.PendingOperatorRefund
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new { r.PublicId, r.NumeroReserva, t.ProductName, t.PickupLocation, t.DropoffLocation, t.VehicleType, t.CostToConfirmReason }).ToListAsync(ct);
        // ADR-018 Ronda 7: VehicleType puede ser null (opcional). La etiqueta sale del contrato unico
        // ServiceDisplayName (ProductName -> ruta -> vehiculo), igual que el bucket de Aereo.
        alerts.AddRange(transfer.Select(x =>
            BuildCostToConfirmAlert(x.PublicId, x.NumeroReserva, "Traslado",
                ServiceDisplayName.ForTransfer(x.ProductName, x.PickupLocation, x.DropoffLocation, x.VehicleType), x.CostToConfirmReason)));

        // Paquete
        var package = await (
            from p in _context.PackageBookings
            join r in _context.Reservas on p.ReservaId equals r.Id
            where p.CostToConfirm
                  && r.Status != EstadoReserva.Cancelled && r.Status != EstadoReserva.Closed
                  && r.Status != EstadoReserva.PendingOperatorRefund
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new { r.PublicId, r.NumeroReserva, Label = p.PackageName, p.CostToConfirmReason }).ToListAsync(ct);
        alerts.AddRange(package.Select(x =>
            BuildCostToConfirmAlert(x.PublicId, x.NumeroReserva, "Paquete", x.Label, x.CostToConfirmReason)));

        // Asistencia
        var assistance = await (
            from a in _context.AssistanceBookings
            join r in _context.Reservas on a.ReservaId equals r.Id
            where a.CostToConfirm
                  && r.Status != EstadoReserva.Cancelled && r.Status != EstadoReserva.Closed
                  && r.Status != EstadoReserva.PendingOperatorRefund
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new { r.PublicId, r.NumeroReserva, Label = a.PlanType, a.CostToConfirmReason }).ToListAsync(ct);
        alerts.AddRange(assistance.Select(x =>
            BuildCostToConfirmAlert(x.PublicId, x.NumeroReserva, "Asistencia", x.Label, x.CostToConfirmReason)));

        return alerts;
    }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (item 5): alarma "vence el pago al operador". UN aviso POR SERVICIO no
    /// cancelado, de reservas VIVAS (InManagement/Confirmed/Traveling), cuya fecha limite de pago cae
    /// dentro de la ventana <c>[.. hoy + OperatorDeadlineAlertDays]</c> O ya vencio (sin borde inferior:
    /// un pago vencido es lo MAS urgente). Recorre los 6 tipos de servicio con costo/proveedor.
    ///
    /// <para>Mismo gating que UpcomingStarts: admin ve todas, el vendedor solo SUS reservas, no-admin sin
    /// identidad -> vacio (fail-closed). El texto de negocio (serviceLabel + daysLeft) viaja listo para que
    /// el front lo muestre sin logica extra (daysLeft &lt; 0 = vencido).</para>
    /// </summary>
    private async Task<List<object>> ComputeOperatorPaymentDeadlinesAsync(
        AlertCallerContext caller, DateTime today, CancellationToken ct)
    {
        // Fail-closed: un no-admin sin identidad no ve alarmas de nadie (mismo borde que UpcomingStarts).
        if (!caller.IsAdmin && string.IsNullOrEmpty(caller.UserId))
            return new List<object>();

        // Tope superior EXCLUSIVO contra la medianoche del dia siguiente: "hasta hoy + X inclusive".
        var windowUpperExclusive = today.AddDays(OperatorDeadlineAlertDays + 1);
        var alerts = new List<object>();

        // Predicados inline (EF no traduce helpers propios). "Reserva viva" = compromiso con el operador
        // ya existe: InManagement/Confirmed/Traveling. El servicio no debe estar cancelado.

        // Hotel
        var hotel = await (
            from h in _context.HotelBookings
            join r in _context.Reservas on h.ReservaId equals r.Id
            where h.OperatorPaymentDeadline != null
                  && h.OperatorPaymentDeadline < windowUpperExclusive
                  && h.Status != "Cancelado"
                  && (r.Status == EstadoReserva.InManagement || r.Status == EstadoReserva.Confirmed || r.Status == EstadoReserva.Traveling)
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new { r.PublicId, r.NumeroReserva, Label = h.HotelName, Deadline = h.OperatorPaymentDeadline!.Value }).ToListAsync(ct);
        alerts.AddRange(hotel.Select(x => BuildDeadlineAlert(x.PublicId, x.NumeroReserva, "Hotel", x.Label, x.Deadline, today)));

        // Aereo (pago al operador, distinto del time-limit de emision)
        var flight = await (
            from f in _context.FlightSegments
            join r in _context.Reservas on f.ReservaId equals r.Id
            where f.OperatorPaymentDeadline != null
                  && f.OperatorPaymentDeadline < windowUpperExclusive
                  && f.Status != "UN" && f.Status != "UC" && f.Status != "HX" && f.Status != "NO"
                  && (r.Status == EstadoReserva.InManagement || r.Status == EstadoReserva.Confirmed || r.Status == EstadoReserva.Traveling)
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new { r.PublicId, r.NumeroReserva, f.ProductName, f.AirlineCode, f.FlightNumber, Deadline = f.OperatorPaymentDeadline!.Value }).ToListAsync(ct);
        alerts.AddRange(flight.Select(x => BuildDeadlineAlert(
            x.PublicId, x.NumeroReserva, "Aereo",
            ServiceDisplayName.ForFlight(x.ProductName, x.AirlineCode, x.FlightNumber), x.Deadline, today)));

        // Traslado
        var transfer = await (
            from t in _context.TransferBookings
            join r in _context.Reservas on t.ReservaId equals r.Id
            where t.OperatorPaymentDeadline != null
                  && t.OperatorPaymentDeadline < windowUpperExclusive
                  && t.Status != "Cancelado"
                  && (r.Status == EstadoReserva.InManagement || r.Status == EstadoReserva.Confirmed || r.Status == EstadoReserva.Traveling)
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new { r.PublicId, r.NumeroReserva, t.ProductName, t.PickupLocation, t.DropoffLocation, t.VehicleType, Deadline = t.OperatorPaymentDeadline!.Value }).ToListAsync(ct);
        alerts.AddRange(transfer.Select(x => BuildDeadlineAlert(
            x.PublicId, x.NumeroReserva, "Traslado",
            ServiceDisplayName.ForTransfer(x.ProductName, x.PickupLocation, x.DropoffLocation, x.VehicleType), x.Deadline, today)));

        // Paquete
        var package = await (
            from p in _context.PackageBookings
            join r in _context.Reservas on p.ReservaId equals r.Id
            where p.OperatorPaymentDeadline != null
                  && p.OperatorPaymentDeadline < windowUpperExclusive
                  && p.Status != "Cancelado"
                  && (r.Status == EstadoReserva.InManagement || r.Status == EstadoReserva.Confirmed || r.Status == EstadoReserva.Traveling)
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new { r.PublicId, r.NumeroReserva, Label = p.PackageName, Deadline = p.OperatorPaymentDeadline!.Value }).ToListAsync(ct);
        alerts.AddRange(package.Select(x => BuildDeadlineAlert(x.PublicId, x.NumeroReserva, "Paquete", x.Label, x.Deadline, today)));

        // Asistencia
        var assistance = await (
            from a in _context.AssistanceBookings
            join r in _context.Reservas on a.ReservaId equals r.Id
            where a.OperatorPaymentDeadline != null
                  && a.OperatorPaymentDeadline < windowUpperExclusive
                  && a.Status != "Cancelado"
                  && (r.Status == EstadoReserva.InManagement || r.Status == EstadoReserva.Confirmed || r.Status == EstadoReserva.Traveling)
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new { r.PublicId, r.NumeroReserva, Label = a.PlanType, Deadline = a.OperatorPaymentDeadline!.Value }).ToListAsync(ct);
        alerts.AddRange(assistance.Select(x => BuildDeadlineAlert(x.PublicId, x.NumeroReserva, "Asistencia", x.Label, x.Deadline, today)));

        // Servicio generico (ReservaId nullable en esta entidad)
        var generic = await (
            from s in _context.Servicios
            join r in _context.Reservas on s.ReservaId equals r.Id
            where s.OperatorPaymentDeadline != null
                  && s.OperatorPaymentDeadline < windowUpperExclusive
                  && s.Status != "Cancelado"
                  && (r.Status == EstadoReserva.InManagement || r.Status == EstadoReserva.Confirmed || r.Status == EstadoReserva.Traveling)
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new { r.PublicId, r.NumeroReserva, s.Description, s.ServiceType, Deadline = s.OperatorPaymentDeadline!.Value }).ToListAsync(ct);
        alerts.AddRange(generic.Select(x => BuildDeadlineAlert(
            x.PublicId, x.NumeroReserva, "Servicio",
            string.IsNullOrWhiteSpace(x.Description) ? x.ServiceType : x.Description, x.Deadline, today)));

        // Orden estable: lo que vence antes (mas urgente) primero.
        return alerts
            .OrderBy(a => (DateTime)a.GetType().GetProperty("Deadline")!.GetValue(a)!)
            .ToList();
    }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (item 5): alarma "vence la emision del aereo" (time-limit). Mismo criterio
    /// que <see cref="ComputeOperatorPaymentDeadlinesAsync"/> pero SOLO sobre segmentos de vuelo no
    /// cancelados y mirando <c>FlightSegment.TicketingDeadline</c>. Es ESPECIFICA del aereo: solo el vuelo
    /// tiene emision. Vencidos incluidos (un time-limit pasado = cupo perdido, lo mas urgente).
    /// </summary>
    private async Task<List<object>> ComputeTicketingDeadlinesAsync(
        AlertCallerContext caller, DateTime today, CancellationToken ct)
    {
        if (!caller.IsAdmin && string.IsNullOrEmpty(caller.UserId))
            return new List<object>();

        var windowUpperExclusive = today.AddDays(OperatorDeadlineAlertDays + 1);

        var flight = await (
            from f in _context.FlightSegments
            join r in _context.Reservas on f.ReservaId equals r.Id
            where f.TicketingDeadline != null
                  && f.TicketingDeadline < windowUpperExclusive
                  && f.Status != "UN" && f.Status != "UC" && f.Status != "HX" && f.Status != "NO"
                  && (r.Status == EstadoReserva.InManagement || r.Status == EstadoReserva.Confirmed || r.Status == EstadoReserva.Traveling)
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new { r.PublicId, r.NumeroReserva, f.ProductName, f.AirlineCode, f.FlightNumber, Deadline = f.TicketingDeadline!.Value }).ToListAsync(ct);

        return flight
            .Select(x => BuildDeadlineAlert(
                x.PublicId, x.NumeroReserva, "Aereo",
                ServiceDisplayName.ForFlight(x.ProductName, x.AirlineCode, x.FlightNumber), x.Deadline, today))
            .OrderBy(a => (DateTime)a.GetType().GetProperty("Deadline")!.GetValue(a)!)
            .ToList();
    }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (item 8): alarma "pasaporte por vencer". UN aviso POR PASAJERO de reservas
    /// VIVAS cuyo pasaporte vence dentro de los <see cref="PassportValidityMonthsAfterTrip"/> meses
    /// POSTERIORES al inicio del viaje (regla tipica: el destino exige vigencia 6 meses despues del viaje).
    ///
    /// <para>"Inicio del viaje" = el PRIMER inicio de servicio no cancelado de la reserva
    /// (<see cref="UpcomingStartCalculator"/>, MISMA definicion que el aviso "Proximos inicios"): si todos
    /// los servicios estan cancelados o no hay servicios, no hay viaje y la reserva no genera esta alarma.</para>
    ///
    /// <para>PII: solo expone el nombre del pasajero (ya viaja en otros buckets como holderName), NO el
    /// numero de documento ni la nacionalidad. Mismo gating de visibilidad que las otras alarmas.</para>
    /// </summary>
    private async Task<List<object>> ComputePassportExpiriesAsync(AlertCallerContext caller, CancellationToken ct)
    {
        if (!caller.IsAdmin && string.IsNullOrEmpty(caller.UserId))
            return new List<object>();

        // Pasajeros con vencimiento informado, de reservas vivas visibles para el caller. Traemos el
        // minimo: id de reserva (para cruzar con el inicio del viaje), datos de display y la fecha.
        var candidates = await (
            from p in _context.Passengers
            join r in _context.Reservas on p.ReservaId equals r.Id
            where p.PassportExpiry != null
                  && (r.Status == EstadoReserva.InManagement || r.Status == EstadoReserva.Confirmed || r.Status == EstadoReserva.Traveling)
                  && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId)
            select new
            {
                ReservaId = r.Id,
                r.PublicId,
                r.NumeroReserva,
                PassengerName = p.FullName,
                PassportExpiry = p.PassportExpiry!.Value
            }).ToListAsync(ct);

        if (candidates.Count == 0)
            return new List<object>();

        // Inicio del viaje por reserva (MIN sin cancelados). Sin tope: necesitamos la fecha exacta para
        // comparar contra el vencimiento del pasaporte. Reservas sin servicios elegibles quedan afuera.
        var reservaIds = candidates.Select(c => c.ReservaId).Distinct().ToList();
        var tripStartByReserva = await UpcomingStartCalculator.ComputeFirstStartsAsync(
            _context, reservaIds, maxStartDateInclusive: null, ct);

        var alerts = new List<object>();
        foreach (var candidate in candidates)
        {
            if (!tripStartByReserva.TryGetValue(candidate.ReservaId, out var tripStart))
                continue; // sin viaje (todos los servicios cancelados o sin servicios): no avisa

            // Regla de vigencia: avisar si el pasaporte vence ANTES de [inicio del viaje + 6 meses].
            var validityRequiredUntil = tripStart.AddMonths(PassportValidityMonthsAfterTrip);
            if (candidate.PassportExpiry.Date < validityRequiredUntil.Date)
            {
                alerts.Add(new
                {
                    ReservaPublicId = candidate.PublicId,
                    NumeroReserva = candidate.NumeroReserva,
                    PassengerName = candidate.PassengerName,
                    PassportExpiry = candidate.PassportExpiry,
                    TripStartDate = tripStart
                });
            }
        }

        // Orden estable: el que vence antes primero.
        return alerts
            .OrderBy(a => (DateTime)a.GetType().GetProperty("PassportExpiry")!.GetValue(a)!)
            .ToList();
    }

    /// <summary>
    /// ADR-027 (auditoria ERP, hallazgo #10): bucket "reservas confirmadas con cambios sin revisar".
    /// Lista UN aviso POR RESERVA viva (InManagement/Confirmed/Traveling) marcada
    /// <c>HasUnacknowledgedChanges</c>=true: el operador confirmo un servicio con otro precio/condicion, el
    /// vendedor lo reflejo editando el servicio, el saldo del cliente ya se ajusto solo y la reserva espera
    /// el OK del dueño.
    ///
    /// <para>Filtra por estado vivo aunque el flag siga en true: si una reserva marcada cae luego a
    /// Cancelada/Cerrada, deja de avisar (el flag se limpia recien al acusar, pero el aviso no tiene sentido
    /// fuera de un estado vivo). Mismo gating de visibilidad que las demas alarmas: admin ve todas, el
    /// vendedor solo SUS reservas, no-admin sin identidad -> vacio (fail-closed). NO expone montos.</para>
    /// </summary>
    private async Task<List<object>> ComputeConfirmedWithChangesAsync(AlertCallerContext caller, CancellationToken ct)
    {
        // Fail-closed: un no-admin sin identidad no ve avisos de nadie (mismo borde que UpcomingStarts).
        if (!caller.IsAdmin && string.IsNullOrEmpty(caller.UserId))
            return new List<object>();

        var rows = await _context.Reservas
            .Where(r => r.HasUnacknowledgedChanges
                        && (r.Status == EstadoReserva.InManagement
                            || r.Status == EstadoReserva.Confirmed
                            || r.Status == EstadoReserva.Traveling)
                        && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId))
            .Select(r => new
            {
                r.PublicId,
                r.NumeroReserva,
                r.Name,
                r.Status,
                r.ChangesPendingSince,
                PayerName = r.Payer != null ? r.Payer.FullName : null
            })
            // Lo que espera revision desde hace mas tiempo, primero.
            .OrderBy(r => r.ChangesPendingSince)
            .ToListAsync(ct);

        return rows
            .Select(r => (object)new
            {
                ReservaPublicId = r.PublicId,
                NumeroReserva = r.NumeroReserva,
                Name = r.Name,
                Status = r.Status,
                HolderName = r.PayerName,
                ChangesPendingSince = r.ChangesPendingSince
            })
            .ToList();
    }

    /// <summary>
    /// ADR-033 (2026-06-16, E6/B3 — SOLO VISIBILIDAD): reservas atascadas en "esperando refund del operador"
    /// (PendingOperatorRefund) cuyo saldo a favor del cliente quedo SIN consumir (RemainingBalance &gt; 0).
    /// La cancelacion ya devolvio la plata como saldo a favor, pero si el cliente nunca lo aplica/cobra, la
    /// reserva queda colgada para siempre. Esta alarma la hace VISIBLE para que alguien la accione.
    ///
    /// <para>Solo visibilidad: NO da de baja ni devuelve el remanente (eso espera firma de contador, Parte C
    /// del ADR). NO toca el estado de la reserva.</para>
    ///
    /// <para>El saldo a favor de cancelacion se ata a la reserva por la cadena
    /// ClientCreditEntry.BookingCancellationId -&gt; BookingCancellation.ReservaId. Se suma el remanente vivo
    /// por reserva (puede haber mas de un entry). Mismo gating de visibilidad que los demas buckets nuevos.</para>
    /// </summary>
    private async Task<List<object>> ComputeStuckOperatorRefundsAsync(AlertCallerContext caller, CancellationToken ct)
    {
        // Fail-closed: un no-admin sin identidad no ve avisos de nadie (mismo borde que UpcomingStarts).
        if (!caller.IsAdmin && string.IsNullOrEmpty(caller.UserId))
            return new List<object>();

        // Saldos a favor de cancelacion todavia vivos (RemainingBalance > 0), unidos a su reserva via la BC.
        // Se filtra a reservas que SIGUEN esperando refund: si el cliente ya consumio todo, la reserva paso a
        // Cancelada (OnAllCreditConsumedAsync) y no es un caso "atascado".
        var query =
            from credit in _context.ClientCreditEntries
            where credit.RemainingBalance > 0 && credit.BookingCancellationId != null
            join bc in _context.BookingCancellations on credit.BookingCancellationId equals bc.Id
            join reserva in _context.Reservas on bc.ReservaId equals reserva.Id
            where reserva.Status == EstadoReserva.PendingOperatorRefund
                  && (caller.IsAdmin || reserva.ResponsibleUserId == caller.UserId)
            select new
            {
                reserva.PublicId,
                reserva.NumeroReserva,
                reserva.Name,
                PayerName = reserva.Payer != null ? reserva.Payer.FullName : null,
                credit.Currency,
                credit.RemainingBalance,
                credit.CreatedAt
            };

        var rows = await query.ToListAsync(ct);

        // Agrupamos por reserva + moneda: una reserva con remanente en dos monedas produce dos filas (igual
        // criterio que el resto de la plata, que nunca mezcla monedas en un solo numero). Lo mas viejo
        // primero (lleva mas tiempo atascado) -> se ordena ANTES de boxear a object.
        return rows
            .GroupBy(r => new { r.PublicId, r.NumeroReserva, r.Name, r.PayerName, r.Currency })
            .Select(g => new
            {
                ReservaPublicId = g.Key.PublicId,
                NumeroReserva = g.Key.NumeroReserva,
                Name = g.Key.Name,
                HolderName = g.Key.PayerName,
                Currency = Monedas.Normalizar(g.Key.Currency),
                RemainingCredit = EconomicRulesHelper.RoundCurrency(g.Sum(x => x.RemainingBalance)),
                Since = g.Min(x => x.CreatedAt)
            })
            .OrderBy(x => x.Since)
            .Select(x => (object)x)
            .ToList();
    }

    /// <summary>
    /// (2026-06-26): alarma "el operador no reembolso". UN aviso POR RESERVA cuya cancelacion quedo sin que el
    /// operador devuelva la plata: ya sea <c>AbandonedByOperator</c> (el plazo vencio y el job nocturno la cerro)
    /// o <c>AwaitingOperatorRefund</c> con <c>OperatorRefundDueBy</c> ya vencido (todavia sin cerrar — entre que
    /// vence y corre el job, o si el job aun no paso).
    ///
    /// <para>Cierra el hueco por el que la cuenta por cobrar al operador quedaba colgada sin alerta: antes el
    /// estado <c>AbandonedByOperator</c> ni se asignaba. El usuario ve la lista para reclamar al operador o dar
    /// la cancelacion por perdida (<c>AbandonedByOperator</c> es terminal: registrar un reembolso tardio sobre
    /// una BC ya abandonada NO esta implementado, es follow-up futuro).</para>
    ///
    /// <para><b>Cota temporal</b>: solo se avisan las cuya <c>OperatorRefundDueBy</c> cayó en los ultimos
    /// <see cref="AbandonedOperatorRefundAlertDays"/> dias. Sin este tope, como <c>AbandonedByOperator</c> no
    /// transiciona nunca, cada cancelacion abandonada se acumularía en la campanita para siempre.</para>
    ///
    /// <para>Mismo gating de visibilidad que los demas buckets nuevos: admin ve todas, el vendedor solo SUS
    /// reservas (<c>reserva.ResponsibleUserId</c>), no-admin sin identidad -&gt; vacio (fail-closed). NO expone
    /// montos (es solo visibilidad); el detalle economico se ve abriendo la cancelacion.</para>
    /// </summary>
    private async Task<List<object>> ComputeAbandonedOperatorRefundsAsync(
        AlertCallerContext caller, DateTime today, CancellationToken ct)
    {
        // Fail-closed: un no-admin sin identidad no ve avisos de nadie (mismo borde que UpcomingStarts).
        if (!caller.IsAdmin && string.IsNullOrEmpty(caller.UserId))
            return new List<object>();

        var nowUtc = DateTime.UtcNow;
        // Cota temporal (ver doc del metodo): solo las vencidas/abandonadas en los ultimos N dias.
        var cutoffUtc = nowUtc.AddDays(-AbandonedOperatorRefundAlertDays);

        // Cancelaciones con el reembolso del operador trabado: ya abandonadas por el job, o vencidas sin cerrar.
        // Acotadas a la ventana (OperatorRefundDueBy >= cutoff) para no acumular abandonadas viejas para siempre.
        // Se unen a su reserva para el scope por dueño y los datos de display.
        var query =
            from bc in _context.BookingCancellations
            where bc.OperatorRefundDueBy != null
                  && bc.OperatorRefundDueBy >= cutoffUtc
                  && (bc.Status == BookingCancellationStatus.AbandonedByOperator
                      || (bc.Status == BookingCancellationStatus.AwaitingOperatorRefund
                          && bc.OperatorRefundDueBy < nowUtc))
            join reserva in _context.Reservas on bc.ReservaId equals reserva.Id
            where caller.IsAdmin || reserva.ResponsibleUserId == caller.UserId
            select new
            {
                reserva.PublicId,
                reserva.NumeroReserva,
                reserva.Name,
                PayerName = reserva.Payer != null ? reserva.Payer.FullName : null,
                bc.Status,
                bc.OperatorRefundDueBy
            };

        var rows = await query.ToListAsync(ct);

        // daysOverdue: dias corridos desde que vencio el plazo (>= 0). El que mas tiempo lleva vencido, primero.
        return rows
            .Select(r =>
            {
                bool isAbandoned = r.Status == BookingCancellationStatus.AbandonedByOperator;
                int daysOverdue = r.OperatorRefundDueBy.HasValue
                    ? Math.Max(0, (int)(today - r.OperatorRefundDueBy.Value.Date).TotalDays)
                    : 0;
                return new
                {
                    ReservaPublicId = r.PublicId,
                    NumeroReserva = r.NumeroReserva,
                    Name = r.Name,
                    HolderName = r.PayerName,
                    // El front distingue "ya dada por perdida" de "vencida sin cerrar" por este campo.
                    Status = isAbandoned ? "AbandonedByOperator" : "Overdue",
                    OperatorRefundDueBy = r.OperatorRefundDueBy,
                    DaysOverdue = daysOverdue
                };
            })
            .OrderByDescending(x => x.DaysOverdue)
            .ThenBy(x => x.NumeroReserva, StringComparer.Ordinal)
            .Select(x => (object)x)
            .ToList();
    }

    /// <summary>
    /// Q9 (2026-06-24): alarma "presupuesto/cotizacion por caducar". UN aviso POR RESERVA en estado de
    /// pre-venta (Budget o Quotation) cuya caducidad esta CONFIGURADA (BudgetExpirationDays /
    /// QuotationExpirationDays &gt; 0) y a la que le faltan &lt;= <see cref="PreSaleExpiryAlertDays"/> dias
    /// para caducar a Perdido. Es un aviso APARTE del de "proximos inicios": las pre-ventas no entran a ese
    /// bucket; este avisa que la cotizacion esta por vencerse sola.
    ///
    /// <para><b>Coherencia con la caducidad (CRITICO)</b>: la antigüedad se mide IGUAL que el job G6
    /// (<c>ReservaLifecycleAutomationService.AutoExpireStalePreSaleAsync</c>): el momento en que la reserva
    /// ENTRO al estado actual = el ULTIMO <see cref="ReservaStatusChangeLog"/> con <c>ToStatus == estado</c>,
    /// y si no hay log (Quotation nace en ese estado sin log de transicion) se cae a <c>CreatedAt</c>. Asi
    /// el aviso y la caducidad cuentan los mismos dias: nunca avisamos "vence en 2 dias" y el job la caduca
    /// al dia siguiente.</para>
    ///
    /// <para><b>Dias restantes</b>: <c>plazo - dias_transcurridos</c>, redondeado hacia abajo (igual que el
    /// job, que compara "entro antes de hoy - X"). 1 -&gt; "vence mañana", 0 -&gt; "vence hoy". El texto se
    /// arma en el backend (legible, sin montos). Si el plazo ya se cumplio (dias restantes &lt; 0) NO se
    /// avisa: esa reserva la caduca el job, no es "por caducar". Mismo gating de visibilidad que las otras
    /// alarmas: admin ve todas, el vendedor solo SUS reservas, no-admin sin identidad -&gt; vacio.</para>
    /// </summary>
    private async Task<List<object>> ComputeExpiringPreSalesAsync(
        AlertCallerContext caller, OperationalFinanceSettings settings, CancellationToken ct)
    {
        // Fail-closed: un no-admin sin identidad no ve avisos de nadie (mismo borde que UpcomingStarts).
        if (!caller.IsAdmin && string.IsNullOrEmpty(caller.UserId))
            return new List<object>();

        var alerts = new List<object>();

        // Presupuesto y Cotizacion son ejes SEPARADOS: cada uno con su propio plazo y solo si esta activo
        // (dias > 0). Si el dueño desactivo la caducidad de un tipo, no avisamos por ese tipo.
        if (settings.BudgetExpirationDays > 0)
        {
            await CollectExpiringPreSaleAsync(
                caller, EstadoReserva.Budget, "Presupuesto", settings.BudgetExpirationDays, alerts, ct);
        }

        if (settings.QuotationExpirationDays > 0)
        {
            await CollectExpiringPreSaleAsync(
                caller, EstadoReserva.Quotation, "Cotización", settings.QuotationExpirationDays, alerts, ct);
        }

        // Orden estable: lo que vence antes (menos dias restantes) primero.
        return alerts
            .OrderBy(a => (int)a.GetType().GetProperty("DaysLeft")!.GetValue(a)!)
            .ToList();
    }

    /// <summary>
    /// Q9: junta en <paramref name="alerts"/> los avisos de un estado de pre-venta concreto. Mide la
    /// antigüedad con la MISMA logica que el job G6 (ultimo log de entrada al estado, fallback CreatedAt) y
    /// avisa solo cuando faltan entre 0 y <see cref="PreSaleExpiryAlertDays"/> dias para caducar.
    /// </summary>
    private async Task CollectExpiringPreSaleAsync(
        AlertCallerContext caller,
        string preSaleStatus,
        string preSaleLabel,
        int expirationDays,
        List<object> alerts,
        CancellationToken ct)
    {
        // Candidatas: las que SIGUEN en este estado de pre-venta, visibles para el caller. Traemos lo minimo
        // para el aviso + CreatedAt para el fallback de antigüedad. NO exponemos montos ni costos.
        var candidates = await _context.Reservas
            .Where(r => r.Status == preSaleStatus
                        && (caller.IsAdmin || r.ResponsibleUserId == caller.UserId))
            .Select(r => new
            {
                r.Id,
                r.PublicId,
                r.NumeroReserva,
                r.Name,
                r.CreatedAt,
                PayerName = r.Payer != null ? r.Payer.FullName : null
            })
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return;

        var candidateIds = candidates.Select(c => c.Id).ToList();

        // Momento en que cada candidata entro a ESTE estado = el ultimo log con ToStatus == preSaleStatus.
        // Una sola query + agrupacion en memoria (sin N+1). Misma medicion que el job de caducidad.
        var logRows = await _context.ReservaStatusChangeLogs
            .Where(log => candidateIds.Contains(log.ReservaId) && log.ToStatus == preSaleStatus)
            .Select(log => new { log.ReservaId, log.OccurredAt })
            .ToListAsync(ct);

        var enteredStateAt = logRows
            .GroupBy(row => row.ReservaId)
            .ToDictionary(group => group.Key, group => group.Max(row => row.OccurredAt));

        var nowUtc = DateTime.UtcNow;

        foreach (var candidate in candidates)
        {
            // Sin log para este estado (Quotation inicial) -> desde la creacion, igual que el job.
            var sinceUtc = enteredStateAt.TryGetValue(candidate.Id, out var loggedAt)
                ? loggedAt
                : candidate.CreatedAt;

            // Dias transcurridos en el estado (corridos, UTC) y dias que faltan para caducar. Floor de los
            // dias transcurridos para que "vence hoy/mañana" coincida con el corte por dias del job.
            var daysElapsed = (int)Math.Floor((nowUtc - sinceUtc).TotalDays);
            var daysLeft = expirationDays - daysElapsed;

            // Solo "por caducar": entre 0 (vence hoy) y la anticipacion. Si daysLeft < 0 ya supero el plazo
            // (la caduca el job, no es un aviso "por caducar"); si es mayor a la anticipacion, todavia falta.
            if (daysLeft < 0 || daysLeft > PreSaleExpiryAlertDays)
                continue;

            alerts.Add(new
            {
                ReservaPublicId = candidate.PublicId,
                NumeroReserva = candidate.NumeroReserva,
                Name = candidate.Name,
                HolderName = candidate.PayerName,
                PreSaleKind = preSaleStatus,
                DaysLeft = daysLeft,
                Message = BuildPreSaleExpiryMessage(preSaleLabel, candidate.PayerName ?? candidate.Name, daysLeft)
            });
        }
    }

    /// <summary>
    /// Q9: arma el texto legible del aviso de caducidad. 1 dia -&gt; "vence mañana"; 0 -&gt; "vence hoy";
    /// resto -&gt; "vence en N dias". Sin montos ni costos.
    /// </summary>
    private static string BuildPreSaleExpiryMessage(string preSaleLabel, string clientName, int daysLeft)
    {
        var when = daysLeft switch
        {
            0 => "vence hoy",
            1 => "vence mañana",
            _ => $"vence en {daysLeft} días"
        };
        return $"El {preSaleLabel.ToLowerInvariant()} de {clientName} {when}.";
    }

    /// <summary>
    /// Item de alarma de fecha limite (pago al operador / time-limit de emision). El texto de negocio viaja
    /// listo: <c>deadline</c> (fecha) + <c>daysLeft</c> (negativo = vencido) para que el front no recompute.
    /// </summary>
    private static object BuildDeadlineAlert(
        Guid reservaPublicId, string numeroReserva, string serviceKind, string? serviceLabel,
        DateTime deadline, DateTime today)
    {
        // deadline ya es fecha de pared Kind=Utc (NormalizeCalendarDate al persistir). La resta da dias
        // enteros exactos; daysLeft < 0 => vencido (el front lo pinta en rojo "Vencio hace N dias").
        var deadlineDate = DateTime.SpecifyKind(deadline.Date, DateTimeKind.Utc);
        var daysLeft = (int)(deadlineDate - today).TotalDays;
        return new
        {
            ReservaPublicId = reservaPublicId,
            NumeroReserva = numeroReserva,
            ServiceKind = serviceKind,
            ServiceLabel = string.IsNullOrWhiteSpace(serviceLabel) ? serviceKind : serviceLabel,
            Deadline = deadlineDate,
            DaysLeft = daysLeft
        };
    }

    /// <summary>
    /// ADR-019 D4: implementacion del "Listo" global. El controller ya paso el filtro de ownership
    /// ([RequireOwnership] con bypass Admin/reservas.view_all), asi que aca solo queda: flag, existencia,
    /// recalculo server-side del primer inicio y upsert idempotente del descarte.
    /// </summary>
    public async Task<UpcomingStartDismissOutcome> DismissUpcomingStartAsync(
        string reservaPublicIdOrLegacyId, string dismissedByUserId, CancellationToken cancellationToken)
    {
        // Flag OFF -> la feature "no existe" (404 en el controller). OJO: el filtro de ownership corre
        // ANTES que este check, asi que un no-owner recibe 403 aun con flag OFF — trade-off aceptado
        // en el ADR (revela que la ruta existe, no revela datos).
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        if (!settings.EnableServiceDeadlineAlerts)
            return UpcomingStartDismissOutcome.FeatureDisabled;

        // Resolver la reserva por PublicId (Guid) o id legacy (int) — mismo contrato dual que
        // OwnershipResolver.ParseId, para que el filtro y el controller hablen del mismo recurso.
        var reservaId = await ResolveReservaIdAsync(reservaPublicIdOrLegacyId, cancellationToken);
        if (reservaId is null)
            return UpcomingStartDismissOutcome.ReservaNotFound;

        // El SERVER recalcula el primer inicio con el MISMO helper del bucket — el cliente no manda
        // fecha (elimina la carrera "vi una fecha, descarto otra": se descarta lo que el server ve AHORA;
        // si difiere de lo que vio el usuario, el re-armado de D3 lo cubre).
        var firstStart = await UpcomingStartCalculator.ComputeFirstStartAsync(_context, reservaId.Value, cancellationToken);
        if (firstStart is null)
            return UpcomingStartDismissOutcome.NoUpcomingStart; // 204 no-op: no se escribe nada

        try
        {
            await UpsertDismissalAsync(reservaId.Value, firstStart.Value, dismissedByUserId, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Carrera de dos POST simultaneos: ambos no encontraron fila y ambos insertaron; el indice
            // UNIQUE de Postgres rechazo a uno. Reintentamos UNA vez como update — ahora la fila del
            // ganador ya existe y el upsert la pisa. (El provider InMemory no aplica el UNIQUE, asi que
            // este camino solo se ejercita en los tests de integracion Postgres — M4 del ADR.)
            _context.ChangeTracker.Clear();
            await UpsertDismissalAsync(reservaId.Value, firstStart.Value, dismissedByUserId, cancellationToken);
        }

        // Observabilidad (D4): la fila misma es el audit trail minimo; el log estructurado ayuda a
        // reconstruir "quien apago que" sin query. Identificadores seguros, sin datos de pasajeros.
        _logger.LogInformation(
            "UpcomingStart dismissed. ReservaId={ReservaId} FirstStartDate={FirstStartDate} DismissedBy={UserId}",
            reservaId.Value, firstStart.Value.ToString("yyyy-MM-dd"), dismissedByUserId);

        return UpcomingStartDismissOutcome.Dismissed;
    }

    /// <summary>
    /// Inserta o pisa LA fila de descarte de la reserva (a lo sumo una, UNIQUE en ReservaId).
    /// Re-descartar actualiza fecha + auditoria; no se acumula historia (trade-off aceptado en D3).
    /// </summary>
    private async Task UpsertDismissalAsync(int reservaId, DateTime firstStart, string userId, CancellationToken ct)
    {
        var existing = await _context.UpcomingStartAlertDismissals
            .FirstOrDefaultAsync(d => d.ReservaId == reservaId, ct);

        if (existing is null)
        {
            existing = new UpcomingStartAlertDismissal { ReservaId = reservaId };
            _context.UpcomingStartAlertDismissals.Add(existing);
        }

        existing.DismissedFirstStartDate = firstStart;
        existing.DismissedByUserId = userId;
        existing.DismissedAtUtc = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Traduce el id de ruta (PublicId Guid o id legacy int) al Id interno de la reserva.
    /// Null = no existe (404 para quien paso el filtro de ownership: Admin / reservas.view_all).
    /// </summary>
    private async Task<int?> ResolveReservaIdAsync(string publicIdOrLegacyId, CancellationToken ct)
    {
        if (Guid.TryParse(publicIdOrLegacyId, out var publicId))
        {
            var byPublicId = await _context.Reservas
                .Where(r => r.PublicId == publicId)
                .Select(r => (int?)r.Id)
                .FirstOrDefaultAsync(ct);
            return byPublicId;
        }

        if (int.TryParse(publicIdOrLegacyId, out var legacyId) && legacyId > 0)
        {
            var byLegacyId = await _context.Reservas
                .Where(r => r.Id == legacyId)
                .Select(r => (int?)r.Id)
                .FirstOrDefaultAsync(ct);
            return byLegacyId;
        }

        return null;
    }

    private static object BuildCostToConfirmAlert(
        Guid reservaPublicId, string numeroReserva, string serviceKind, string? serviceLabel, string? reason)
        => new
        {
            ReservaPublicId = reservaPublicId,
            NumeroReserva = numeroReserva,
            ServiceKind = serviceKind,
            ServiceLabel = string.IsNullOrWhiteSpace(serviceLabel) ? serviceKind : serviceLabel,
            Reason = reason
        };
}
