using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

/// <summary>
/// Tarifario - precios de referencia por proveedor y tipo de servicio
/// Los precios se copian al servicio al momento de crearlo (inmutabilidad)
/// </summary>
public class Rate
{
    public int Id { get; set; }
    
    // Proveedor
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    
    // Tipo de servicio
    [Required]
    [MaxLength(50)]
    public string ServiceType { get; set; } = string.Empty; // Aereo, Hotel, Traslado, Paquete
    
    // Producto
    [Required]
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;
    
    [MaxLength(500)]
    public string? Description { get; set; }
    
    // Precios
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetCost { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }
    
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";
    
    // Vigencia
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    
    public bool IsActive { get; set; } = true;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
