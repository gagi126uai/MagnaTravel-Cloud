namespace TravelApi.Domain.Entities;

/// <summary>
/// B1.15 Fase B' (2026-05-11): estado de un ApprovalRequest.
/// </summary>
public enum ApprovalStatus
{
    /// <summary>Solicitada por el usuario, esperando review.</summary>
    Pending = 0,

    /// <summary>Reviewer aprobo. La accion solicitada puede ejecutarse (hasta ExpiresAt o hasta Consumed).</summary>
    Approved = 1,

    /// <summary>Reviewer rechazo. La accion no puede ejecutarse. Cooldown activo.</summary>
    Rejected = 2,

    /// <summary>El solicitante ejecuto la accion. La aprobacion ya no es valida para nuevos intentos.</summary>
    Consumed = 3,

    /// <summary>ExpiresAt paso sin que el solicitante consumiera. Requiere nuevo request.</summary>
    Expired = 4,
}
