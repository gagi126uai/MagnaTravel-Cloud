using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class AfipSettings
{
    public int Id { get; set; }

    [Required]
    public long Cuit { get; set; }

    public int PuntoDeVenta { get; set; } = 1;

    public bool IsProduction { get; set; } = false;

    // We store the certificate content as Base64 to avoid filesystem issues in Docker/Azure
    // Alternatively, we could store the path if volume mounting is guaranteed.
    public string? CertificatePath { get; set; }
    
    public byte[]? CertificateData { get; set; } // Stored in DB for docker persistence

    public string? CertificatePassword { get; set; }
    
    // Token caching fields (to avoid re-authing every request)
    public string? Token { get; set; }
    public string? Sign { get; set; }
    public DateTime? TokenExpiration { get; set; }

    // Padron token caching fields
    public string? PadronToken { get; set; }
    public string? PadronSign { get; set; }
    public DateTime? PadronTokenExpiration { get; set; }

    [MaxLength(50)]
    public string TaxCondition { get; set; } = "Responsable Inscripto"; // Monotributo, Exento
}
