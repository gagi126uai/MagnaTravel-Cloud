using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class CatalogPackageService : ICatalogPackageService
{
    private const long MaxHeroImageSizeBytes = 10 * 1024 * 1024;

    private static readonly Regex SlugCleanupRegex = new("[^a-z0-9]+", RegexOptions.Compiled);

    private static readonly Dictionary<string, string[]> AllowedHeroImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = new[] { "image/png" },
        [".jpg"] = new[] { "image/jpeg" },
        [".jpeg"] = new[] { "image/jpeg" },
        [".webp"] = new[] { "image/webp" }
    };

    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<CatalogPackageService> _logger;

    public CatalogPackageService(
        AppDbContext db,
        IWebHostEnvironment environment,
        ILogger<CatalogPackageService> logger)
    {
        _db = db;
        _environment = environment;
        _logger = logger;
    }

    public async Task<PagedResponse<CatalogPackageListItemDto>> GetPackagesAsync(PackageListQuery query, CancellationToken ct)
    {
        var packagesQuery = _db.CatalogPackages
            .AsNoTracking()
            .Include(package => package.Departures)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            packagesQuery = packagesQuery.Where(package =>
                package.Title.ToLower().Contains(normalized) ||
                package.Slug.ToLower().Contains(normalized) ||
                (package.Tagline != null && package.Tagline.ToLower().Contains(normalized)) ||
                (package.Destination != null && package.Destination.ToLower().Contains(normalized)) ||
                (package.CountryName != null && package.CountryName.ToLower().Contains(normalized)) ||
                (package.CountrySlug != null && package.CountrySlug.ToLower().Contains(normalized)));
        }

        if (!string.IsNullOrWhiteSpace(query.Country))
        {
            var normalizedCountry = query.Country.Trim().ToLowerInvariant();
            packagesQuery = packagesQuery.Where(package =>
                (package.CountryName != null && package.CountryName.ToLower().Contains(normalizedCountry)) ||
                (package.CountrySlug != null && package.CountrySlug.ToLower().Contains(normalizedCountry)));
        }

        var status = (query.Status ?? "all").Trim().ToLowerInvariant();
        packagesQuery = status switch
        {
            "published" => packagesQuery.Where(package => package.IsPublished),
            "draft" => packagesQuery.Where(package => !package.IsPublished),
            _ => packagesQuery
        };

        packagesQuery = ApplyOrdering(packagesQuery, query);

        var totalCount = await packagesQuery.CountAsync(ct);
        var safePage = query.GetNormalizedPage();
        var safePageSize = query.GetNormalizedPageSize();

        var packages = await packagesQuery
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        var items = new List<CatalogPackageListItemDto>(packages.Count);
        foreach (var package in packages)
        {
            items.Add(await MapAdminListItemAsync(package, ct));
        }

        return PagedResponse<CatalogPackageListItemDto>.Create(items, safePage, safePageSize, totalCount);
    }

    public async Task<CatalogPackageDetailDto?> GetPackageByIdAsync(int id, CancellationToken ct)
    {
        var package = await _db.CatalogPackages
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        return package is null ? null : await MapAdminDetailAsync(package, ct);
    }

    public async Task<CatalogPackageDetailDto> CreateAsync(PackageUpsertRequest request, CancellationToken ct)
    {
        var package = new CatalogPackage
        {
            CreatedAt = DateTime.UtcNow
        };

        await ApplyRequestAsync(package, request, ct);

        _db.CatalogPackages.Add(package);
        await _db.SaveChangesAsync(ct);

        return await MapAdminDetailAsync(package, ct);
    }

    public async Task<CatalogPackageDetailDto?> UpdateAsync(int id, PackageUpsertRequest request, CancellationToken ct)
    {
        var package = await _db.CatalogPackages
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        if (package is null)
        {
            return null;
        }

        await ApplyRequestAsync(package, request, ct);

        if (package.IsPublished)
        {
            var issues = await GetPublishIssuesAsync(package, ct);
            if (issues.Count > 0)
            {
                package.IsPublished = false;
                package.PublishedAt = null;
            }
        }

        await _db.SaveChangesAsync(ct);
        return await MapAdminDetailAsync(package, ct);
    }

    public async Task<CatalogPackageDetailDto> PublishAsync(int id, CancellationToken ct)
    {
        var package = await _db.CatalogPackages
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.Id == id, ct)
            ?? throw new KeyNotFoundException("Paquete no encontrado.");

        var issues = await GetPublishIssuesAsync(package, ct);
        if (issues.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", issues));
        }

        package.IsPublished = true;
        package.PublishedAt = DateTime.UtcNow;
        package.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return await MapAdminDetailAsync(package, ct);
    }

    public async Task<CatalogPackageDetailDto> UnpublishAsync(int id, CancellationToken ct)
    {
        var package = await _db.CatalogPackages
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.Id == id, ct)
            ?? throw new KeyNotFoundException("Paquete no encontrado.");

        package.IsPublished = false;
        package.PublishedAt = null;
        package.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return await MapAdminDetailAsync(package, ct);
    }

    public async Task<CatalogPackageDetailDto> UploadHeroImageAsync(
        int id,
        Stream fileStream,
        string fileName,
        string contentType,
        CancellationToken ct)
    {
        var package = await _db.CatalogPackages
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.Id == id, ct)
            ?? throw new KeyNotFoundException("Paquete no encontrado.");

        var safeFileName = SanitizeOriginalFileName(fileName);
        var extension = Path.GetExtension(safeFileName);
        if (!AllowedHeroImageTypes.ContainsKey(extension))
        {
            throw new InvalidOperationException("El formato de imagen no está permitido.");
        }

        await using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
        {
            throw new InvalidOperationException("La imagen está vacía.");
        }

        if (buffer.Length > MaxHeroImageSizeBytes)
        {
            throw new InvalidOperationException("La imagen supera el máximo permitido de 10 MB.");
        }

        var normalizedContentType = (contentType ?? string.Empty).Trim();
        if (AllowedHeroImageTypes.TryGetValue(extension, out var allowedContentTypes) &&
            allowedContentTypes.Length > 0 &&
            !allowedContentTypes.Contains(normalizedContentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("La imagen no coincide con el tipo declarado.");
        }

        if (!MatchesHeroImageSignature(extension, buffer.ToArray()))
        {
            throw new InvalidOperationException("La firma de la imagen no coincide con el tipo permitido.");
        }

        var storageFolder = Path.Combine(_environment.ContentRootPath, "Uploads", "Packages", DateTime.UtcNow.Year.ToString());
        Directory.CreateDirectory(storageFolder);

        var storedFileName = $"{Guid.NewGuid()}{extension}";
        var filePath = Path.Combine(storageFolder, storedFileName);

        buffer.Position = 0;
        await using (var output = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await buffer.CopyToAsync(output, ct);
        }

        DeleteStoredHeroImage(package);

        package.HeroImageFileName = safeFileName;
        package.HeroImageStoredFileName = Path.Combine(DateTime.UtcNow.Year.ToString(), storedFileName);
        package.HeroImageContentType = normalizedContentType;
        package.HeroImageFileSize = buffer.Length;
        package.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return await MapAdminDetailAsync(package, ct);
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetHeroImageAsync(int id, CancellationToken ct)
    {
        var package = await _db.CatalogPackages
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        if (package is null)
        {
            throw new KeyNotFoundException("Paquete no encontrado.");
        }

        return await ReadHeroImageAsync(package, ct);
    }

    public async Task<PublicPackageDetailDto?> GetPublicPackageBySlugAsync(string slug, CancellationToken ct)
    {
        var normalizedSlug = NormalizeSlug(slug);
        if (string.IsNullOrWhiteSpace(normalizedSlug))
        {
            return null;
        }

        var package = await _db.CatalogPackages
            .AsNoTracking()
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.IsPublished && item.Slug == normalizedSlug, ct);

        if (package is null)
        {
            return null;
        }

        return MapPublicDetail(package);
    }

    public async Task<PublicCountryEmbedDto?> GetPublicCountryBySlugAsync(string countrySlug, CancellationToken ct)
    {
        var normalizedCountrySlug = NormalizeSlug(countrySlug);
        if (string.IsNullOrWhiteSpace(normalizedCountrySlug))
        {
            return null;
        }

        var packages = await _db.CatalogPackages
            .AsNoTracking()
            .Include(item => item.Departures)
            .Where(item => item.IsPublished && item.CountrySlug == normalizedCountrySlug)
            .ToListAsync(ct);

        var packageDetails = packages
            .Select(MapPublicDetail)
            .Where(item => item is not null)
            .Cast<PublicPackageDetailDto>()
            .OrderBy(item => item.DestinationOrder)
            .ThenBy(item => item.Destination ?? item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (packageDetails.Count == 0)
        {
            return null;
        }

        var countryName = packageDetails
            .Select(item => item.CountryName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? packages
                .Select(item => item.CountryName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? normalizedCountrySlug;

        return new PublicCountryEmbedDto
        {
            CountryName = countryName,
            CountrySlug = normalizedCountrySlug,
            Destinations = packageDetails
                .Select(item => new PublicCountryDestinationDto
                {
                    PackageSlug = item.Slug,
                    Destination = item.Destination ?? item.Title,
                    Order = item.DestinationOrder,
                    FromPrice = item.FromPrice,
                    Currency = item.Currency
                })
                .ToList()
        };
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetPublicHeroImageBySlugAsync(string slug, CancellationToken ct)
    {
        var normalizedSlug = NormalizeSlug(slug);
        if (string.IsNullOrWhiteSpace(normalizedSlug))
        {
            return null;
        }

        var package = await _db.CatalogPackages
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.IsPublished && item.Slug == normalizedSlug, ct);

        return package is null ? null : await ReadHeroImageAsync(package, ct);
    }

    public async Task CreatePublicLeadAsync(string slug, PublicPackageLeadRequest request, string? referer, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            return;
        }

        var normalizedSlug = NormalizeSlug(slug);
        if (string.IsNullOrWhiteSpace(normalizedSlug))
        {
            throw new KeyNotFoundException("Paquete no encontrado.");
        }

        var fullName = RequireTrimmed(request.FullName, "El nombre es obligatorio.");
        var phone = RequireTrimmed(request.Phone, "El teléfono es obligatorio.");

        var package = await _db.CatalogPackages
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.IsPublished && item.Slug == normalizedSlug, ct)
            ?? throw new KeyNotFoundException("Paquete no encontrado.");

        var activeDepartures = package.Departures
            .Where(departure => departure.IsActive)
            .ToList();

        var selectedDeparture = request.DeparturePublicId.HasValue
            ? activeDepartures.FirstOrDefault(departure => departure.PublicId == request.DeparturePublicId.Value)
            : activeDepartures.FirstOrDefault(departure => departure.IsPrimary);

        if (selectedDeparture is null)
        {
            throw new InvalidOperationException("La salida seleccionada no está disponible.");
        }

        var lead = new Lead
        {
            FullName = fullName,
            Phone = phone,
            Email = TrimToNull(request.Email),
            Source = "Web",
            Status = LeadStatus.New,
            InterestedIn = package.Title,
            TravelDates = $"{selectedDeparture.StartDate:dd/MM/yyyy} · {selectedDeparture.Nights} noches",
            Notes = BuildLeadNotes(package, selectedDeparture, TrimToNull(request.Message), referer),
            CreatedAt = DateTime.UtcNow
        };

        _db.Leads.Add(lead);
        await _db.SaveChangesAsync(ct);
    }

    private async Task ApplyRequestAsync(CatalogPackage package, PackageUpsertRequest request, CancellationToken ct)
    {
        package.Title = RequireTrimmed(request.Title, "El título es obligatorio.");
        package.Slug = NormalizeSlug(request.Slug);
        if (string.IsNullOrWhiteSpace(package.Slug))
        {
            throw new InvalidOperationException("El slug es obligatorio.");
        }

        var slugExists = await _db.CatalogPackages
            .AsNoTracking()
            .AnyAsync(item => item.Slug == package.Slug && item.Id != package.Id, ct);

        if (slugExists)
        {
            throw new InvalidOperationException("Ya existe un paquete con ese slug.");
        }

        package.Tagline = TrimToNull(request.Tagline);
        package.Destination = TrimToNull(request.Destination);
        package.CountryName = TrimToNull(request.CountryName);
        package.CountrySlug = NormalizeOptionalSlug(request.CountrySlug) ?? NormalizeOptionalSlug(request.CountryName);
        package.DestinationOrder = Math.Max(0, request.DestinationOrder);
        package.GeneralInfo = TrimToNull(request.GeneralInfo);
        package.UpdatedAt = DateTime.UtcNow;

        SyncDepartures(package, request.Departures ?? Array.Empty<PackageDepartureUpsertRequest>());
    }

    private void SyncDepartures(CatalogPackage package, IReadOnlyList<PackageDepartureUpsertRequest> requests)
    {
        var requestedIds = requests
            .Where(item => item.PublicId.HasValue)
            .Select(item => item.PublicId!.Value)
            .ToHashSet();

        var toRemove = package.Departures
            .Where(item => !requestedIds.Contains(item.PublicId))
            .ToList();

        foreach (var departure in toRemove)
        {
            package.Departures.Remove(departure);
            _db.Remove(departure);
        }

        foreach (var request in requests)
        {
            ValidateDeparture(request);

            var departure = request.PublicId.HasValue
                ? package.Departures.FirstOrDefault(item => item.PublicId == request.PublicId.Value)
                : null;

            if (departure is null)
            {
                departure = new CatalogPackageDeparture
                {
                    CreatedAt = DateTime.UtcNow
                };
                package.Departures.Add(departure);
            }

            departure.StartDate = request.StartDate;
            departure.Nights = request.Nights;
            departure.TransportLabel = RequireTrimmed(request.TransportLabel, "El transporte es obligatorio.");
            departure.HotelName = RequireTrimmed(request.HotelName, "El hotel es obligatorio.");
            departure.MealPlan = RequireTrimmed(request.MealPlan, "El régimen es obligatorio.");
            departure.RoomBase = RequireTrimmed(request.RoomBase, "La base es obligatoria.");
            departure.Currency = NormalizeCurrency(request.Currency);
            departure.SalePrice = request.SalePrice;
            departure.IsPrimary = request.IsPrimary;
            departure.IsActive = request.IsActive;
            departure.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static void ValidateDeparture(PackageDepartureUpsertRequest request)
    {
        if (request.StartDate == default)
        {
            throw new InvalidOperationException("La fecha de salida es obligatoria.");
        }

        if (request.Nights <= 0)
        {
            throw new InvalidOperationException("Las noches deben ser mayores a cero.");
        }

        if (request.SalePrice <= 0)
        {
            throw new InvalidOperationException("La tarifa debe ser mayor a cero.");
        }
    }

    private async Task<CatalogPackageListItemDto> MapAdminListItemAsync(CatalogPackage package, CancellationToken ct)
    {
        var issues = await GetPublishIssuesAsync(package, ct);
        var pricedDeparture = package.Departures
            .Where(departure => departure.IsActive)
            .OrderBy(departure => departure.SalePrice)
            .ThenBy(departure => departure.StartDate)
            .FirstOrDefault()
            ?? package.Departures
                .OrderBy(departure => departure.SalePrice)
                .ThenBy(departure => departure.StartDate)
                .FirstOrDefault();

        return new CatalogPackageListItemDto
        {
            PublicId = package.PublicId,
            Title = package.Title,
            Slug = package.Slug,
            Tagline = package.Tagline,
            Destination = package.Destination,
            CountryName = package.CountryName,
            CountrySlug = package.CountrySlug,
            DestinationOrder = package.DestinationOrder,
            IsPublished = package.IsPublished,
            HasHeroImage = !string.IsNullOrWhiteSpace(package.HeroImageStoredFileName),
            HeroImageUrl = package.HeroImageStoredFileName is null ? null : $"/api/packages/{package.PublicId}/hero-image",
            FromPrice = pricedDeparture?.SalePrice,
            Currency = pricedDeparture?.Currency,
            DepartureCount = package.Departures.Count,
            ActiveDepartureCount = package.Departures.Count(departure => departure.IsActive),
            CanPublish = issues.Count == 0,
            PublishIssues = issues,
            PublicPagePath = $"/embed/packages/{package.Slug}",
            CountryPagePath = string.IsNullOrWhiteSpace(package.CountrySlug) ? null : $"/embed/countries/{package.CountrySlug}",
            CreatedAt = package.CreatedAt,
            UpdatedAt = package.UpdatedAt,
            PublishedAt = package.PublishedAt
        };
    }

    private async Task<CatalogPackageDetailDto> MapAdminDetailAsync(CatalogPackage package, CancellationToken ct)
    {
        var issues = await GetPublishIssuesAsync(package, ct);
        var pricedDeparture = package.Departures
            .Where(departure => departure.IsActive)
            .OrderBy(departure => departure.SalePrice)
            .ThenBy(departure => departure.StartDate)
            .FirstOrDefault()
            ?? package.Departures
                .OrderBy(departure => departure.SalePrice)
                .ThenBy(departure => departure.StartDate)
                .FirstOrDefault();

        var primaryDeparture = package.Departures.FirstOrDefault(departure => departure.IsPrimary);

        return new CatalogPackageDetailDto
        {
            PublicId = package.PublicId,
            Title = package.Title,
            Slug = package.Slug,
            Tagline = package.Tagline,
            Destination = package.Destination,
            CountryName = package.CountryName,
            CountrySlug = package.CountrySlug,
            DestinationOrder = package.DestinationOrder,
            GeneralInfo = package.GeneralInfo,
            IsPublished = package.IsPublished,
            HasHeroImage = !string.IsNullOrWhiteSpace(package.HeroImageStoredFileName),
            HeroImageFileName = package.HeroImageFileName,
            HeroImageUrl = package.HeroImageStoredFileName is null ? null : $"/api/packages/{package.PublicId}/hero-image",
            FromPrice = pricedDeparture?.SalePrice,
            Currency = pricedDeparture?.Currency,
            PrimaryDeparturePublicId = primaryDeparture?.PublicId,
            CanPublish = issues.Count == 0,
            PublishIssues = issues,
            PublicPagePath = $"/embed/packages/{package.Slug}",
            CountryPagePath = string.IsNullOrWhiteSpace(package.CountrySlug) ? null : $"/embed/countries/{package.CountrySlug}",
            Departures = package.Departures
                .OrderBy(departure => departure.StartDate)
                .Select(departure => new CatalogPackageDepartureDto
                {
                    PublicId = departure.PublicId,
                    StartDate = departure.StartDate,
                    Nights = departure.Nights,
                    TransportLabel = departure.TransportLabel,
                    HotelName = departure.HotelName,
                    MealPlan = departure.MealPlan,
                    RoomBase = departure.RoomBase,
                    Currency = departure.Currency,
                    SalePrice = departure.SalePrice,
                    IsPrimary = departure.IsPrimary,
                    IsActive = departure.IsActive
                })
                .ToList(),
            CreatedAt = package.CreatedAt,
            UpdatedAt = package.UpdatedAt,
            PublishedAt = package.PublishedAt
        };
    }

    private static PublicPackageDepartureDto MapPublicDeparture(CatalogPackageDeparture departure)
    {
        return new PublicPackageDepartureDto
        {
            PublicId = departure.PublicId,
            StartDate = departure.StartDate,
            Nights = departure.Nights,
            TransportLabel = departure.TransportLabel,
            HotelName = departure.HotelName,
            MealPlan = departure.MealPlan,
            RoomBase = departure.RoomBase,
            Currency = departure.Currency,
            SalePrice = departure.SalePrice,
            IsPrimary = departure.IsPrimary
        };
    }

    private static PublicPackageDetailDto? MapPublicDetail(CatalogPackage package)
    {
        var primaryDeparture = package.Departures
            .Where(departure => departure.IsActive && departure.IsPrimary)
            .OrderBy(departure => departure.StartDate)
            .FirstOrDefault();

        if (primaryDeparture is null)
        {
            return null;
        }

        var departures = package.Departures
            .Where(departure => departure.IsActive)
            .OrderBy(departure => departure.StartDate)
            .Select(MapPublicDeparture)
            .ToList();

        return new PublicPackageDetailDto
        {
            Title = package.Title,
            Slug = package.Slug,
            Tagline = package.Tagline,
            Destination = package.Destination,
            CountryName = package.CountryName,
            CountrySlug = package.CountrySlug,
            DestinationOrder = package.DestinationOrder,
            GeneralInfo = package.GeneralInfo,
            HeroImageUrl = package.HeroImageStoredFileName is null ? null : $"/api/public/packages/{package.Slug}/hero-image",
            FromPrice = primaryDeparture.SalePrice,
            Currency = primaryDeparture.Currency,
            PrimaryDeparture = MapPublicDeparture(primaryDeparture),
            Departures = departures
        };
    }

    private async Task<IReadOnlyList<string>> GetPublishIssuesAsync(CatalogPackage package, CancellationToken ct)
    {
        var issues = GetBasicPublishIssues(package);

        if (await HasPublishedDestinationConflictAsync(package, ct))
        {
            issues.Add("Ya existe otro paquete publicado con el mismo pais y destino.");
        }

        return issues;
    }

    private static List<string> GetBasicPublishIssues(CatalogPackage package)
    {
        var issues = new List<string>();
        var activeDepartures = package.Departures.Where(departure => departure.IsActive).ToList();
        var primaryDepartures = package.Departures.Where(departure => departure.IsPrimary).ToList();
        var activePrimaryDepartures = activeDepartures.Where(departure => departure.IsPrimary).ToList();

        if (string.IsNullOrWhiteSpace(package.Slug))
        {
            issues.Add("Define un slug válido.");
        }

        if (string.IsNullOrWhiteSpace(package.GeneralInfo))
        {
            issues.Add("Completa la información general.");
        }

        if (string.IsNullOrWhiteSpace(package.HeroImageStoredFileName))
        {
            issues.Add("Sube una imagen principal.");
        }

        if (activeDepartures.Count == 0)
        {
            issues.Add("Debe existir al menos una salida activa.");
        }

        if (primaryDepartures.Count != 1)
        {
            issues.Add("Debe existir exactamente una salida principal.");
        }
        else if (activePrimaryDepartures.Count != 1)
        {
            issues.Add("La salida principal debe estar activa.");
        }

        return issues;
    }

    private async Task<bool> HasPublishedDestinationConflictAsync(CatalogPackage package, CancellationToken ct)
    {
        var normalizedCountrySlug = NormalizeOptionalSlug(package.CountrySlug);
        var normalizedDestination = TrimToNull(package.Destination)?.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalizedCountrySlug) || string.IsNullOrWhiteSpace(normalizedDestination))
        {
            return false;
        }

        return await _db.CatalogPackages
            .AsNoTracking()
            .Where(item => item.Id != package.Id && item.IsPublished && item.CountrySlug == normalizedCountrySlug && item.Destination != null)
            .AnyAsync(item => item.Destination!.ToLower() == normalizedDestination, ct);
    }

    private async Task<(byte[] Bytes, string ContentType)?> ReadHeroImageAsync(CatalogPackage package, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(package.HeroImageStoredFileName))
        {
            return null;
        }

        var path = Path.Combine(_environment.ContentRootPath, "Uploads", "Packages", package.HeroImageStoredFileName);
        if (!File.Exists(path))
        {
            _logger.LogWarning("Hero image missing on disk for package {PackagePublicId}", package.PublicId);
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, ct);
        return (bytes, package.HeroImageContentType ?? "application/octet-stream");
    }

    private void DeleteStoredHeroImage(CatalogPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.HeroImageStoredFileName))
        {
            return;
        }

        try
        {
            var path = Path.Combine(_environment.ContentRootPath, "Uploads", "Packages", package.HeroImageStoredFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting hero image for package {PackagePublicId}", package.PublicId);
        }
    }

    private static IQueryable<CatalogPackage> ApplyOrdering(IQueryable<CatalogPackage> query, PackageListQuery request)
    {
        var sortBy = (request.SortBy ?? "updatedAt").Trim().ToLowerInvariant();
        var desc = request.IsSortDescending();

        return sortBy switch
        {
            "title" => desc
                ? query.OrderByDescending(item => item.Title).ThenByDescending(item => item.UpdatedAt ?? item.CreatedAt)
                : query.OrderBy(item => item.Title).ThenByDescending(item => item.UpdatedAt ?? item.CreatedAt),
            "publishedat" => desc
                ? query.OrderByDescending(item => item.PublishedAt).ThenByDescending(item => item.UpdatedAt ?? item.CreatedAt)
                : query.OrderBy(item => item.PublishedAt).ThenByDescending(item => item.UpdatedAt ?? item.CreatedAt),
            _ => desc
                ? query.OrderByDescending(item => item.UpdatedAt ?? item.CreatedAt).ThenByDescending(item => item.CreatedAt)
                : query.OrderBy(item => item.UpdatedAt ?? item.CreatedAt).ThenBy(item => item.CreatedAt)
        };
    }

    private static string BuildLeadNotes(
        CatalogPackage package,
        CatalogPackageDeparture departure,
        string? message,
        string? referer)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Consulta desde paquete embebido: {package.Title}");
        builder.AppendLine($"Slug: {package.Slug}");
        builder.AppendLine($"Salida elegida: {departure.StartDate:dd/MM/yyyy}");
        builder.AppendLine($"Noches: {departure.Nights}");
        builder.AppendLine($"Transporte: {departure.TransportLabel}");
        builder.AppendLine($"Hotel: {departure.HotelName}");
        builder.AppendLine($"Régimen: {departure.MealPlan}");
        builder.AppendLine($"Base: {departure.RoomBase}");
        builder.AppendLine($"Tarifa: {departure.Currency} {departure.SalePrice:0.##}");

        if (!string.IsNullOrWhiteSpace(referer))
        {
            builder.AppendLine($"Referer: {referer}");
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            builder.AppendLine();
            builder.AppendLine("Mensaje del cliente:");
            builder.AppendLine(message);
        }

        return builder.ToString().Trim();
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

    private static string? NormalizeOptionalSlug(string? value)
    {
        var normalized = NormalizeSlug(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeCurrency(string? value)
    {
        var normalized = (value ?? "USD").Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "USD" : normalized[..Math.Min(3, normalized.Length)];
    }

    private static string RequireTrimmed(string? value, string errorMessage)
    {
        var normalized = TrimToNull(value);
        if (normalized is null)
        {
            throw new InvalidOperationException(errorMessage);
        }

        return normalized;
    }

    private static string? TrimToNull(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string SanitizeOriginalFileName(string fileName)
    {
        var original = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(original))
        {
            original = "package-image";
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            original = original.Replace(invalidChar, '_');
        }

        return original;
    }

    private static bool MatchesHeroImageSignature(string extension, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        return extension.ToLowerInvariant() switch
        {
            ".png" => bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            ".jpg" or ".jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            ".webp" => bytes.Length >= 12
                && bytes.AsSpan().StartsWith("RIFF"u8)
                && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
    }
}
