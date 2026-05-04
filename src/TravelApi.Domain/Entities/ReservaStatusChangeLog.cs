using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Audit trail de cambios de Status de Reserva. Captura especialmente las
/// "reversiones" (Operativo->Reservado, Reservado->Presupuesto, Cerrado->Operativo)
/// que requieren autorizacion cuando el actor no es Admin.
///
/// Para transiciones forward triviales (Presupuesto->Reservado, etc.) tambien
/// puede registrarse pero no es obligatorio — el campo Direction lo distingue.
/// </summary>
public class ReservaStatusChangeLog : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    [Required, MaxLength(50)]
    public string FromStatus { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string ToStatus { get; set; } = string.Empty;

    /// <summary>"Forward" o "Revert" segun direccion del cambio</summary>
    [Required, MaxLength(20)]
    public string Direction { get; set; } = "Forward";

    [MaxLength(200)]
    public string? ByUserId { get; set; }

    [MaxLength(200)]
    public string? ByUserName { get; set; }

    /// <summary>Para reversiones por no-admin: ID del supervisor que autorizo</summary>
    [MaxLength(200)]
    public string? AuthorizedBySuperiorUserId { get; set; }

    [MaxLength(200)]
    public string? AuthorizedBySuperiorUserName { get; set; }

    [MaxLength(1000)]
    public string? Reason { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
