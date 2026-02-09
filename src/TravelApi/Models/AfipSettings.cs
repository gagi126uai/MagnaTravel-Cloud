using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

public class AfipSettings
{
    public int Id { get; set; }

    [Required]
    public long Cuit { get; set; }

    public int PuntoDeVenta { get; set; } = 1;

    public bool IsProduction { get; set; } = false;

    // We store the certificate content as Base64 to avoid filesystem issues in Docker/Azure
    // Alternatively, we could store the path if volume mounting is guaranteed.
    // For now, let's store the path string, assuming the user uploads it to a specific folder.
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }
    
    // Token caching fields (to avoid re-authing every request)
    public string? Token { get; set; }
    public string? Sign { get; set; }
    public DateTime? TokenExpiration { get; set; }
}
