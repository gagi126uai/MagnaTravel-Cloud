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

        // --- Buckets financieros (admin-only, Fuga 2 ADR-017 §2.7 F1b) ---
        // UrgentTrips y SupplierDebts son informacion financiera de TODA la agencia (deudas a
        // proveedores, saldos de clientes). El gating "solo admin" vive en el SERVER: un no-admin
        // los recibe vacios. Estos buckets siguen usando UtcNow.Date como "hoy" (no se tocan).
        IReadOnlyList<object> urgentTrips = Array.Empty<object>();
        IReadOnlyList<object> supplierDebts = Array.Empty<object>();
        if (caller.IsAdmin)
        {
            (urgentTrips, supplierDebts) = await ComputeFinancialBucketsAsync(settings, cancellationToken);
        }
        var financialCount = urgentTrips.Count + supplierDebts.Count;

        // --- Buckets gateados (cada uno detras de su flag) ---
        // ADR-019: el flag EnableServiceDeadlineAlerts ahora gatea el bucket UpcomingStarts ("Proximos
        // inicios"). El nombre interno NO se renombra (decision D7: renombrar = migracion + churn por
        // cero valor de usuario); el nombre de cara al dueño es solo el texto de la UI.
        var upcomingStartsActive = settings.EnableServiceDeadlineAlerts;
        // CostsToConfirm: gateado por el flag del catalogo + que el caller pueda ver costos (§2.8/D8b).
        var costsToConfirmActive = settings.EnableCatalogFindOrCreate && caller.CanSeeCost;

        // CAMINO BYTE-IDENTICO (default en prod): si ningun bucket nuevo esta activo, devolvemos el
        // MISMO objeto anonimo de siempre (mismas 3 propiedades, mismo orden) — /alerts no cambia en
        // nada para los consumidores actuales.
        if (!upcomingStartsActive && !costsToConfirmActive)
        {
            return new
            {
                UrgentTrips = urgentTrips,
                SupplierDebts = supplierDebts,
                TotalCount = financialCount
            };
        }

        // Con al menos un bucket nuevo activo, el "hoy" del corte es la fecha LOCAL de Argentina:
        // comparar contra UtcNow.Date correria el dia 3h antes (a las 21:00 ART ya seria "mañana").
        var today = AgencyTimezone.TodayWallClockUtc();

        var upcomingStarts = upcomingStartsActive
            ? await ComputeUpcomingStartsAsync(caller, settings.ServiceDeadlineAlertDays, today, cancellationToken)
            : new List<object>();

        var costsToConfirm = costsToConfirmActive
            ? await ComputeCostsToConfirmAsync(caller, cancellationToken)
            : new List<object>();

        // Objeto extendido (solo en el path con flag ON): claves nuevas aditivas. Devolvemos un DTO TIPADO
        // (NO un Dictionary<string,object>) para que System.Text.Json aplique el PropertyNamingPolicy camelCase
        // a las claves — un diccionario deja las claves verbatim (PascalCase) y prender el flag renombraria en
        // silencio urgentTrips->UrgentTrips, rompiendo a los consumidores. Ver AlertsResponse. Cada bucket nuevo
        // se incluye SOLO si su gate esta activo (null = se omite del JSON), misma presencia condicional que antes.
        var totalCount = financialCount + upcomingStarts.Count + costsToConfirm.Count;
        return new AlertsResponse(
            urgentTrips: urgentTrips,
            supplierDebts: supplierDebts,
            upcomingStarts: upcomingStartsActive ? upcomingStarts : null,
            // D1: la ventana viaja junto al bucket para que la pill por servicio (frontend) use el
            // mismo umbral. NO va en OperationalFlagsResponse (regla dura "solo booleanos").
            upcomingStartsWindowDays: upcomingStartsActive ? settings.ServiceDeadlineAlertDays : null,
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

        // ADR-020 (2026-06-07): "viajes urgentes" = reservas activas con saldo pendiente. InManagement
        // (En gestion) reemplaza al viejo Sold. NO sumamos ToSettle (post-viaje).
        //
        // Cubre DOS casos (auditoria de negocio 2026-06-12, item 6 "viajó y debe"):
        //  (A) viaje INMINENTE: salida en [hoy ... hoy + ventana]. El cliente todavia no viajo y debe.
        //  (B) viaje EN CURSO con saldo: Status == Traveling y el viaje ya arranco pero no termino.
        //      Antes este caso DESAPARECIA: el prefiltro StartDate >= hoy lo excluia apenas empezaba el
        //      viaje, y la deuda nunca cerraba sola. Ahora se incluye sin tope de ventana (un viaje en
        //      curso impago es lo MAS urgente). "No termino" = sin EndDate o EndDate >= hoy.
        var urgentTrips = await _context.Reservas
            .Where(f => f.Balance > 0 &&
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
