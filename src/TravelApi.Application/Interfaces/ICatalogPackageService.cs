using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface ICatalogPackageService
{
    Task<PagedResponse<CatalogPackageListItemDto>> GetPackagesAsync(PackageListQuery query, CancellationToken ct);
    Task<CatalogPackageDetailDto?> GetPackageByIdAsync(int id, CancellationToken ct);
    Task<CatalogPackageDetailDto> CreateAsync(PackageUpsertRequest request, CancellationToken ct);
    Task<CatalogPackageDetailDto?> UpdateAsync(int id, PackageUpsertRequest request, CancellationToken ct);
    Task<CatalogPackageDetailDto> PublishAsync(int id, CancellationToken ct);
    Task<CatalogPackageDetailDto> UnpublishAsync(int id, CancellationToken ct);
    Task<CatalogPackageDetailDto> UploadHeroImageAsync(int id, Stream fileStream, string fileName, string contentType, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType)?> GetHeroImageAsync(int id, CancellationToken ct);
    Task<PublicPackageDetailDto?> GetPublicPackageBySlugAsync(string slug, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType)?> GetPublicHeroImageBySlugAsync(string slug, CancellationToken ct);
    Task CreatePublicLeadAsync(string slug, PublicPackageLeadRequest request, string? referer, CancellationToken ct);
}
