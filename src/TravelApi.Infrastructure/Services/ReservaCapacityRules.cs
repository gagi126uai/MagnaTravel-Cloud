using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Reglas compartidas de validacion de Reservas para transiciones de estado.
/// Usado por ReservaService (transicion manual) y ReservaLifecycleAutomationService
/// (transicion automatica del job diario), para que las reglas vivan en un solo lugar.
///
/// Reglas implementadas:
/// - Capacidad pasajeros vs servicios (GetBlockReasonAsync).
/// - Estados de servicio: ningun servicio puede estar en "Solicitado" al pasar a
///   Operativo, porque esos no entran al balance del proveedor (ver SupplierService:205)
///   y dejarian la cuenta corriente con datos sucios (GetUnconfirmedServicesBlockReasonAsync).
/// </summary>
public static class ReservaCapacityRules
{
    /// <summary>
    /// Estados de servicio considerados "confirmados" para fines de balance con
    /// proveedor y para pasar la Reserva a Operativo. Refleja la misma lista que
    /// SupplierService usa para computar TotalPurchases.
    /// </summary>
    public static readonly HashSet<string> ConfirmedServiceStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Confirmado", "Emitido", "HK", "TK", "KK", "KL"
    };

    /// <summary>
    /// Estados de Reserva en los que NO se permite cargar/modificar servicios con
    /// estado distinto de "confirmado". El motivo: en Operativo/Closed la cuenta
    /// corriente del proveedor ya quedo cerrada en base a los confirmados; meter
    /// un servicio Solicitado nuevo o degradar uno confirmado romperia la coherencia.
    /// </summary>
    public static readonly HashSet<string> ReservaStatusesRequiringConfirmedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        EstadoReserva.Operational, EstadoReserva.Closed
    };

    /// <summary>
    /// Bloquea agregar o modificar un servicio con Status no-confirmado si la
    /// Reserva esta en Operativo o Cerrado. Aplica a Hotel/Transfer/Package/Flight
    /// y al ServicioReserva generico.
    ///
    /// Devuelve mensaje accionable o null si el cambio es coherente.
    /// </summary>
    public static async Task<string?> GetServiceStatusBlockReasonAsync(
        AppDbContext db,
        int reservaId,
        string serviceLabel,
        string? newServiceStatus,
        CancellationToken ct = default)
    {
        var reserva = await db.Reservas.AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => new { r.Status })
            .FirstOrDefaultAsync(ct);
        if (reserva == null) return null;

        if (!ReservaStatusesRequiringConfirmedServices.Contains(reserva.Status))
            return null; // Reserva en Presupuesto/Reservado/Cancelado: no aplica este check

        if (string.IsNullOrWhiteSpace(newServiceStatus))
            return $"El estado del servicio es obligatorio cuando la reserva esta en {reserva.Status}.";

        if (ConfirmedServiceStatuses.Contains(newServiceStatus))
            return null; // OK, esta confirmado

        return $"La reserva esta en estado {reserva.Status}. No se puede cargar/modificar el servicio '{serviceLabel}' " +
               $"con estado '{newServiceStatus}' — debe estar confirmado con el proveedor (alguno de: {string.Join(", ", ConfirmedServiceStatuses)}).";
    }

    /// <summary>
    /// Bloquea cambio de Status confirmado -> no-confirmado si la Reserva tiene
    /// SupplierPayments registrados. Razon: el balance del proveedor ya cuenta esos
    /// pagos contra el servicio confirmado; degradarlo a Solicitado/Cancelado
    /// rompe la coherencia (pagos colgando, balance sucio).
    ///
    /// Granularidad: a nivel Reserva (limitacion del modelo, SupplierPayment no
    /// apunta a HotelBooking/TransferBooking/etc. especifico). Si hay 2 hoteles y
    /// solo se pago uno, igual no podras des-confirmar el otro hasta anular los pagos.
    ///
    /// Devuelve mensaje accionable o null si el cambio es permitido.
    /// </summary>
    public static async Task<string?> GetStatusDowngradeBlockReasonAsync(
        AppDbContext db,
        int reservaId,
        string serviceLabel,
        string? oldServiceStatus,
        string? newServiceStatus,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(oldServiceStatus) || string.IsNullOrWhiteSpace(newServiceStatus))
            return null;

        // Solo aplica si: pasaba de confirmado a no-confirmado.
        var wasConfirmed = ConfirmedServiceStatuses.Contains(oldServiceStatus);
        var willBeConfirmed = ConfirmedServiceStatuses.Contains(newServiceStatus);
        if (!wasConfirmed || willBeConfirmed) return null;

        var totalPaid = await db.SupplierPayments.AsNoTracking()
            .Where(p => p.ReservaId == reservaId)
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        if (totalPaid <= 0m) return null;

        return $"No se puede degradar el estado del servicio '{serviceLabel}' (de '{oldServiceStatus}' a '{newServiceStatus}'): " +
               $"esta reserva tiene pagos al proveedor registrados por ${totalPaid:N2}. " +
               "Anula los pagos al proveedor antes de des-confirmar.";
    }

    /// <summary>
    /// Devuelve un mensaje de bloqueo si hay inconsistencia entre cantidad de pasajeros
    /// nominales y capacidad de los servicios cargados. Si todo coherente, devuelve null.
    /// Independiente del estado financiero (deuda).
    ///
    /// Chequeos:
    /// 1) Total: Passengers.Count > max capacidad de hoteles/transfers/packages.
    /// 2) Por servicio (si hay assignments en PassengerServiceAssignments):
    ///    assignments por servicio > capacidad de ese servicio.
    /// </summary>
    public static async Task<string?> GetBlockReasonAsync(AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        var reserva = await db.Reservas
            .AsNoTracking()
            .Include(r => r.Passengers)
            .Include(r => r.HotelBookings)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .FirstOrDefaultAsync(r => r.Id == reservaId, ct);
        if (reserva == null) return null;

        var paxCount = reserva.Passengers?.Count ?? 0;
        if (paxCount == 0) return null; // sin pasajeros no hay como exceder

        // Chequeo 1: total
        var hotelCap = reserva.HotelBookings?.Sum(h => h.GetExpectedPaxCount()) ?? 0;
        var transferCap = reserva.TransferBookings?.Max(t => (int?)t.GetExpectedPaxCount()) ?? 0;
        var packageCap = reserva.PackageBookings?.Sum(p => p.GetExpectedPaxCount()) ?? 0;
        var maxExpected = Math.Max(hotelCap, Math.Max(transferCap, packageCap));

        if (maxExpected > 0 && paxCount > maxExpected)
        {
            return $"Hay {paxCount} pasajeros cargados pero los servicios solo soportan {maxExpected}. " +
                   "Ajusta la capacidad de los servicios o eliminá pasajeros antes de pasar a Operativo.";
        }

        // Chequeo 2: por asignacion individual
        var passengerIds = reserva.Passengers!.Select(p => p.Id).ToList();
        var assignments = await db.PassengerServiceAssignments
            .AsNoTracking()
            .Where(a => passengerIds.Contains(a.PassengerId))
            .Select(a => new { a.ServiceType, a.ServiceId })
            .ToListAsync(ct);

        if (assignments.Count == 0) return null;

        var assignmentCounts = assignments
            .GroupBy(a => new { a.ServiceType, a.ServiceId })
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var hotel in reserva.HotelBookings ?? Enumerable.Empty<HotelBooking>())
        {
            var key = new { ServiceType = AssignmentServiceType.Hotel, ServiceId = hotel.Id };
            if (assignmentCounts.TryGetValue(key, out var count))
            {
                var cap = hotel.GetExpectedPaxCount();
                if (cap > 0 && count > cap)
                {
                    return $"El hotel '{hotel.HotelName ?? "Hotel"}' tiene {count} pasajeros asignados pero su capacidad es {cap}. " +
                           "Ajusta la capacidad o quita asignaciones antes de pasar a Operativo.";
                }
            }
        }

        foreach (var transfer in reserva.TransferBookings ?? Enumerable.Empty<TransferBooking>())
        {
            var key = new { ServiceType = AssignmentServiceType.Transfer, ServiceId = transfer.Id };
            if (assignmentCounts.TryGetValue(key, out var count))
            {
                var cap = transfer.GetExpectedPaxCount();
                if (cap > 0 && count > cap)
                {
                    return $"El transfer ({transfer.VehicleType ?? "vehiculo"}) tiene {count} pasajeros asignados pero su capacidad es {cap}. " +
                           "Ajusta la capacidad o quita asignaciones antes de pasar a Operativo.";
                }
            }
        }

        foreach (var package in reserva.PackageBookings ?? Enumerable.Empty<PackageBooking>())
        {
            var key = new { ServiceType = AssignmentServiceType.Package, ServiceId = package.Id };
            if (assignmentCounts.TryGetValue(key, out var count))
            {
                var cap = package.GetExpectedPaxCount();
                if (cap > 0 && count > cap)
                {
                    return $"El paquete '{package.PackageName ?? "Paquete"}' tiene {count} pasajeros asignados pero su capacidad es {cap}. " +
                           "Ajusta la capacidad o quita asignaciones antes de pasar a Operativo.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Devuelve la capacidad esperada de un servicio especifico (Hotel/Transfer/Package).
    /// Devuelve null si el tipo no declara capacidad (Flight/Generic).
    /// Devuelve 0 si la entidad no fue encontrada (caller debe manejar).
    /// </summary>
    public static async Task<int?> GetServiceCapacityAsync(AppDbContext db, string serviceType, int serviceId, CancellationToken ct = default)
    {
        return serviceType switch
        {
            AssignmentServiceType.Hotel => await db.HotelBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => (int?)(b.Adults + b.Children))
                .FirstOrDefaultAsync(ct),
            AssignmentServiceType.Transfer => await db.TransferBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => (int?)b.Passengers)
                .FirstOrDefaultAsync(ct),
            AssignmentServiceType.Package => await db.PackageBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => (int?)(b.Adults + b.Children))
                .FirstOrDefaultAsync(ct),
            // Flight y Generic no declaran capacidad — devolvemos null para que el caller no bloquee.
            _ => null
        };
    }

    /// <summary>
    /// Bloquea asignar un pasajero NUEVO a un servicio que ya esta lleno.
    /// "Lleno" = count(assignments existentes para ese servicio) >= capacidad declarada.
    /// Si capacidad es 0 o null (no declarada), no bloquea — permite asignar libremente.
    ///
    /// IMPORTANTE: el caller debe garantizar que el pasajero NO esta ya asignado al
    /// servicio (es decir, llamar despues del check de idempotencia). Sino bloquea
    /// re-asignaciones que deberian ser no-op.
    ///
    /// Devuelve mensaje accionable o null si esta permitido.
    /// </summary>
    public static async Task<string?> GetServiceFullBlockReasonAsync(
        AppDbContext db,
        string serviceType,
        int serviceId,
        string serviceLabel,
        CancellationToken ct = default)
    {
        var cap = await GetServiceCapacityAsync(db, serviceType, serviceId, ct);
        if (cap is null or <= 0) return null; // sin capacidad declarada → no bloqueo

        var currentCount = await db.PassengerServiceAssignments.AsNoTracking()
            .CountAsync(a => a.ServiceType == serviceType && a.ServiceId == serviceId, ct);
        if (currentCount < cap.Value) return null;

        return $"El servicio '{serviceLabel}' ya tiene {currentCount} pasajero(s) asignado(s) y su capacidad es {cap.Value}. " +
               "Ampliá la capacidad del servicio o quitá una asignación existente antes de agregar uno nuevo.";
    }

    /// <summary>
    /// Bloquea pase a Operativo si algun servicio no esta en estado "Confirmado" (o
    /// equivalente). Razon: servicios no confirmados NO entran al balance del
    /// proveedor (SupplierService:205) y su confirmacion posterior haria que el
    /// balance pegue un salto retroactivo. Ademas, operativamente no deberian
    /// ejecutarse servicios no confirmados con el proveedor.
    ///
    /// Devuelve mensaje accionable o null si todos los servicios estan confirmados.
    /// </summary>
    public static async Task<string?> GetUnconfirmedServicesBlockReasonAsync(AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        var unconfirmed = new List<string>();

        var hotels = await db.HotelBookings.AsNoTracking()
            .Where(b => b.ReservaId == reservaId && !ConfirmedServiceStatuses.Contains(b.Status))
            .Select(b => new { b.HotelName, b.Status })
            .ToListAsync(ct);
        foreach (var h in hotels)
            unconfirmed.Add($"Hotel '{h.HotelName ?? "sin nombre"}' ({h.Status})");

        var transfers = await db.TransferBookings.AsNoTracking()
            .Where(b => b.ReservaId == reservaId && !ConfirmedServiceStatuses.Contains(b.Status))
            .Select(b => new { b.VehicleType, b.Status })
            .ToListAsync(ct);
        foreach (var t in transfers)
            unconfirmed.Add($"Transfer {t.VehicleType ?? ""} ({t.Status})".Trim());

        var packages = await db.PackageBookings.AsNoTracking()
            .Where(b => b.ReservaId == reservaId && !ConfirmedServiceStatuses.Contains(b.Status))
            .Select(b => new { b.PackageName, b.Status })
            .ToListAsync(ct);
        foreach (var p in packages)
            unconfirmed.Add($"Paquete '{p.PackageName ?? "sin nombre"}' ({p.Status})");

        var flights = await db.FlightSegments.AsNoTracking()
            .Where(f => f.ReservaId == reservaId && !ConfirmedServiceStatuses.Contains(f.Status))
            .Select(f => new { f.AirlineCode, f.FlightNumber, f.Status })
            .ToListAsync(ct);
        foreach (var f in flights)
            unconfirmed.Add($"Vuelo {f.AirlineCode}{f.FlightNumber} ({f.Status})");

        var generics = await db.Servicios.AsNoTracking()
            .Where(s => s.ReservaId == reservaId && !ConfirmedServiceStatuses.Contains(s.Status))
            .Select(s => new { s.Description, s.Status })
            .ToListAsync(ct);
        foreach (var g in generics)
            unconfirmed.Add($"Servicio '{g.Description ?? "sin descripcion"}' ({g.Status})");

        if (unconfirmed.Count == 0) return null;

        var detail = string.Join(", ", unconfirmed.Take(5));
        var more = unconfirmed.Count > 5 ? $" y {unconfirmed.Count - 5} mas" : "";
        return $"Hay servicio(s) sin confirmar con el proveedor: {detail}{more}. " +
               "Confirma todos los servicios antes de pasar a Operativo (los servicios no confirmados no entran al balance del proveedor).";
    }
}
