using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class CountryService : ICountryService
{
    private static readonly Regex SlugCleanupRegex = new("[^a-z0-9]+", RegexOptions.Compiled);
    private readonly AppDbContext _db;

    public CountryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<CountryListItemDto>> GetCountriesAsync(string? search, CancellationToken ct)
    {
        var query = _db.Countries
            .AsNoTracking()
            .Include(item => item.Destinations)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            query = query.Where(item =>
                item.Name.ToLower().Contains(normalized) ||
                item.Slug.ToLower().Contains(normalized));
        }

        var countries = await query
            .OrderBy(item => item.Name)
            .ToListAsync(ct);

        return countries.Select(MapCountryListItem).ToList();
    }

    public async Task<CountryDetailDto?> GetCountryByIdAsync(int id, CancellationToken ct)
    {
        var country = await _db.Countries
            .AsNoTracking()
            .Include(item => item.Destinations)
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        return country is null ? null : MapCountryDetail(country);
    }

    public async Task<CountryDetailDto> CreateAsync(CountryUpsertRequest request, CancellationToken ct)
    {
        var country = new Country
        {
            CreatedAt = DateTime.UtcNow
        };

        await ApplyRequestAsync(country, request, ct);

        _db.Countries.Add(country);
        await _db.SaveChangesAsync(ct);

        return MapCountryDetail(country);
    }

    public async Task<CountryDetailDto?> UpdateAsync(int id, CountryUpsertRequest request, CancellationToken ct)
    {
        var country = await _db.Countries
            .Include(item => item.Destinations)
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        if (country is null)
        {
            return null;
        }

        await ApplyRequestAsync(country, request, ct);
        await _db.SaveChangesAsync(ct);
        return MapCountryDetail(country);
    }

    public async Task<PublicCountryEmbedDto?> GetPublicCountryBySlugAsync(string countrySlug, CancellationToken ct)
    {
        var normalizedCountrySlug = NormalizeSlug(countrySlug);
        if (string.IsNullOrWhiteSpace(normalizedCountrySlug))
        {
            return null;
        }

        var country = await _db.Countries
            .AsNoTracking()
            .Include(item => item.Destinations)
                .ThenInclude(destination => destination.Departures)
            .FirstOrDefaultAsync(item => item.Slug == normalizedCountrySlug, ct);

        if (country is null)
        {
            return null;
        }

        var destinations = country.Destinations
            .Where(item => item.IsPublished)
            .Select(item => new
            {
                Destination = item,
                PrimaryDeparture = item.Departures
                    .Where(departure => departure.IsActive && departure.IsPrimary)
                    .OrderBy(departure => departure.StartDate)
                    .FirstOrDefault()
            })
            .Where(item => item.PrimaryDeparture is not null)
            .OrderBy(item => item.Destination.DisplayOrder)
            .ThenBy(item => item.Destination.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new PublicCountryDestinationDto
            {
                PackageSlug = item.Destination.Slug,
                Destination = item.Destination.Name,
                Order = item.Destination.DisplayOrder,
                FromPrice = item.PrimaryDeparture!.SalePrice,
                Currency = item.PrimaryDeparture.Currency
            })
            .ToList();

        if (destinations.Count == 0)
        {
            return null;
        }

        return new PublicCountryEmbedDto
        {
            CountryName = country.Name,
            CountrySlug = country.Slug,
            Destinations = destinations
        };
    }

    private async Task ApplyRequestAsync(Country country, CountryUpsertRequest request, CancellationToken ct)
    {
        country.Name = RequireTrimmed(request.Name, "El nombre del pais es obligatorio.");

        var normalizedName = country.Name.ToLowerInvariant();
        var nameExists = await _db.Countries
            .AsNoTracking()
            .Where(item => item.Id != country.Id)
            .AnyAsync(item => item.Name.ToLower() == normalizedName, ct);

        if (nameExists)
        {
            throw new InvalidOperationException("Ya existe un pais con ese nombre.");
        }

        if (country.Id == 0 || string.IsNullOrWhiteSpace(country.Slug))
        {
            country.Slug = await GenerateUniqueSlugAsync(request.Slug, country.Name, country.Id, ct);
        }

        country.UpdatedAt = DateTime.UtcNow;
    }

    private async Task<string> GenerateUniqueSlugAsync(string? requestedSlug, string fallbackName, int currentId, CancellationToken ct)
    {
        var baseSlug = NormalizeSlug(string.IsNullOrWhiteSpace(requestedSlug) ? fallbackName : requestedSlug);
        if (string.IsNullOrWhiteSpace(baseSlug))
        {
            throw new InvalidOperationException("No se pudo preparar la publicacion web de este pais.");
        }

        var candidate = baseSlug;
        var suffix = 2;
        while (await _db.Countries
                   .AsNoTracking()
                   .AnyAsync(item => item.Slug == candidate && item.Id != currentId, ct))
        {
            candidate = $"{baseSlug}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static CountryListItemDto MapCountryListItem(Country country)
    {
        var totalDestinations = country.Destinations.Count;
        var publishedDestinations = country.Destinations.Count(item => item.IsPublished);

        return new CountryListItemDto
        {
            PublicId = country.PublicId,
            Name = country.Name,
            Slug = country.Slug,
            TotalDestinations = totalDestinations,
            PublishedDestinations = publishedDestinations,
            DraftDestinations = totalDestinations - publishedDestinations,
            CountryPagePath = $"/embed/countries/{country.Slug}",
            CreatedAt = country.CreatedAt,
            UpdatedAt = country.UpdatedAt
        };
    }

    private static CountryDetailDto MapCountryDetail(Country country)
    {
        var totalDestinations = country.Destinations.Count;
        var publishedDestinations = country.Destinations.Count(item => item.IsPublished);

        return new CountryDetailDto
        {
            PublicId = country.PublicId,
            Name = country.Name,
            Slug = country.Slug,
            TotalDestinations = totalDestinations,
            PublishedDestinations = publishedDestinations,
            DraftDestinations = totalDestinations - publishedDestinations,
            CountryPagePath = $"/embed/countries/{country.Slug}",
            CreatedAt = country.CreatedAt,
            UpdatedAt = country.UpdatedAt
        };
    }

    private static string NormalizeSlug(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        normalized = SlugCleanupRegex.Replace(normalized, "-");
        normalized = normalized.Trim('-');
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        return normalized;
    }

    private static string RequireTrimmed(string? value, string errorMessage)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(errorMessage);
        }

        return normalized;
    }
}
