using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Reglas compartidas de validacion de capacidad pasajeros vs servicios.
/// Usado por ReservaService (transicion manual) y ReservaLifecycleAutomationService
/// (transicion automatica del job diario), para que la regla viva en un solo lugar.
/// </summary>
public static class ReservaCapacityRules
{
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
}
