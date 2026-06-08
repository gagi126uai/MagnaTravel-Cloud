namespace TravelApi.Application.DTOs;

/// <summary>
/// ADR-020 F4 (candado): pedido para destrabar la edicion de una reserva confirmada.
/// El motivo es obligatorio (min 10 chars, mismo criterio que la reversion de estado).
/// <c>AuthorizedByUserId</c> solo se usa cuando el actor NO tiene el permiso
/// <c>reservas.authorize_locked_edit</c>: ahi debe elegir un autorizante que si lo tenga.
/// Si el actor tiene el permiso, se auto-autoriza (y queda registrado igual).
/// </summary>
public record CreateEditAuthorizationRequest(
    string Reason,
    string? AuthorizedByUserId);

/// <summary>
/// ADR-020 F4: resultado de crear una autorizacion de edicion bajo candado. Refleja la ventana
/// viva (CreatedAt..ExpiresAt) y quien pidio/autorizo, para que el frontend muestre el contador.
/// </summary>
public class ReservaEditAuthorizationDto
{
    public Guid PublicId { get; set; }
    public string ReservaStatusSnapshot { get; set; } = string.Empty;
    public string? RequestedByUserId { get; set; }
    public string? RequestedByUserName { get; set; }
    public string? AuthorizedByUserId { get; set; }
    public string? AuthorizedByUserName { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
