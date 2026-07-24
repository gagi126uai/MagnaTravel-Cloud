using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services.Reservations;

/// <summary>
/// (2026-06-25): FUENTE compartida de "cancelar TODOS los servicios vivos de una reserva". Marca cada servicio
/// no-cancelado de las 6 tablas como Cancelado, de modo que deja de contar para el saldo del cliente y para la
/// deuda con el operador.
///
/// <para><b>Por que existe</b>: la anulacion total formal (con Nota de Credito) ya tenia este barrido dentro de
/// <c>BookingCancellationService.CancelAllReservaServicesAsync</c> (privado). El caso (3) del flujo unificado de
/// "Anular reserva" — anular en firme SIN factura pero CON cobros, convirtiendo la plata en saldo a favor — lo
/// necesita igual: si la reserva queda Cancelled pero con servicios resueltos, su venta confirmada seguiria
/// exigible y el saldo no daria 0 tras sacar la plata pagada. Este helper expone la MISMA regla para reusarla
/// sin duplicar criterios de mapeo de estado.</para>
///
/// <para><b>Mapeo de estado por tipo (igual que la anulacion total formal)</b>: el aereo se cancela con el
/// codigo IATA "UN" (el unico que <see cref="WorkflowStatusHelper"/> mapea a cancelado para vuelos; poner el
/// literal "Cancelado" NO lo sacaria del saldo). El resto va a <see cref="WorkflowStatuses.Cancelado"/>.</para>
///
/// <para><b>NO hace SaveChanges</b>: corre dentro de la transaccion del caller, atomico con el cambio de estado
/// de la reserva. Idempotente: re-aplicarlo sobre servicios ya cancelados es no-op (los saltea por IsCancelled).</para>
/// </summary>
internal static class ReservaServiceCanceller
{
    /// <summary>
    /// Cancela todos los servicios vivos (no-cancelados) de la reserva en las 6 tablas, estampando el actor y la
    /// fecha. NO llama a SaveChanges: lo hace el caller en la misma unidad de trabajo.
    ///
    /// <para><b>ADR-050 (2026-07-24, "Volver atrás")</b>: <paramref name="annulAt"/> es el UNICO instante que
    /// captura el acto de anular (lo calcula el caller UNA vez, no cada fila con su propio
    /// <c>DateTime.UtcNow</c>). Así queda igual, al microsegundo, a <c>Reserva.AnnulledAt</c> — el undo
    /// necesita comparar <c>servicio.CancelledAt &gt;= reserva.AnnulledAt</c> para saber "¿este servicio se
    /// canceló EN ESTE acto o ya estaba cancelado de antes?", y una comparación entre dos <c>UtcNow</c>
    /// tomados por separado (aunque sea en la misma llamada) podría dar el orden equivocado. También se
    /// estampa <c>StatusBeforeCancellation</c> con el <c>Status</c> viejo ANTES de pisarlo, para que el undo
    /// pueda restaurar el estado exacto en vez de adivinar uno genérico.</para>
    /// </summary>
    public static async Task CancelAllLiveServicesAsync(
        AppDbContext db, int reservaId, DateTime annulAt, string? userId, string? userName, CancellationToken ct = default)
    {
        var flights = await db.FlightSegments.Where(f => f.ReservaId == reservaId).ToListAsync(ct);
        foreach (var flight in flights)
        {
            if (ServiceResolutionRules.IsCancelled(flight)) continue;
            // "UN" = cancelado para vuelos (MapFlightStatus). El literal "Cancelado" NO lo sacaria del saldo.
            flight.StatusBeforeCancellation = flight.Status;
            flight.Status = "UN";
            flight.CancelledAt = annulAt;
            flight.CancelledByUserId = userId;
            flight.CancelledByUserName = userName;
        }

        var hotels = await db.HotelBookings.Where(h => h.ReservaId == reservaId).ToListAsync(ct);
        foreach (var hotel in hotels)
        {
            if (ServiceResolutionRules.IsCancelled(hotel)) continue;
            hotel.StatusBeforeCancellation = hotel.Status;
            hotel.Status = WorkflowStatuses.Cancelado;
            hotel.CancelledAt = annulAt;
            hotel.CancelledByUserId = userId;
            hotel.CancelledByUserName = userName;
        }

        var transfers = await db.TransferBookings.Where(t => t.ReservaId == reservaId).ToListAsync(ct);
        foreach (var transfer in transfers)
        {
            if (ServiceResolutionRules.IsCancelled(transfer)) continue;
            transfer.StatusBeforeCancellation = transfer.Status;
            transfer.Status = WorkflowStatuses.Cancelado;
            transfer.CancelledAt = annulAt;
            transfer.CancelledByUserId = userId;
            transfer.CancelledByUserName = userName;
        }

        var packages = await db.PackageBookings.Where(p => p.ReservaId == reservaId).ToListAsync(ct);
        foreach (var package in packages)
        {
            if (ServiceResolutionRules.IsCancelled(package)) continue;
            package.StatusBeforeCancellation = package.Status;
            package.Status = WorkflowStatuses.Cancelado;
            package.CancelledAt = annulAt;
            package.CancelledByUserId = userId;
            package.CancelledByUserName = userName;
        }

        var assistances = await db.AssistanceBookings.Where(a => a.ReservaId == reservaId).ToListAsync(ct);
        foreach (var assistance in assistances)
        {
            if (ServiceResolutionRules.IsCancelled(assistance)) continue;
            assistance.StatusBeforeCancellation = assistance.Status;
            assistance.Status = WorkflowStatuses.Cancelado;
            assistance.CancelledAt = annulAt;
            assistance.CancelledByUserId = userId;
            assistance.CancelledByUserName = userName;
        }

        var genericServices = await db.Servicios.Where(s => s.ReservaId == reservaId).ToListAsync(ct);
        foreach (var service in genericServices)
        {
            if (ServiceResolutionRules.IsCancelled(service)) continue;
            service.StatusBeforeCancellation = service.Status;
            service.Status = WorkflowStatuses.Cancelado;
            service.CancelledAt = annulAt;
            service.CancelledByUserId = userId;
            service.CancelledByUserName = userName;
        }
    }
}
