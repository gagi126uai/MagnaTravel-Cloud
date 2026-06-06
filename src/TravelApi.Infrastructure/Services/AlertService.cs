using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Time;

namespace TravelApi.Infrastructure.Services;

public class AlertService : IAlertService
{
    private readonly AppDbContext _context;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;

    public AlertService(AppDbContext context, IOperationalFinanceSettingsService operationalFinanceSettingsService)
    {
        _context = context;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
    }

    public async Task<object> GetAlertsAsync(AlertCallerContext caller, CancellationToken cancellationToken)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);

        // --- Buckets financieros (admin-only, Fuga 2 ADR-017 §2.7 F1b) ---
        // UrgentTrips y SupplierDebts son informacion financiera de TODA la agencia (deudas a
        // proveedores, saldos de clientes). El gating "solo admin" vive en el SERVER: un no-admin
        // los recibe vacios. Estos buckets NO cambian en F1.4 (siguen usando UtcNow.Date como "hoy",
        // como manda el ADR §2.2: "los buckets existentes NO se tocan").
        IReadOnlyList<object> urgentTrips = Array.Empty<object>();
        IReadOnlyList<object> supplierDebts = Array.Empty<object>();
        if (caller.IsAdmin)
        {
            (urgentTrips, supplierDebts) = await ComputeFinancialBucketsAsync(settings, cancellationToken);
        }
        var financialCount = urgentTrips.Count + supplierDebts.Count;

        // --- Buckets nuevos F1.4 (cada uno detras de su gate) ---
        var serviceDeadlinesActive = settings.EnableServiceDeadlineAlerts;
        // CostsToConfirm: gateado por el flag del catalogo + que el caller pueda ver costos (§2.8/D8b).
        var costsToConfirmActive = settings.EnableCatalogFindOrCreate && caller.CanSeeCost;

        // CAMINO BYTE-IDENTICO (default en prod): si ningun bucket nuevo esta activo, devolvemos el
        // MISMO objeto anonimo de siempre (mismas 3 propiedades, mismo orden) — /alerts no cambia en
        // nada para los consumidores actuales.
        if (!serviceDeadlinesActive && !costsToConfirmActive)
        {
            return new
            {
                UrgentTrips = urgentTrips,
                SupplierDebts = supplierDebts,
                TotalCount = financialCount
            };
        }

        // Con al menos un bucket nuevo activo, el "hoy" del corte de deadlines es la fecha LOCAL de
        // Argentina (ADR §2.2): comparar contra UtcNow.Date marcaria "vencido" 3h antes (21:00 ART).
        var today = AgencyTimezone.TodayWallClockUtc();

        var serviceDeadlines = serviceDeadlinesActive
            ? await ComputeServiceDeadlinesAsync(caller, settings.ServiceDeadlineAlertDays, today, cancellationToken)
            : new List<object>();

        var costsToConfirm = costsToConfirmActive
            ? await ComputeCostsToConfirmAsync(caller, cancellationToken)
            : new List<object>();

        // Objeto extendido (solo en el path con flag ON): claves nuevas aditivas. Devolvemos un DTO TIPADO
        // (NO un Dictionary<string,object>) para que System.Text.Json aplique el PropertyNamingPolicy camelCase
        // a las claves — un diccionario deja las claves verbatim (PascalCase) y prender el flag renombraria en
        // silencio urgentTrips->UrgentTrips, rompiendo a los consumidores. Ver AlertsResponse. Cada bucket nuevo
        // se incluye SOLO si su gate esta activo (null = se omite del JSON), misma presencia condicional que antes.
        var totalCount = financialCount + serviceDeadlines.Count + costsToConfirm.Count;
        return new AlertsResponse(
            urgentTrips: urgentTrips,
            supplierDebts: supplierDebts,
            serviceDeadlines: serviceDeadlinesActive ? serviceDeadlines : null,
            costsToConfirm: costsToConfirmActive ? costsToConfirm : null,
            totalCount: totalCount);
    }

    /// <summary>
    /// Buckets financieros historicos (viajes urgentes con saldo + deudas a proveedores). Codigo movido
    /// tal cual del metodo principal: mismo criterio y mismo "hoy" (UtcNow.Date) que antes de F1.4.
    /// </summary>
    private async Task<(IReadOnlyList<object> UrgentTrips, IReadOnlyList<object> SupplierDebts)>
        ComputeFinancialBucketsAsync(OperationalFinanceSettings settings, CancellationToken cancellationToken)
    {
        var today = DateTime.UtcNow.Date;
        var threshold = today.AddDays(Math.Max(settings.UpcomingUnpaidReservationAlertDays, 1));

        // Fase D (rediseño Sold/ToSettle): "viajes urgentes" = reservas activas con viaje inminente y
        // saldo pendiente. Sumamos Sold (vendida pero el operador todavia no confirmo). NO sumamos
        // ToSettle (post-viaje). Con el flag EnableSoldToSettleStates OFF nunca hay filas en Sold, asi
        // que el resultado es identico al historico.
        var urgentTrips = await _context.Reservas
            .Where(f => (f.Status == EstadoReserva.Sold ||
                         f.Status == EstadoReserva.Confirmed ||
                         f.Status == EstadoReserva.Traveling) &&
                        f.StartDate >= today &&
                        f.StartDate <= threshold &&
                        f.Balance > 0)
            .Select(f => new
            {
                f.PublicId,
                f.NumeroReserva,
                f.Name,
                f.StartDate,
                f.Balance,
                f.Status,
                PayerName = f.Payer != null ? f.Payer.FullName : "Sin Cliente"
            })
            .OrderBy(f => f.StartDate)
            .ToListAsync(cancellationToken);

        var supplierDebts = await _context.Suppliers
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

        return (urgentTrips, supplierDebts);
    }

    /// <summary>
    /// ADR-017 F1.4 (§2.5, R9): bucket <c>ServiceDeadlines</c> compute-on-read. Avisa fechas limite de
    /// seña/pago al operador (Hotel/Paquete) y de emision de ticket (Aereo) que caen dentro de la ventana
    /// o ya vencieron (<c>isOverdue=true</c>), mientras la reserva siga activa y el viaje no haya empezado.
    ///
    /// <para>Visibilidad (D2): admin ve todas; el vendedor ve solo las de SUS reservas
    /// (<c>Reserva.ResponsibleUserId == caller.UserId</c>). Limitacion conocida: reservas con
    /// <c>ResponsibleUserId</c> null (backfill historico pendiente) solo le suenan al admin.</para>
    /// </summary>
    private async Task<List<object>> ComputeServiceDeadlinesAsync(
        AlertCallerContext caller, int alertDays, DateTime today, CancellationToken ct)
    {
        // Seguridad (F1.4 review): un no-admin SIN identidad (token sin claim NameIdentifier -> UserId null) no
        // debe ver los deadlines de NADIE. Sin esta guarda, el predicado de abajo "ResponsibleUserId == caller.UserId"
        // se traduce a SQL como "ResponsibleUserId IS NULL" y le mostraria todas las reservas sin responsable
        // asignado (backfill historico). Fail-closed: cortamos a vacio antes de tocar la base.
        if (!caller.IsAdmin && string.IsNullOrEmpty(caller.UserId))
            return new List<object>();

        // Ventana inclusiva: un deadline entra si vence dentro de [hoy ... hoy + ventana]. Los ya vencidos
        // (deadline < hoy) tambien entran (isOverdue=true): siguen siendo accionables.
        var window = today.AddDays(Math.Max(alertDays, 1));
        var alerts = new List<object>();

        // Hotel (seña/pago al operador). Join explicito con Reservas (no navegacion) para que funcione
        // igual en Postgres y en el provider InMemory de los tests. Los predicados van INLINE (no en un
        // helper) porque EF Core no traduce llamadas a metodos propios dentro de la query.
        var hotelDeadlines = await (
            from hotel in _context.HotelBookings
            join reserva in _context.Reservas on hotel.ReservaId equals reserva.Id
            where hotel.OperatorPaymentDeadline != null
                  && hotel.OperatorPaymentDeadline <= window
                  && hotel.Status != "Cancelado"
                  && (reserva.Status == EstadoReserva.Budget
                      || reserva.Status == EstadoReserva.Sold
                      || reserva.Status == EstadoReserva.Confirmed)
                  && (reserva.StartDate == null || reserva.StartDate >= today)
                  && (caller.IsAdmin || reserva.ResponsibleUserId == caller.UserId)
            select new
            {
                reserva.PublicId,
                reserva.NumeroReserva,
                Label = hotel.HotelName,
                Deadline = hotel.OperatorPaymentDeadline!.Value
            }).ToListAsync(ct);

        foreach (var row in hotelDeadlines)
        {
            alerts.Add(BuildDeadlineAlert(
                row.PublicId, row.NumeroReserva, "Hotel",
                string.IsNullOrWhiteSpace(row.Label) ? "Hotel" : row.Label,
                "OperatorPayment", row.Deadline, today));
        }

        // Paquete (seña/pago al operador).
        var packageDeadlines = await (
            from package in _context.PackageBookings
            join reserva in _context.Reservas on package.ReservaId equals reserva.Id
            where package.OperatorPaymentDeadline != null
                  && package.OperatorPaymentDeadline <= window
                  && package.Status != "Cancelado"
                  && (reserva.Status == EstadoReserva.Budget
                      || reserva.Status == EstadoReserva.Sold
                      || reserva.Status == EstadoReserva.Confirmed)
                  && (reserva.StartDate == null || reserva.StartDate >= today)
                  && (caller.IsAdmin || reserva.ResponsibleUserId == caller.UserId)
            select new
            {
                reserva.PublicId,
                reserva.NumeroReserva,
                Label = package.PackageName,
                Deadline = package.OperatorPaymentDeadline!.Value
            }).ToListAsync(ct);

        foreach (var row in packageDeadlines)
        {
            alerts.Add(BuildDeadlineAlert(
                row.PublicId, row.NumeroReserva, "Paquete",
                string.IsNullOrWhiteSpace(row.Label) ? "Paquete" : row.Label,
                "OperatorPayment", row.Deadline, today));
        }

        // Aereo (emision de ticket). Se trae a memoria para agrupar por (Reserva, PNR) con MIN(deadline):
        // el deadline conceptual es del PNR, pero la columna es por segmento. Segmentos cancelados
        // (UN/UC/HX/NO, ver WorkflowStatusHelper) quedan afuera.
        var flightRows = await (
            from flight in _context.FlightSegments
            join reserva in _context.Reservas on flight.ReservaId equals reserva.Id
            where flight.TicketingDeadline != null
                  && flight.TicketingDeadline <= window
                  && flight.Status != "UN" && flight.Status != "UC"
                  && flight.Status != "HX" && flight.Status != "NO"
                  && (reserva.Status == EstadoReserva.Budget
                      || reserva.Status == EstadoReserva.Sold
                      || reserva.Status == EstadoReserva.Confirmed)
                  && (reserva.StartDate == null || reserva.StartDate >= today)
                  && (caller.IsAdmin || reserva.ResponsibleUserId == caller.UserId)
            select new
            {
                flight.ReservaId,
                reserva.PublicId,
                reserva.NumeroReserva,
                flight.PNR,
                flight.AirlineCode,
                flight.FlightNumber,
                flight.Origin,
                flight.Destination,
                // ADR-018: identidad de la ficha "producto-primero"; fallback cuando los estructurados son null.
                flight.ProductName,
                Deadline = flight.TicketingDeadline!.Value
            }).ToListAsync(ct);

        // PNR utilizable = no null/vacio y distinto de "TBD" (placeholder que generan ConvertToFile y
        // cargas manuales). Sin PNR utilizable: cada segmento emite su propio aviso (no se agrupa).
        static bool PnrUsable(string? pnr)
            => !string.IsNullOrWhiteSpace(pnr) && !pnr.Trim().Equals("TBD", StringComparison.OrdinalIgnoreCase);

        foreach (var group in flightRows.Where(r => PnrUsable(r.PNR))
                     .GroupBy(r => new { r.ReservaId, Pnr = r.PNR!.Trim() }))
        {
            var earliest = group.Min(r => r.Deadline);
            var sample = group.First();
            // ADR-018: si la ficha "producto-primero" no cargo origen/destino, mostramos el ProductName
            // en vez de "Aereo -". Orden: ruta cargada -> ProductName -> aerolinea+numero.
            var groupedIdentity = ServiceDisplayName.FirstNonBlank(
                ServiceDisplayName.RouteOrEmpty(sample.Origin, sample.Destination),
                sample.ProductName,
                $"{sample.AirlineCode}{sample.FlightNumber}".Trim());
            alerts.Add(BuildDeadlineAlert(
                sample.PublicId, sample.NumeroReserva, "Aereo",
                $"Aereo {groupedIdentity} (PNR {group.Key.Pnr})",
                "Ticketing", earliest, today));
        }

        foreach (var row in flightRows.Where(r => !PnrUsable(r.PNR)))
        {
            // ADR-018: identidad = ProductName si no hay aerolinea/numero; la ruta va entre parentesis solo si esta.
            var identity = ServiceDisplayName.ForFlight(row.ProductName, row.AirlineCode, row.FlightNumber);
            var route = ServiceDisplayName.RouteOrEmpty(row.Origin, row.Destination);
            var label = string.IsNullOrEmpty(route) ? $"Aereo {identity}" : $"Aereo {identity} ({route})";
            alerts.Add(BuildDeadlineAlert(
                row.PublicId, row.NumeroReserva, "Aereo",
                label,
                "Ticketing", row.Deadline, today));
        }

        return alerts;
    }

    /// <summary>
    /// ADR-017 F1.4 (§2.8, D8b): bucket <c>CostsToConfirm</c> compute-on-read. Lista los servicios marcados
    /// "costo a confirmar" (D7) para que alguien con permiso los revise/confirme. NO expone montos (solo
    /// reserva, tipo, etiqueta y razon). Gateado server-side por <c>cobranzas.see_cost</c> (decidido en el
    /// controller) + el flag del catalogo. Mismo filtro por caller que ServiceDeadlines: admin todas, el
    /// resto solo las de SUS reservas.
    /// </summary>
    private async Task<List<object>> ComputeCostsToConfirmAsync(AlertCallerContext caller, CancellationToken ct)
    {
        // Seguridad (F1.4 review, defensa en profundidad): mismo borde que ServiceDeadlines. Hoy el controller ya
        // pone CanSeeCost=false cuando el UserId es null (asi este bucket ni se calcula), pero NO confiamos en eso
        // aca: un no-admin sin identidad corta a vacio antes de filtrar por "ResponsibleUserId == UserId".
        if (!caller.IsAdmin && string.IsNullOrEmpty(caller.UserId))
            return new List<object>();

        var alerts = new List<object>();

        // Reserva "viva" a efectos de este bucket: no cancelada, no cerrada, no en espera de refund. Es
        // menos estricta que el filtro de deadlines a proposito: un costo se puede confirmar aunque el viaje
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
            select new { r.PublicId, r.NumeroReserva, Label = t.VehicleType, t.CostToConfirmReason }).ToListAsync(ct);
        alerts.AddRange(transfer.Select(x =>
            BuildCostToConfirmAlert(x.PublicId, x.NumeroReserva, "Traslado", x.Label, x.CostToConfirmReason)));

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

    private static object BuildDeadlineAlert(
        Guid reservaPublicId, string numeroReserva, string serviceKind, string serviceLabel,
        string deadlineKind, DateTime deadline, DateTime today)
        => new
        {
            ReservaPublicId = reservaPublicId,
            NumeroReserva = numeroReserva,
            ServiceKind = serviceKind,
            ServiceLabel = serviceLabel,
            DeadlineKind = deadlineKind,
            Deadline = deadline,
            IsOverdue = deadline < today
        };

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
