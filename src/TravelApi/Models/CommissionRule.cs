using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

/// <summary>
/// Reglas de comisión variable por proveedor y/o tipo de servicio
/// </summary>
public class CommissionRule
{
    public int Id { get; set; }

    /// <summary>
    /// Proveedor específico (null = aplica a todos los proveedores)
    /// </summary>
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>
    /// Tipo de servicio específico (null = aplica a todos los tipos)
    /// Valores: Aereo, Hotel, Traslado, Asistencia, Excursion, Paquete, Otro
    /// </summary>
    [MaxLength(50)]
    public string? ServiceType { get; set; }

    /// <summary>
    /// Porcentaje de comisión (ej: 10.5 = 10.5%)
    /// </summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal CommissionPercent { get; set; }

    /// <summary>
    /// Prioridad de la regla:
    /// 1 = Default (sin proveedor ni servicio específico)
    /// 2 = Solo proveedor O solo servicio
    /// 3 = Proveedor + Servicio (más específica)
    /// </summary>
    public int Priority { get; set; } = 1;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Descripción opcional de la regla
    /// </summary>
    [MaxLength(200)]
    public string? Description { get; set; }
}
