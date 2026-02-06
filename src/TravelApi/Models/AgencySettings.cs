using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

/// <summary>
/// Configuración global de la agencia
/// </summary>
public class AgencySettings
{
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string AgencyName { get; set; } = "Mi Agencia de Viajes";

    [MaxLength(20)]
    public string? TaxId { get; set; } // CUIT

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? Phone { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    /// <summary>
    /// % de comisión por defecto para nuevos servicios (ej: 10 = 10%)
    /// </summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal DefaultCommissionPercent { get; set; } = 10;

    /// <summary>
    /// Moneda principal de la agencia
    /// </summary>
    [MaxLength(3)]
    public string Currency { get; set; } = "ARS";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
