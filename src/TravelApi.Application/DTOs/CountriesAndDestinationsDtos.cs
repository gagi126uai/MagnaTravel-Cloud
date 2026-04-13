namespace TravelApi.Application.DTOs;

public record CountryUpsertRequest(
    string Name,
    string? Slug = null);

public class CountryListItemDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public DateTime? PublishedAt { get; set; }
    public int TotalDestinations { get; set; }
    public int PublishedDestinations { get; set; }
    public int DraftDestinations { get; set; }
    public string CountryPagePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CountryDetailDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public DateTime? PublishedAt { get; set; }
    public int TotalDestinations { get; set; }
    public int PublishedDestinations { get; set; }
    public int DraftDestinations { get; set; }
    public string CountryPagePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public record DestinationDepartureUpsertRequest(
    Guid? PublicId,
    DateTime StartDate,
    int Nights,
    string TransportLabel,
    string HotelName,
    string? MealPlan,
    string? RoomBase,
    string Currency,
    decimal SalePrice,
    bool IsPrimary,
    bool IsActive = true);

public record DestinationUpsertRequest(
    Guid CountryPublicId,
    string Name,
    string Title,
    string? Tagline,
    int DisplayOrder,
    string? GeneralInfo,
    IReadOnlyList<DestinationDepartureUpsertRequest> Departures,
    string? Slug = null);

public class DestinationListItemDto
{
    public Guid PublicId { get; set; }
    public Guid CountryPublicId { get; set; }
    public string CountryName { get; set; } = string.Empty;
    public string CountrySlug { get; set; } = string.Empty;
    public bool IsCountryPublished { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Tagline { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsPublished { get; set; }
    public bool HasHeroImage { get; set; }
    public string? HeroImageUrl { get; set; }
    public decimal? FromPrice { get; set; }
    public string? Currency { get; set; }
    public DateTime? NextDepartureDate { get; set; }
    public int DepartureCount { get; set; }
    public int ActiveDepartureCount { get; set; }
    public bool CanPublish { get; set; }
    public IReadOnlyList<string> PublishIssues { get; set; } = Array.Empty<string>();
    public string PublicPagePath { get; set; } = string.Empty;
    public string CountryPagePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public class DestinationDepartureDto
{
    public Guid PublicId { get; set; }
    public DateTime StartDate { get; set; }
    public int Nights { get; set; }
    public string TransportLabel { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string? MealPlan { get; set; }
    public string? RoomBase { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal SalePrice { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsActive { get; set; }
}

public class DestinationDetailDto
{
    public Guid PublicId { get; set; }
    public Guid CountryPublicId { get; set; }
    public string CountryName { get; set; } = string.Empty;
    public string CountrySlug { get; set; } = string.Empty;
    public bool IsCountryPublished { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Tagline { get; set; }
    public int DisplayOrder { get; set; }
    public string? GeneralInfo { get; set; }
    public bool IsPublished { get; set; }
    public bool HasHeroImage { get; set; }
    public string? HeroImageFileName { get; set; }
    public string? HeroImageUrl { get; set; }
    public decimal? FromPrice { get; set; }
    public string? Currency { get; set; }
    public Guid? PrimaryDeparturePublicId { get; set; }
    public bool CanPublish { get; set; }
    public IReadOnlyList<string> PublishIssues { get; set; } = Array.Empty<string>();
    public string PublicPagePath { get; set; } = string.Empty;
    public string CountryPagePath { get; set; } = string.Empty;
    public IReadOnlyList<DestinationDepartureDto> Departures { get; set; } = Array.Empty<DestinationDepartureDto>();
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}
