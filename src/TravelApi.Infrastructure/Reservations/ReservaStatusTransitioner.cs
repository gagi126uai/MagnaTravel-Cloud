using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// PUNTO ÚNICO de cambio de <see cref="Reserva.Status"/>. Todo lugar que mueve el estado de una reserva pasa por acá,
/// así el rastro auditable (<see cref="ReservaStatusChangeLog"/>) y la limpieza de las marcas de revisión ocurren de
/// forma consistente y no dependen de que cada call-site se acuerde de hacerlas.
///
/// <para><b>Por qué existe</b> (auditoría 2026-07-04): la marca "confirmada con cambios" (ADR-027) solo se limpiaba en
/// UN camino (el OK humano). Los ~15 sitios que escribían <c>Reserva.Status</c> a mano decidían por su cuenta si
/// limpiaban o no — y casi ninguno lo hacía. Resultado: reservas anuladas o revertidas a Presupuesto seguían
/// mostrando el cartel "Se editaron precios..." y quedaban trabadas para pasar a viaje si se reabrían. Este helper
/// aplica la tabla declarativa <see cref="ReservaStateCleanupRules"/> en cada transición, cerrando el hueco de raíz.</para>
///
/// <para><b>Qué hace y qué NO hace</b>: escribe el log (si es un cambio real), setea el estado y limpia las marcas
/// según la tabla. NO valida matrices de transición (los gates de negocio quedan donde están, ANTES de llamar acá) y
/// NO hace <c>SaveChanges</c>: corre dentro de la unidad de trabajo del caller, atómico con el resto de la operación.</para>
/// </summary>
public static class ReservaStatusTransitioner
{
    /// <summary>
    /// Aplica un cambio de estado a <paramref name="reserva"/>: rastro auditable + set de estado + limpieza de marcas.
    /// El caller ya validó que la transición es legal (gates de negocio) y cierra la transacción con su propio
    /// <c>SaveChanges</c>.
    /// </summary>
    /// <param name="db">Contexto EF de la unidad de trabajo del caller (no se persiste acá).</param>
    /// <param name="reserva">La reserva a transicionar (ya cargada y trackeada por <paramref name="db"/>).</param>
    /// <param name="toStatus">Estado destino (constante de <see cref="EstadoReserva"/>).</param>
    /// <param name="direction">"Forward", "Revert" o "Correction" — se guarda tal cual en el log auditable.</param>
    /// <param name="actorUserId">Usuario que dispara el cambio; null si lo dispara el sistema (job/callback).</param>
    /// <param name="actorUserName">Nombre snapshot del actor (o del proceso del sistema).</param>
    /// <param name="reason">Motivo del cambio para el log (opcional).</param>
    /// <param name="ct">Cancelación cooperativa.</param>
    /// <param name="stampChangeLog">
    /// Si se debe escribir la fila de <see cref="ReservaStatusChangeLog"/>. Default true. Se expone para los callers
    /// (ej. el job de lifecycle) que ya deciden por transición si dejan rastro. Aun con true, el log solo se escribe
    /// cuando el estado REALMENTE cambia (un set idempotente al mismo estado no genera log).
    /// </param>
    /// <param name="occurredAt">
    /// Instante a estampar en el log. Default null → <c>DateTime.UtcNow</c>. Los procesos por lotes (motor de
    /// estados, job de lifecycle) pasan el <c>now</c> compartido de la tanda para que todas las filas de esa corrida
    /// queden con el mismo instante.
    /// </param>
    /// <param name="authorizedBySuperiorUserId">
    /// Solo para reversiones autorizadas por un supervisor (revert de no-admin): ID del supervisor que autorizó.
    /// </param>
    /// <param name="authorizedBySuperiorUserName">Nombre snapshot del supervisor autorizante (par del anterior).</param>
    public static async Task ApplyAsync(
        AppDbContext db,
        Reserva reserva,
        string toStatus,
        string direction,
        string? actorUserId,
        string? actorUserName,
        string? reason,
        CancellationToken ct,
        bool stampChangeLog = true,
        System.DateTime? occurredAt = null,
        string? authorizedBySuperiorUserId = null,
        string? authorizedBySuperiorUserName = null)
    {
        var fromStatus = reserva.Status;

        // Un set al MISMO estado no es una transición: no genera log (mismo criterio que el ciclo de vida ADR-020).
        // La limpieza de marcas SÍ corre igual más abajo: es idempotente y así una reserva que ya está terminal
        // pero quedó con una marca colgada (dato legacy) también se sanea.
        var isRealChange = !string.Equals(fromStatus, toStatus, System.StringComparison.OrdinalIgnoreCase);

        if (stampChangeLog && isRealChange)
        {
            db.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
            {
                ReservaId = reserva.Id,
                FromStatus = fromStatus,
                ToStatus = toStatus,
                Direction = direction,
                ByUserId = actorUserId,
                ByUserName = actorUserName,
                AuthorizedBySuperiorUserId = authorizedBySuperiorUserId,
                AuthorizedBySuperiorUserName = authorizedBySuperiorUserName,
                Reason = reason,
                OccurredAt = occurredAt ?? System.DateTime.UtcNow,
            });
        }

        reserva.Status = toStatus;

        await ApplyCleanupAsync(db, reserva, toStatus, ct);
    }

    /// <summary>
    /// Aplica la limpieza de marcas de revisión que corresponde al estado destino (según
    /// <see cref="ReservaStateCleanupRules"/>). Idempotente: si no hay nada marcado ni filas de detalle, es un no-op.
    /// </summary>
    private static async Task ApplyCleanupAsync(AppDbContext db, Reserva reserva, string toStatus, CancellationToken ct)
    {
        var cleanup = ReservaStateCleanupRules.For(toStatus);

        if (cleanup.ClearUnacknowledgedChanges)
        {
            // ADR-027: apagar la marca "confirmada con cambios". En estados terminales/pre-venta ya no hay nada
            // "pendiente de revisar".
            reserva.HasUnacknowledgedChanges = false;
            reserva.ChangesPendingSince = null;
        }

        if (cleanup.ClearLastRegression)
        {
            // Limpiar el motivo de revisión (franja "confirmada con cambios / revisar"). Se limpia al confirmar
            // (entra a Confirmed sin arrastrar un motivo viejo) y al revertir a pre-venta.
            reserva.LastRegressionReason = null;
            reserva.LastRegressionAt = null;
        }

        if (cleanup.ClearPendingChangeRows)
        {
            // Borrar el DETALLE de cambios ("qué precio cambió"). RemoveRange marca las filas Deleted en el
            // ChangeTracker; se borran con el SaveChanges del caller (no hacemos SaveChanges acá). Solo consultamos
            // si la regla lo pide, así los estados que no limpian filas (ej. Confirmed) no pagan una query de más.
            var pendingChanges = await db.ReservaPendingChanges
                .Where(change => change.ReservaId == reserva.Id)
                .ToListAsync(ct);

            if (pendingChanges.Count > 0)
                db.ReservaPendingChanges.RemoveRange(pendingChanges);
        }
    }
}
