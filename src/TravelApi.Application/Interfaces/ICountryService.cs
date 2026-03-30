using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface ICountryService
{
    Task<IReadOnlyList<CountryListItemDto>> GetCountriesAsync(string? search, CancellationToken ct);
    Task<CountryDetailDto?> GetCountryByIdAsync(int id, CancellationToken ct);
    Task<CountryDetailDto> CreateAsync(CountryUpsertRequest request, CancellationToken ct);
    Task<CountryDetailDto?> UpdateAsync(int id, CountryUpsertRequest request, CancellationToken ct);
    Task<PublicCountryEmbedDto?> GetPublicCountryBySlugAsync(string countrySlug, CancellationToken ct);
    Task<PreviewCountryEmbedDto?> GetPreviewCountryBySlugAsync(string countrySlug, CancellationToken ct);
}
