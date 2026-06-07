namespace TravelApi.Application.Interfaces;

/// <summary>
/// Identidad del usuario que pide las alertas, resuelta en el controller a partir
/// de los claims. Existe para que el gating de buckets sea SERVER-SIDE (Fuga 2,
/// ADR-017 §2.7, F1b) y para que la fase F3 pueda sumar buckets por-vendedor
/// (filtrando por <see cref="UserId"/>) sin volver a cambiar la firma.
/// </summary>
/// <param name="UserId">Id del usuario (claim NameIdentifier). Puede ser null si el token no lo trae.</param>
/// <param name="IsAdmin">True si el caller tiene el rol "Admin".</param>
/// <param name="CanSeeCost">
/// ADR-017 F1.4 (§2.8/D8b): true si el caller puede ver costos (rol Admin o permiso
/// <c>cobranzas.see_cost</c>). Gatea el bucket <c>CostsToConfirm</c> server-side. Default false
/// (fail-closed): los callers/tests que no lo seteen quedan sin ver el bucket de costos.
/// </param>
public sealed record AlertCallerContext(string? UserId, bool IsAdmin, bool CanSeeCost = false);

/// <summary>
/// ADR-019 D4: resultado del intento de descarte ("Listo") de un aviso de proximo inicio.
/// El controller lo traduce a HTTP: <see cref="FeatureDisabled"/> y <see cref="ReservaNotFound"/>
/// ⇒ 404; <see cref="Dismissed"/> y <see cref="NoUpcomingStart"/> ⇒ 204 (el no-op tambien es
/// exito: no hay nada que descartar y repetir el POST da el mismo estado final).
/// </summary>
public enum UpcomingStartDismissOutcome
{
    /// <summary>Flag <c>EnableServiceDeadlineAlerts</c> apagado: la feature "no existe".</summary>
    FeatureDisabled,

    /// <summary>No hay reserva con ese id. OJO: a este 404 solo llegan Admin y portadores de
    /// <c>reservas.view_all</c> — un vendedor comun recibe 403 ANTES, en el filtro de ownership
    /// (que devuelve false tanto para inexistente como para ajena, sin filtrar existencia).</summary>
    ReservaNotFound,

    /// <summary>La reserva no tiene servicios elegibles (sin primer inicio): 204 no-op, no se escribe nada.</summary>
    NoUpcomingStart,

    /// <summary>Descarte registrado (insert o upsert de la fila existente).</summary>
    Dismissed
}

public interface IAlertService
{
    Task<object> GetAlertsAsync(AlertCallerContext caller, CancellationToken cancellationToken);

    /// <summary>
    /// ADR-019 D4: registra el "Listo" GLOBAL de un aviso de proximo inicio. El server recalcula el
    /// primer inicio con el MISMO helper que usa el bucket (no confia en el cliente) y lo ancla en
    /// la fila de descarte; si el primer inicio cambia despues, el aviso reaparece solo (D3).
    /// Idempotente: repetir el POST pisa la misma fila (UNIQUE por reserva en Postgres).
    /// </summary>
    /// <param name="reservaPublicIdOrLegacyId">PublicId (Guid) o id legacy (int) de la reserva, como en el resto de los endpoints por-reserva.</param>
    /// <param name="dismissedByUserId">Quien apreto "Listo" (auditoria). El filtro de ownership ya garantizo que no es null.</param>
    Task<UpcomingStartDismissOutcome> DismissUpcomingStartAsync(
        string reservaPublicIdOrLegacyId, string dismissedByUserId, CancellationToken cancellationToken);
}
