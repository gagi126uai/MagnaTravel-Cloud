using System.ComponentModel.DataAnnotations;

namespace TravelApi.Models;

public class Supplier
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? ContactName { get; set; }
    
    [MaxLength(100)]
    public string? Email { get; set; }
    
    [MaxLength(50)]
    public string? Phone { get; set; }
    
    [MaxLength(20)]
    public string? TaxId { get; set; } // CUIT

    [MaxLength(50)]
    public string? TaxCondition { get; set; } // IVA_RESP_INSCRIPTO, MONOTRIBUTISTA, IVA_EXENTO

    [MaxLength(200)]
    public string? Address { get; set; }

    public bool IsActive { get; set; } = true;

    // Financials (what we owe them) - calculated, not editable on create
    public decimal CurrentBalance { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class TaxConditions
{
    public const string IvaResponsableInscripto = "IVA_RESP_INSCRIPTO";
    public const string Monotributista = "MONOTRIBUTISTA";
    public const string IvaExento = "IVA_EXENTO";
    public const string ConsumidorFinal = "CONSUMIDOR_FINAL";
}
