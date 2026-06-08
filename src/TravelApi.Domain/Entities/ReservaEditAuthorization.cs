using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-020 F4 (candado): autorizacion para editar una reserva que esta bajo candado
/// (Status ∈ {Confirmed, Traveling, ToSettle, Closed}). Desde Confirmada en adelante la
/// edicion esta bloqueada; cada operacion protegida (editar/borrar/cancelar servicio, datos
/// de la reserva, pasajeros, agregar servicio) exige que exista una autorizacion VIVA.
///
/// <para>Regla de unicidad: a lo sumo UNA autorizacion viva por reserva. Al crear una nueva
/// con otra vigente, la vigente se expira en el acto (<c>ExpiresAt = now</c>) y la nueva la
/// reemplaza. El guard resuelve con un solo lookup por el indice (ReservaId, ExpiresAt).</para>
///
/// <para>Aplica a TODOS los roles, Admin incluido: quien tiene el permiso
/// <c>reservas.authorize_locked_edit</c> se auto-autoriza, pero la fila igual queda
/// registrada (quien pidio, quien autorizo, motivo, cuando).</para>
/// </summary>
public class ReservaEditAuthorization : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    /// <summary>Quien va a editar (el actor que pidio la autorizacion).</summary>
    [MaxLength(200)]
    public string? RequestedByUserId { get; set; }

    [MaxLength(200)]
    public string? RequestedByUserName { get; set; }

    /// <summary>
    /// Quien autorizo (tiene <c>reservas.authorize_locked_edit</c>). Puede coincidir con el
    /// solicitante si este tiene el permiso (auto-autorizacion registrada, Admin incluido).
    /// </summary>
    [MaxLength(200)]
    public string? AuthorizedByUserId { get; set; }

    [MaxLength(200)]
    public string? AuthorizedByUserName { get; set; }

    /// <summary>Motivo obligatorio (min 10 chars — mismo criterio que RevertStatusAsync).</summary>
    [Required, MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Fin de la ventana de autorizacion. Mientras <c>now &lt; ExpiresAt</c> la autorizacion esta
    /// viva. Indexado junto con ReservaId para que el guard pregunte "hay autorizacion viva?" barato.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Estado del file al momento de autorizar (snapshot para auditoria).</summary>
    [MaxLength(50)]
    public string? ReservaStatusSnapshot { get; set; }

    /// <summary>Las operaciones concretas ejecutadas al amparo de esta autorizacion.</summary>
    public ICollection<ReservaEditAuthorizationChange> Changes { get; set; } = new List<ReservaEditAuthorizationChange>();
}

/// <summary>
/// ADR-020 F4: una operacion concreta ejecutada bajo el amparo de una
/// <see cref="ReservaEditAuthorization"/> viva. Responde "que cambio" para la auditoria.
/// </summary>
public class ReservaEditAuthorizationChange
{
    public int Id { get; set; }

    public int AuthorizationId { get; set; }
    public ReservaEditAuthorization? Authorization { get; set; }

    /// <summary>
    /// Tipo de operacion: ServiceEdited | ServiceDeleted | ServiceCancelled | ServiceAdded |
    /// ReservaDataEdited | PassengerAdded | PassengerEdited | PassengerDeleted.
    /// </summary>
    [Required, MaxLength(50)]
    public string Operation { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? EntityType { get; set; }

    public int? EntityId { get; set; }

    /// <summary>Resumen legible del cambio (campo viejo -> nuevo, resumido).</summary>
    [MaxLength(1000)]
    public string? Summary { get; set; }

    [MaxLength(200)]
    public string? PerformedByUserId { get; set; }

    [MaxLength(200)]
    public string? PerformedByUserName { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>ADR-020 F4: nombres canonicos de las operaciones registradas en el candado.</summary>
public static class ReservaEditAuthorizationOperations
{
    public const string ServiceEdited = "ServiceEdited";
    public const string ServiceDeleted = "ServiceDeleted";
    public const string ServiceCancelled = "ServiceCancelled";
    public const string ServiceAdded = "ServiceAdded";
    public const string ReservaDataEdited = "ReservaDataEdited";
    public const string PassengerAdded = "PassengerAdded";
    public const string PassengerEdited = "PassengerEdited";
    public const string PassengerDeleted = "PassengerDeleted";
}
