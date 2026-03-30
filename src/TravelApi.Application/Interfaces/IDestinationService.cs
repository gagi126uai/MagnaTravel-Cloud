using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IDestinationService
{
    Task<IReadOnlyList<DestinationListItemDto>> GetDestinationsByCountryIdAsync(int countryId, CancellationToken ct);
    Task<DestinationDetailDto?> GetDestinationByIdAsync(int id, CancellationToken ct);
    Task<DestinationDetailDto> CreateAsync(DestinationUpsertRequest request, CancellationToken ct);
    Task<DestinationDetailDto?> UpdateAsync(int id, DestinationUpsertRequest request, CancellationToken ct);
    Task<DestinationDetailDto> PublishAsync(int id, CancellationToken ct);
    Task<DestinationDetailDto> UnpublishAsync(int id, CancellationToken ct);
    Task<DestinationDetailDto> UploadHeroImageAsync(int id, Stream fileStream, string fileName, string contentType, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType)?> GetHeroImageAsync(int id, CancellationToken ct);
    Task<PublicPackageDetailDto?> GetPublicPackageBySlugAsync(string slug, CancellationToken ct);
    Task<PreviewPackageDetailDto?> GetPreviewPackageBySlugAsync(string slug, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType)?> GetPublicHeroImageBySlugAsync(string slug, CancellationToken ct);
    Task CreatePublicLeadAsync(string slug, PublicPackageLeadRequest request, string? referer, CancellationToken ct);
}
