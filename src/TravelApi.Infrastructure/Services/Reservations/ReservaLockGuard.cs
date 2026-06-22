using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services.Reservations;

/// <summary>
/// ADR-020 F4 (candado): regla unica de "puedo editar esta reserva CON autorizacion?".
///
/// <para>En el estado CONFIRMADA (Confirmed) la reserva queda bajo candado: cada write-path
/// (servicios, pasajeros, datos, adjuntos) exige que exista una <see cref="ReservaEditAuthorization"/>
/// VIVA. En las etapas comerciales tempranas (Quotation, Budget, InManagement) la edicion es libre.</para>
///
/// <para><b>ADR-036 (2026-06-21, prepago puro):</b> se quitaron <c>Traveling</c> y <c>ToSettle</c> de este
/// candado. NO porque pasen a ser editables — al contrario: Traveling es SOLO LECTURA TOTAL (Decision 2) y
/// Closed tambien. El bloqueo REAL de esos estados NO vive aca sino en la POLITICA DE CAPACIDADES
/// (<see cref="TravelApi.Domain.Reservations.ReservaCapabilityPolicy"/>.CanEditServices/CanEditPassengers/
/// CanEditReservaData), que NO los incluye en sus estados editables. Esa politica es la PRIMERA COMPUERTA de
/// cada write-path (ReservaCapacityRules.Ensure*EditableByStateAsync, BookingService.GuardServicesEditable*):
/// rechaza Traveling/Closed de raiz, ANTES de este candado. Este candado solo gobierna el unico estado donde
/// editar-con-autorizacion sigue siendo valido: Confirmed.</para>
///
/// <para>Igual que <c>DeleteGuards</c>, vive en un solo lugar para que ReservaService,
/// BookingService y los endpoints que tocan la reserva apliquen exactamente la misma regla.
/// No cubre pagos, facturas ni vouchers: esos tienen sus propios guards.</para>
/// </summary>
public static class ReservaLockGuard
{
    /// <summary>
    /// Estados bajo candado de AUTORIZACION (editar es valido pero requiere autorizacion viva). ADR-036:
    /// queda SOLO Confirmed. Traveling y Closed ya no estan aca porque son solo-lectura dura: su bloqueo lo
    /// impone la politica de capacidades antes de llegar a este candado (ver doc de la clase). ToSettle murio.
    /// </summary>
    private static readonly HashSet<string> LockedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        EstadoReserva.Confirmed,
    };

    /// <summary>Mensaje unico para el usuario cuando la reserva esta con candado y no hay autorizacion.</summary>
    public const string LockedMessage =
        "La reserva esta confirmada (con candado). Pedi autorizacion para editarla antes de modificarla.";

    /// <summary>True si el estado dado esta bajo candado (Confirmada en adelante).</summary>
    public static bool IsLockedStatus(string? status)
        => status != null && LockedStatuses.Contains(status);

    /// <summary>
    /// Verifica que se puede editar la reserva indicada y, si esta bajo candado con autorizacion
    /// viva, registra la operacion concreta (que cambio / quien) de forma ADITIVA.
    ///
    /// <list type="bullet">
    /// <item>Reserva inexistente o en etapa libre (Quotation/Budget/InManagement): no-op, devuelve null.</item>
    /// <item>Bajo candado SIN autorizacion viva: lanza <see cref="InvalidOperationException"/>
    /// (los controllers de escritura lo mapean a 409 Conflict, mismo patron que DeleteGuards).</item>
    /// <item>Bajo candado CON autorizacion viva: agrega una fila
    /// <see cref="ReservaEditAuthorizationChange"/> al contexto (la persiste el SaveChanges del
    /// caller) y devuelve la autorizacion usada.</item>
    /// </list>
    ///
    /// El lookup de "hay autorizacion viva?" usa el indice (ReservaId, ExpiresAt): a lo sumo hay
    /// UNA viva por reserva (la creacion de una nueva expira la anterior), asi que se toma la de
    /// mayor ExpiresAt. La autorizacion es por reserva, NO por usuario: cualquier write-path de esa
    /// reserva pasa mientras la ventana este abierta, y se registra quien hizo cada cambio.
    /// </summary>
    public static async Task<ReservaEditAuthorization?> EnsureCanEditAsync(
        AppDbContext context,
        int reservaId,
        string operation,
        string? actorUserId,
        string? actorUserName,
        string? entityType = null,
        int? entityId = null,
        string? summary = null,
        CancellationToken ct = default)
    {
        var status = await context.Reservas
            .Where(r => r.Id == reservaId)
            .Select(r => r.Status)
            .FirstOrDefaultAsync(ct);

        // Reserva inexistente: dejamos que el flujo propio devuelva su 404, no inventamos candado.
        if (status == null) return null;
        if (!IsLockedStatus(status)) return null;

        var now = DateTime.UtcNow;
        var liveAuthorization = await context.ReservaEditAuthorizations
            .Where(a => a.ReservaId == reservaId && a.ExpiresAt > now)
            .OrderByDescending(a => a.ExpiresAt)
            .FirstOrDefaultAsync(ct);

        if (liveAuthorization == null)
            throw new InvalidOperationException(LockedMessage);

        // Rastro auditable: que operacion concreta se ejecuto al amparo de esta autorizacion.
        // No hacemos SaveChanges: se persiste junto con la mutacion del caller (misma transaccion logica).
        context.ReservaEditAuthorizationChanges.Add(new ReservaEditAuthorizationChange
        {
            AuthorizationId = liveAuthorization.Id,
            Operation = operation,
            EntityType = entityType,
            EntityId = entityId,
            Summary = summary,
            PerformedByUserId = actorUserId,
            PerformedByUserName = actorUserName,
            OccurredAt = now,
        });

        return liveAuthorization;
    }
}
