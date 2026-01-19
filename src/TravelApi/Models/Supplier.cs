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
    public string TaxId { get; set; } = string.Empty; // CUIT/CUIL

    public bool IsActive { get; set; } = true;

    // Financials (what we owe them)
    public decimal CurrentBalance { get; set; } = 0; // Positive = we owe them
}
