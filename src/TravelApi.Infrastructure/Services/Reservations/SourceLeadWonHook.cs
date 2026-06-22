using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services.Reservations;

/// <summary>
/// CRM leads (auditoria ERP 2026-06-13, fix de fondo 2026-06-18): regla UNICA "el lead de origen pasa a
/// Ganado cuando la reserva linkeada llega a una VENTA OPERATIVA VIVA". Vive en un solo lugar (igual que
/// <see cref="ReservaLockGuard"/> / <see cref="DeleteGuards"/>) para que TODOS los caminos que pueden dejar
/// una reserva en estado firme disparen exactamente la misma regla.
///
/// <para><b>Por que un helper compartido y no un metodo privado:</b> antes el disparo vivia solo en la
/// transicion MANUAL <c>ReservaService.UpdateStatusAsync</c> (Budget -&gt; InManagement). Pero una reserva
/// puede entrar a un estado firme (<see cref="EstadoReserva.ActiveCollectionStatuses"/> = InManagement,
/// Confirmed; ADR-036 quito Traveling y ToSettle de esa lista) por caminos que NO pasan por esa transicion
/// manual:</para>
/// <list type="bullet">
///   <item>auto-confirmacion del motor (InManagement -&gt; Confirmed) en <c>ReservaAutoStateService</c>;</item>
///   <item>la reconciliacion nocturna del job, que tambien corre el motor;</item>
///   <item>el revert de una Cancelada de vuelta a En gestion (<c>ReservaService.RevertStatusAsync</c>).</item>
/// </list>
/// <para>ADR-036 (2026-06-21): aunque el pase a Traveling ya NO esta en <c>ActiveCollectionStatuses</c>, el
/// lead-won NO se pierde: para llegar a Traveling la reserva paso antes por InManagement/Confirmed (que SI
/// disparan el hook). La idempotencia conserva la fecha del primer Ganado.</para>
/// <para>Como ninguno de esos llamaba al disparo, habia leads con venta firme que NUNCA se marcaban Ganado
/// -&gt; la metrica de conversion del CRM quedaba subreportada. Centralizar la regla aca cierra el agujero.</para>
///
/// <para><b>Idempotente y seguro:</b> se apoya en <see cref="LeadService.MarkLeadAsWonForSale"/>, que NO
/// reabre un lead Perdido y NO re-sella un lead ya Ganado (conserva la fecha del primer Ganado). Por eso es
/// seguro llamarlo de mas (cada vez que una reserva ya firme avanza a otro estado firme). NO hace
/// SaveChanges: el caller persiste el lead trackeado junto con la transicion que lo disparo (todo o nada).</para>
///
/// <para><b>Por que ActiveCollectionStatuses (SIN Closed) y no la lista de deuda:</b> "venta operativa viva"
/// (lead-won) y "tiene cuenta por cobrar" (AR/cobranza, que SI incluye Closed) son ejes distintos — ver
/// ADR-033 B1. Cerrar/finalizar una reserva NO es un evento de "venta nueva concretada" y no debe marcar el
/// lead como Ganado.</para>
/// </summary>
public static class SourceLeadWonHook
{
    /// <summary>
    /// Si la <paramref name="reserva"/> esta en un estado de venta operativa viva
    /// (<see cref="EstadoReserva.ActiveCollectionStatuses"/>) y nacio de un lead, marca ese lead como Ganado.
    /// No-op si la reserva no esta firme o no tiene <c>SourceLeadId</c>. NO llama a SaveChanges.
    ///
    /// <para>Devuelve <c>true</c> SOLO si efectivamente cambio el estado del lead (de no-Ganado a Ganado).
    /// Asi el caller que corre su propio SaveChanges puede decidir con precision si hay algo que persistir,
    /// sin recurrir a un <c>ChangeTracker.HasChanges()</c> global que podria arrastrar cambios ajenos
    /// pendientes en el contexto (importante en los flujos de plata, donde el contexto se reutiliza).</para>
    /// </summary>
    public static async Task<bool> MarkSourceLeadAsWonIfReservaIsFirmAsync(
        AppDbContext context,
        Reserva reserva,
        CancellationToken ct = default)
    {
        if (reserva.SourceLeadId == null) return false;
        if (!EstadoReserva.ActiveCollectionStatuses.Contains(reserva.Status)) return false;

        // Cargamos la entidad trackeada (no AsNoTracking): le vamos a cambiar el Status y necesitamos que
        // EF lo persista en el SaveChanges del caller.
        var sourceLead = await context.Leads.FirstOrDefaultAsync(l => l.Id == reserva.SourceLeadId.Value, ct);
        if (sourceLead == null) return false;

        // El guard de dominio es idempotente: si el lead ya estaba Ganado o esta Perdido, NO lo toca. Solo
        // reportamos "hubo cambio" cuando realmente pasamos un lead vivo a Ganado.
        bool wasAlreadyResolved =
            sourceLead.Status == LeadStatus.Won || sourceLead.Status == LeadStatus.Lost;
        LeadService.MarkLeadAsWonForSale(sourceLead);
        return !wasAlreadyResolved && sourceLead.Status == LeadStatus.Won;
    }
}
