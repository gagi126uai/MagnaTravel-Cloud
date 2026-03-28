using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class CatalogPackage : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Slug { get; set; } = string.Empty;

    [MaxLength(120)]
    public string? Tagline { get; set; }

    [MaxLength(120)]
    public string? Destination { get; set; }

    [MaxLength(260)]
    public string? HeroImageFileName { get; set; }

    [MaxLength(260)]
    public string? HeroImageStoredFileName { get; set; }

    [MaxLength(120)]
    public string? HeroImageContentType { get; set; }

    public long? HeroImageFileSize { get; set; }

    [MaxLength(8000)]
    public string? GeneralInfo { get; set; }

    public bool IsPublished { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }

    public ICollection<CatalogPackageDeparture> Departures { get; set; } = new List<CatalogPackageDeparture>();
}

public class CatalogPackageDeparture : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int CatalogPackageId { get; set; }
    public CatalogPackage? CatalogPackage { get; set; }

    public DateTime StartDate { get; set; }
    public int Nights { get; set; }

    [Required]
    [MaxLength(100)]
    public string TransportLabel { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string HotelName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? MealPlan { get; set; }

    [MaxLength(50)]
    public string? RoomBase { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = "USD";

    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }

    public bool IsPrimary { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
