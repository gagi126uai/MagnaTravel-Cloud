using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class Country : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(120)]
    public string Slug { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<Destination> Destinations { get; set; } = new List<Destination>();
}

public class Destination : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int CountryId { get; set; }
    public Country? Country { get; set; }

    [Required]
    [MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Slug { get; set; } = string.Empty;

    [MaxLength(120)]
    public string? Tagline { get; set; }

    public int DisplayOrder { get; set; }

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

    public ICollection<DestinationDeparture> Departures { get; set; } = new List<DestinationDeparture>();
}

public class DestinationDeparture : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int DestinationId { get; set; }
    public Destination? Destination { get; set; }

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
