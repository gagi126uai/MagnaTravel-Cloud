using System.ComponentModel.DataAnnotations;

namespace TravelApi.Models;

public class Agency
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;
    
    [MaxLength(50)]
    public string? TaxId { get; set; }
    
    [MaxLength(200)]
    public string? Email { get; set; }
    
    [MaxLength(50)]
    public string? Phone { get; set; }
    
    [MaxLength(300)]
    public string? Address { get; set; }
    
    public decimal CreditLimit { get; set; }
    public decimal CurrentBalance { get; set; }
    
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ApplicationUser> Users { get; set; } = new List<ApplicationUser>();
}
