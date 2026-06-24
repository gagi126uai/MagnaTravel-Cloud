using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services.Reservations;

/// <summary>
/// B2 (2026-06-24): FUENTE UNICA de "finalizar los servicios de una reserva cuando se cierra" (transicion a
/// Closed/Finalizada). Marca como "Finalizado" (prestado/cumplido) los servicios RESUELTOS de las 6 tablas.
///
/// <para><b>Por que existe (centralizacion)</b>: a Closed se llega por DOS caminos — el cierre MANUAL
/// (<c>ReservaService.UpdateStatusAsync</c> -&gt; ApplyTransitionAsync) y el cierre AUTOMATICO por el job diario
/// (<c>ReservaLifecycleAutomationService.ApplyTransitionsAsync</c>, el camino DOMINANTE: cierra por fin de
/// viaje). Si la finalizacion viviera solo en uno, el otro dejaria los servicios en "Confirmado" para una
/// reserva ya Finalizada (el bug que detecto el reviewer). Este helper lo aplican AMBOS, igual que
/// <see cref="SourceLeadWonHook"/> y <c>SupplierDebtPersister</c> comparten su regla.</para>
///
/// <para><b>CRITICO — "Finalizado" NO es "Cancelado"</b>: un servicio finalizado sigue siendo parte de la
/// venta, asi que NO sale del saldo del cliente ni de la deuda con el operador. El mapeo de estado
/// (<see cref="WorkflowStatusHelper"/>) trata "Finalizado" como Confirmado (activo), no como Cancelado.</para>
///
/// <para><b>Que se finaliza y que NO</b>: SOLO los servicios RESUELTOS
/// (<see cref="ServiceResolutionRules.IsResolved"/>): los activos en la venta confirmada, que son los que se
/// prestaron. Los CANCELADOS quedan cancelados (IsResolved ya los excluye). Los NO resueltos (un "Solicitado"
/// que el operador nunca confirmo) se dejan como estan: marcarlos resueltos retroactivamente inflaria la venta
/// confirmada (ConfirmedSale) de una reserva ya saldada y cerrada. Invariante de plata: como "Finalizado" mapea
/// a Confirmado y solo se aplica a resueltos, el saldo NO cambia (un resuelto sigue resuelto), por eso no hace
/// falta recalcular el balance.</para>
///
/// <para><b>NO hace SaveChanges</b>: corre dentro de la transaccion del caller (el cierre), atomico con el
/// cambio de estado de la reserva. Idempotente: re-aplicarlo sobre una reserva ya finalizada es no-op (un
/// "Finalizado" sigue siendo resuelto, se re-setea al mismo valor).</para>
/// </summary>
internal static class ReservaServiceFinalizer
{
    /// <summary>
    /// Marca como "Finalizado" los servicios RESUELTOS de la reserva (las 6 tablas). NO llama a SaveChanges:
    /// lo hace el caller, en la misma unidad de trabajo del cierre.
    /// </summary>
    public static async Task MarkResolvedServicesFinalizedAsync(
        AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        // Solo se finaliza lo RESUELTO (activo y prestado). Como IsResolved excluye cancelados, no hace falta
        // chequear IsCancelled por separado. Para el aereo, IsResolved exige ticket emitido (poner "Finalizado"
        // no lo re-clasifica porque el ticket persiste).
        var flights = await db.FlightSegments.Where(f => f.ReservaId == reservaId).ToListAsync(ct);
        foreach (var flight in flights)
        {
            if (ServiceResolutionRules.IsResolved(flight))
                flight.Status = WorkflowStatuses.Finalizado;
        }

        var hotels = await db.HotelBookings.Where(h => h.ReservaId == reservaId).ToListAsync(ct);
        foreach (var hotel in hotels)
        {
            if (ServiceResolutionRules.IsResolved(hotel))
                hotel.Status = WorkflowStatuses.Finalizado;
        }

        var transfers = await db.TransferBookings.Where(t => t.ReservaId == reservaId).ToListAsync(ct);
        foreach (var transfer in transfers)
        {
            if (ServiceResolutionRules.IsResolved(transfer))
                transfer.Status = WorkflowStatuses.Finalizado;
        }

        var packages = await db.PackageBookings.Where(p => p.ReservaId == reservaId).ToListAsync(ct);
        foreach (var package in packages)
        {
            if (ServiceResolutionRules.IsResolved(package))
                package.Status = WorkflowStatuses.Finalizado;
        }

        var assistances = await db.AssistanceBookings.Where(a => a.ReservaId == reservaId).ToListAsync(ct);
        foreach (var assistance in assistances)
        {
            if (ServiceResolutionRules.IsResolved(assistance))
                assistance.Status = WorkflowStatuses.Finalizado;
        }

        var genericServices = await db.Servicios.Where(s => s.ReservaId == reservaId).ToListAsync(ct);
        foreach (var service in genericServices)
        {
            if (ServiceResolutionRules.IsResolved(service))
                service.Status = WorkflowStatuses.Finalizado;
        }
    }
}
