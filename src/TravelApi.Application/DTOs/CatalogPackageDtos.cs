namespace TravelApi.Application.DTOs;

public class PackageListQuery : PagedQuery
{
    public string Status { get; set; } = "all";
    public string? Country { get; set; }

    public PackageListQuery()
    {
        SortBy = "updatedAt";
        SortDir = "desc";
    }
}

public record PackageDepartureUpsertRequest(
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

public record PackageUpsertRequest(
    string Title,
    string Slug,
    string? Tagline,
    string? Destination,
    string? CountryName,
    string? CountrySlug,
    int DestinationOrder,
    string? GeneralInfo,
    IReadOnlyList<PackageDepartureUpsertRequest> Departures);

public class CatalogPackageListItemDto
{
    public Guid PublicId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Tagline { get; set; }
    public string? Destination { get; set; }
    public string? CountryName { get; set; }
    public string? CountrySlug { get; set; }
    public int DestinationOrder { get; set; }
    public bool IsPublished { get; set; }
    public bool HasHeroImage { get; set; }
    public string? HeroImageUrl { get; set; }
    public decimal? FromPrice { get; set; }
    public string? Currency { get; set; }
    public int DepartureCount { get; set; }
    public int ActiveDepartureCount { get; set; }
    public bool CanPublish { get; set; }
    public IReadOnlyList<string> PublishIssues { get; set; } = Array.Empty<string>();
    public string PublicPagePath { get; set; } = string.Empty;
    public string? CountryPagePath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public class CatalogPackageDepartureDto
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

public class CatalogPackageDetailDto
{
    public Guid PublicId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Tagline { get; set; }
    public string? Destination { get; set; }
    public string? CountryName { get; set; }
    public string? CountrySlug { get; set; }
    public int DestinationOrder { get; set; }
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
    public string? CountryPagePath { get; set; }
    public IReadOnlyList<CatalogPackageDepartureDto> Departures { get; set; } = Array.Empty<CatalogPackageDepartureDto>();
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public class PublicPackageDepartureDto
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
}

public class PublicPackageDetailDto
{
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Tagline { get; set; }
    public string? Destination { get; set; }
    public string? CountryName { get; set; }
    public string? CountrySlug { get; set; }
    public int DestinationOrder { get; set; }
    public string? GeneralInfo { get; set; }
    public string? HeroImageUrl { get; set; }
    public decimal FromPrice { get; set; }
    public string Currency { get; set; } = "USD";
    public PublicPackageDepartureDto PrimaryDeparture { get; set; } = new();
    public IReadOnlyList<PublicPackageDepartureDto> Departures { get; set; } = Array.Empty<PublicPackageDepartureDto>();
}

public class PublicCountryDestinationDto
{
    public string PackageSlug { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public int Order { get; set; }
    public decimal FromPrice { get; set; }
    public string Currency { get; set; } = "USD";
}

public class PublicCountryEmbedDto
{
    public string CountryName { get; set; } = string.Empty;
    public string CountrySlug { get; set; } = string.Empty;
    public string SelectedPackageSlug { get; set; } = string.Empty;
    public IReadOnlyList<PublicCountryDestinationDto> Destinations { get; set; } = Array.Empty<PublicCountryDestinationDto>();
    public IReadOnlyList<PublicPackageDetailDto> Packages { get; set; } = Array.Empty<PublicPackageDetailDto>();
}

public record PublicPackageLeadRequest(
    string FullName,
    string Phone,
    string? Email,
    string? Message,
    Guid? DeparturePublicId,
    string? Website);
