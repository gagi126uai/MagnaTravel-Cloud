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

public class DestinationService : IDestinationService
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
    private readonly ILogger<DestinationService> _logger;

    public DestinationService(
        AppDbContext db,
        IWebHostEnvironment environment,
        ILogger<DestinationService> logger)
    {
        _db = db;
        _environment = environment;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DestinationListItemDto>> GetDestinationsByCountryIdAsync(int countryId, CancellationToken ct)
    {
        var destinations = await _db.Destinations
            .AsNoTracking()
            .Include(item => item.Country)
            .Include(item => item.Departures)
            .Where(item => item.CountryId == countryId)
            .OrderBy(item => item.DisplayOrder)
            .ThenBy(item => item.Name)
            .ToListAsync(ct);

        var items = new List<DestinationListItemDto>(destinations.Count);
        foreach (var destination in destinations)
        {
            items.Add(await MapAdminListItemAsync(destination, ct));
        }

        return items;
    }

    public async Task<DestinationDetailDto?> GetDestinationByIdAsync(int id, CancellationToken ct)
    {
        var destination = await _db.Destinations
            .Include(item => item.Country)
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        return destination is null ? null : await MapAdminDetailAsync(destination, ct);
    }

    public async Task<DestinationDetailDto> CreateAsync(DestinationUpsertRequest request, CancellationToken ct)
    {
        var destination = new Destination
        {
            CreatedAt = DateTime.UtcNow
        };

        await ApplyRequestAsync(destination, request, ct);

        _db.Destinations.Add(destination);
        await _db.SaveChangesAsync(ct);

        return await MapAdminDetailAsync(destination, ct);
    }

    public async Task<DestinationDetailDto?> UpdateAsync(int id, DestinationUpsertRequest request, CancellationToken ct)
    {
        var destination = await _db.Destinations
            .Include(item => item.Country)
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        if (destination is null)
        {
            return null;
        }

        await ApplyRequestAsync(destination, request, ct);

        if (destination.IsPublished)
        {
            var issues = await GetPublishIssuesAsync(destination, ct);
            if (issues.Count > 0)
            {
                destination.IsPublished = false;
                destination.PublishedAt = null;
            }
        }

        await _db.SaveChangesAsync(ct);
        return await MapAdminDetailAsync(destination, ct);
    }

    public async Task<DestinationDetailDto> PublishAsync(int id, CancellationToken ct)
    {
        var destination = await _db.Destinations
            .Include(item => item.Country)
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.Id == id, ct)
            ?? throw new KeyNotFoundException("Destino no encontrado.");

        var issues = await GetPublishIssuesAsync(destination, ct);
        if (issues.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", issues));
        }

        destination.IsPublished = true;
        destination.PublishedAt = DateTime.UtcNow;
        destination.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return await MapAdminDetailAsync(destination, ct);
    }

    public async Task<DestinationDetailDto> UnpublishAsync(int id, CancellationToken ct)
    {
        var destination = await _db.Destinations
            .Include(item => item.Country)
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.Id == id, ct)
            ?? throw new KeyNotFoundException("Destino no encontrado.");

        destination.IsPublished = false;
        destination.PublishedAt = null;
        destination.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return await MapAdminDetailAsync(destination, ct);
    }

    public async Task<DestinationDetailDto> UploadHeroImageAsync(
        int id,
        Stream fileStream,
        string fileName,
        string contentType,
        CancellationToken ct)
    {
        var destination = await _db.Destinations
            .Include(item => item.Country)
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.Id == id, ct)
            ?? throw new KeyNotFoundException("Destino no encontrado.");

        var safeFileName = SanitizeOriginalFileName(fileName);
        var extension = Path.GetExtension(safeFileName);
        if (!AllowedHeroImageTypes.ContainsKey(extension))
        {
            throw new InvalidOperationException("El formato de imagen no esta permitido.");
        }

        await using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
        {
            throw new InvalidOperationException("La imagen esta vacia.");
        }

        if (buffer.Length > MaxHeroImageSizeBytes)
        {
            throw new InvalidOperationException("La imagen supera el maximo permitido de 10 MB.");
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

        DeleteStoredHeroImage(destination);

        destination.HeroImageFileName = safeFileName;
        destination.HeroImageStoredFileName = Path.Combine(DateTime.UtcNow.Year.ToString(), storedFileName);
        destination.HeroImageContentType = normalizedContentType;
        destination.HeroImageFileSize = buffer.Length;
        destination.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return await MapAdminDetailAsync(destination, ct);
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetHeroImageAsync(int id, CancellationToken ct)
    {
        var destination = await _db.Destinations
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        if (destination is null)
        {
            throw new KeyNotFoundException("Destino no encontrado.");
        }

        return await ReadHeroImageAsync(destination, ct);
    }

    public async Task<PublicPackageDetailDto?> GetPublicPackageBySlugAsync(string slug, CancellationToken ct)
    {
        var normalizedSlug = NormalizeSlug(slug);
        if (string.IsNullOrWhiteSpace(normalizedSlug))
        {
            return null;
        }

        var destination = await _db.Destinations
            .AsNoTracking()
            .Include(item => item.Country)
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.IsPublished && item.Slug == normalizedSlug, ct);

        if (destination is null)
        {
            return null;
        }

        return MapPublicDetail(destination);
    }

    public async Task<PublicPackageDetailDto?> GetPreviewPackageBySlugAsync(string slug, CancellationToken ct)
    {
        var normalizedSlug = NormalizeSlug(slug);
        if (string.IsNullOrWhiteSpace(normalizedSlug))
        {
            return null;
        }

        var destination = await _db.Destinations
            .AsNoTracking()
            .Include(item => item.Country)
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.Slug == normalizedSlug, ct);

        if (destination is null)
        {
            return null;
        }

        return MapPublicDetail(destination, usePreviewHeroImage: true);
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetPublicHeroImageBySlugAsync(string slug, CancellationToken ct)
    {
        var normalizedSlug = NormalizeSlug(slug);
        if (string.IsNullOrWhiteSpace(normalizedSlug))
        {
            return null;
        }

        var destination = await _db.Destinations
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.IsPublished && item.Slug == normalizedSlug, ct);

        return destination is null ? null : await ReadHeroImageAsync(destination, ct);
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
            throw new KeyNotFoundException("Destino no encontrado.");
        }

        var fullName = RequireTrimmed(request.FullName, "El nombre es obligatorio.");
        var phone = RequireTrimmed(request.Phone, "El telefono es obligatorio.");

        var destination = await _db.Destinations
            .Include(item => item.Country)
            .Include(item => item.Departures)
            .FirstOrDefaultAsync(item => item.IsPublished && item.Slug == normalizedSlug, ct)
            ?? throw new KeyNotFoundException("Destino no encontrado.");

        var activeDepartures = destination.Departures
            .Where(departure => departure.IsActive)
            .ToList();

        var selectedDeparture = request.DeparturePublicId.HasValue
            ? activeDepartures.FirstOrDefault(departure => departure.PublicId == request.DeparturePublicId.Value)
            : activeDepartures.FirstOrDefault(departure => departure.IsPrimary);

        if (selectedDeparture is null)
        {
            throw new InvalidOperationException("La salida seleccionada no esta disponible.");
        }

        var lead = new Lead
        {
            FullName = fullName,
            Phone = phone,
            Email = TrimToNull(request.Email),
            Source = "Web",
            Status = LeadStatus.New,
            InterestedIn = destination.Title,
            TravelDates = $"{selectedDeparture.StartDate:dd/MM/yyyy} - {selectedDeparture.Nights} noches",
            Notes = BuildLeadNotes(destination, selectedDeparture, TrimToNull(request.Message), referer),
            CreatedAt = DateTime.UtcNow
        };

        _db.Leads.Add(lead);
        await _db.SaveChangesAsync(ct);
    }

    private async Task ApplyRequestAsync(Destination destination, DestinationUpsertRequest request, CancellationToken ct)
    {
        var country = await _db.Countries.FirstOrDefaultAsync(item => item.PublicId == request.CountryPublicId, ct);
        if (country is null)
        {
            throw new InvalidOperationException("El pais seleccionado no existe.");
        }

        destination.CountryId = country.Id;
        destination.Country = country;
        destination.Name = RequireTrimmed(request.Name, "El nombre del destino es obligatorio.");
        destination.Title = RequireTrimmed(request.Title, "El titulo comercial es obligatorio.");

        var normalizedName = destination.Name.ToLowerInvariant();
        var duplicateNameExists = await _db.Destinations
            .AsNoTracking()
            .Where(item => item.Id != destination.Id && item.CountryId == country.Id)
            .AnyAsync(item => item.Name.ToLower() == normalizedName, ct);

        if (duplicateNameExists)
        {
            throw new InvalidOperationException("Ya existe un destino con ese nombre dentro de este pais.");
        }

        if (destination.Id == 0 || string.IsNullOrWhiteSpace(destination.Slug))
        {
            destination.Slug = await GenerateUniqueSlugAsync(request.Slug, destination.Name, destination.Title, destination.Id, ct);
        }

        destination.Tagline = TrimToNull(request.Tagline);
        destination.DisplayOrder = Math.Max(0, request.DisplayOrder);
        destination.GeneralInfo = TrimToNull(request.GeneralInfo);
        destination.UpdatedAt = DateTime.UtcNow;

        SyncDepartures(destination, request.Departures ?? Array.Empty<DestinationDepartureUpsertRequest>());
    }

    private async Task<string> GenerateUniqueSlugAsync(string? requestedSlug, string name, string title, int currentId, CancellationToken ct)
    {
        var baseSlug = NormalizeSlug(string.IsNullOrWhiteSpace(requestedSlug) ? name : requestedSlug);
        if (string.IsNullOrWhiteSpace(baseSlug))
        {
            baseSlug = NormalizeSlug(title);
        }

        if (string.IsNullOrWhiteSpace(baseSlug))
        {
            throw new InvalidOperationException("No se pudo preparar la publicacion web de este destino.");
        }

        var candidate = baseSlug;
        var suffix = 2;
        while (await _db.Destinations
                   .AsNoTracking()
                   .AnyAsync(item => item.Slug == candidate && item.Id != currentId, ct))
        {
            candidate = $"{baseSlug}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private void SyncDepartures(Destination destination, IReadOnlyList<DestinationDepartureUpsertRequest> requests)
    {
        var requestedIds = requests
            .Where(item => item.PublicId.HasValue)
            .Select(item => item.PublicId!.Value)
            .ToHashSet();

        var toRemove = destination.Departures
            .Where(item => !requestedIds.Contains(item.PublicId))
            .ToList();

        foreach (var departure in toRemove)
        {
            destination.Departures.Remove(departure);
            _db.Remove(departure);
        }

        foreach (var request in requests)
        {
            ValidateDeparture(request);

            var departure = request.PublicId.HasValue
                ? destination.Departures.FirstOrDefault(item => item.PublicId == request.PublicId.Value)
                : null;

            if (departure is null)
            {
                departure = new DestinationDeparture
                {
                    CreatedAt = DateTime.UtcNow
                };
                destination.Departures.Add(departure);
            }

            departure.StartDate = request.StartDate;
            departure.Nights = request.Nights;
            departure.TransportLabel = RequireTrimmed(request.TransportLabel, "El transporte es obligatorio.");
            departure.HotelName = RequireTrimmed(request.HotelName, "El hotel es obligatorio.");
            departure.MealPlan = RequireTrimmed(request.MealPlan, "El regimen es obligatorio.");
            departure.RoomBase = RequireTrimmed(request.RoomBase, "La base es obligatoria.");
            departure.Currency = NormalizeCurrency(request.Currency);
            departure.SalePrice = request.SalePrice;
            departure.IsPrimary = request.IsPrimary;
            departure.IsActive = request.IsActive;
            departure.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static void ValidateDeparture(DestinationDepartureUpsertRequest request)
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

    private async Task<DestinationListItemDto> MapAdminListItemAsync(Destination destination, CancellationToken ct)
    {
        var issues = await GetPublishIssuesAsync(destination, ct);
        var pricedDeparture = destination.Departures
            .Where(departure => departure.IsActive)
            .OrderBy(departure => departure.SalePrice)
            .ThenBy(departure => departure.StartDate)
            .FirstOrDefault()
            ?? destination.Departures
                .OrderBy(departure => departure.SalePrice)
                .ThenBy(departure => departure.StartDate)
                .FirstOrDefault();

        var nextDeparture = destination.Departures
            .Where(departure => departure.IsActive)
            .OrderBy(departure => departure.StartDate)
            .FirstOrDefault()
            ?? destination.Departures
                .OrderBy(departure => departure.StartDate)
                .FirstOrDefault();

        return new DestinationListItemDto
        {
            PublicId = destination.PublicId,
            CountryPublicId = destination.Country?.PublicId ?? Guid.Empty,
            CountryName = destination.Country?.Name ?? string.Empty,
            CountrySlug = destination.Country?.Slug ?? string.Empty,
            Name = destination.Name,
            Title = destination.Title,
            Slug = destination.Slug,
            Tagline = destination.Tagline,
            DisplayOrder = destination.DisplayOrder,
            IsPublished = destination.IsPublished,
            HasHeroImage = !string.IsNullOrWhiteSpace(destination.HeroImageStoredFileName),
            HeroImageUrl = destination.HeroImageStoredFileName is null ? null : $"/api/destinations/{destination.PublicId}/hero-image",
            FromPrice = pricedDeparture?.SalePrice,
            Currency = pricedDeparture?.Currency,
            NextDepartureDate = nextDeparture?.StartDate,
            DepartureCount = destination.Departures.Count,
            ActiveDepartureCount = destination.Departures.Count(departure => departure.IsActive),
            CanPublish = issues.Count == 0,
            PublishIssues = issues,
            PublicPagePath = $"/embed/packages/{destination.Slug}",
            CountryPagePath = string.IsNullOrWhiteSpace(destination.Country?.Slug) ? string.Empty : $"/embed/countries/{destination.Country.Slug}",
            CreatedAt = destination.CreatedAt,
            UpdatedAt = destination.UpdatedAt,
            PublishedAt = destination.PublishedAt
        };
    }

    private async Task<DestinationDetailDto> MapAdminDetailAsync(Destination destination, CancellationToken ct)
    {
        var issues = await GetPublishIssuesAsync(destination, ct);
        var pricedDeparture = destination.Departures
            .Where(departure => departure.IsActive)
            .OrderBy(departure => departure.SalePrice)
            .ThenBy(departure => departure.StartDate)
            .FirstOrDefault()
            ?? destination.Departures
                .OrderBy(departure => departure.SalePrice)
                .ThenBy(departure => departure.StartDate)
                .FirstOrDefault();

        var primaryDeparture = destination.Departures.FirstOrDefault(departure => departure.IsPrimary);

        return new DestinationDetailDto
        {
            PublicId = destination.PublicId,
            CountryPublicId = destination.Country?.PublicId ?? Guid.Empty,
            CountryName = destination.Country?.Name ?? string.Empty,
            CountrySlug = destination.Country?.Slug ?? string.Empty,
            Name = destination.Name,
            Title = destination.Title,
            Slug = destination.Slug,
            Tagline = destination.Tagline,
            DisplayOrder = destination.DisplayOrder,
            GeneralInfo = destination.GeneralInfo,
            IsPublished = destination.IsPublished,
            HasHeroImage = !string.IsNullOrWhiteSpace(destination.HeroImageStoredFileName),
            HeroImageFileName = destination.HeroImageFileName,
            HeroImageUrl = destination.HeroImageStoredFileName is null ? null : $"/api/destinations/{destination.PublicId}/hero-image",
            FromPrice = pricedDeparture?.SalePrice,
            Currency = pricedDeparture?.Currency,
            PrimaryDeparturePublicId = primaryDeparture?.PublicId,
            CanPublish = issues.Count == 0,
            PublishIssues = issues,
            PublicPagePath = $"/embed/packages/{destination.Slug}",
            CountryPagePath = string.IsNullOrWhiteSpace(destination.Country?.Slug) ? string.Empty : $"/embed/countries/{destination.Country.Slug}",
            Departures = destination.Departures
                .OrderBy(departure => departure.StartDate)
                .Select(departure => new DestinationDepartureDto
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
            CreatedAt = destination.CreatedAt,
            UpdatedAt = destination.UpdatedAt,
            PublishedAt = destination.PublishedAt
        };
    }

    private static PublicPackageDepartureDto MapPublicDeparture(DestinationDeparture departure)
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

    private static PublicPackageDetailDto? MapPublicDetail(Destination destination, bool usePreviewHeroImage = false)
    {
        var primaryDeparture = destination.Departures
            .Where(departure => departure.IsActive && departure.IsPrimary)
            .OrderBy(departure => departure.StartDate)
            .FirstOrDefault();

        if (primaryDeparture is null)
        {
            return null;
        }

        var departures = destination.Departures
            .Where(departure => departure.IsActive)
            .OrderBy(departure => departure.StartDate)
            .Select(MapPublicDeparture)
            .ToList();

        return new PublicPackageDetailDto
        {
            Title = destination.Title,
            Slug = destination.Slug,
            Tagline = destination.Tagline,
            Destination = destination.Name,
            CountryName = destination.Country?.Name,
            CountrySlug = destination.Country?.Slug,
            DestinationOrder = destination.DisplayOrder,
            GeneralInfo = destination.GeneralInfo,
            HeroImageUrl = destination.HeroImageStoredFileName is null
                ? null
                : usePreviewHeroImage
                    ? $"/api/destinations/{destination.PublicId}/hero-image"
                    : $"/api/public/packages/{destination.Slug}/hero-image",
            FromPrice = primaryDeparture.SalePrice,
            Currency = primaryDeparture.Currency,
            PrimaryDeparture = MapPublicDeparture(primaryDeparture),
            Departures = departures
        };
    }

    private async Task<IReadOnlyList<string>> GetPublishIssuesAsync(Destination destination, CancellationToken ct)
    {
        var issues = GetBasicPublishIssues(destination);

        if (await HasDestinationNameConflictAsync(destination, ct))
        {
            issues.Add("Ya existe otro destino con el mismo nombre dentro de este pais.");
        }

        return issues;
    }

    private static List<string> GetBasicPublishIssues(Destination destination)
    {
        var issues = new List<string>();
        var activeDepartures = destination.Departures.Where(departure => departure.IsActive).ToList();
        var primaryDepartures = destination.Departures.Where(departure => departure.IsPrimary).ToList();
        var activePrimaryDepartures = activeDepartures.Where(departure => departure.IsPrimary).ToList();

        if (string.IsNullOrWhiteSpace(destination.Slug))
        {
            issues.Add("No se pudo preparar la publicacion web de este destino.");
        }

        if (string.IsNullOrWhiteSpace(destination.GeneralInfo))
        {
            issues.Add("Completa la descripcion para la web.");
        }

        if (string.IsNullOrWhiteSpace(destination.HeroImageStoredFileName))
        {
            issues.Add("Sube una imagen principal.");
        }

        if (activeDepartures.Count == 0)
        {
            issues.Add("Agrega al menos una salida visible.");
        }

        if (primaryDepartures.Count != 1)
        {
            issues.Add("Marca una sola salida destacada.");
        }
        else if (activePrimaryDepartures.Count != 1)
        {
            issues.Add("La salida destacada debe estar visible.");
        }

        return issues;
    }

    private async Task<bool> HasDestinationNameConflictAsync(Destination destination, CancellationToken ct)
    {
        var normalizedName = TrimToNull(destination.Name)?.ToLowerInvariant();
        if (destination.CountryId <= 0 || string.IsNullOrWhiteSpace(normalizedName))
        {
            return false;
        }

        return await _db.Destinations
            .AsNoTracking()
            .Where(item => item.Id != destination.Id && item.CountryId == destination.CountryId)
            .AnyAsync(item => item.Name.ToLower() == normalizedName, ct);
    }

    private async Task<(byte[] Bytes, string ContentType)?> ReadHeroImageAsync(Destination destination, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(destination.HeroImageStoredFileName))
        {
            return null;
        }

        var path = Path.Combine(_environment.ContentRootPath, "Uploads", "Packages", destination.HeroImageStoredFileName);
        if (!File.Exists(path))
        {
            _logger.LogWarning("Hero image missing on disk for destination {DestinationPublicId}", destination.PublicId);
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, ct);
        return (bytes, destination.HeroImageContentType ?? "application/octet-stream");
    }

    private void DeleteStoredHeroImage(Destination destination)
    {
        if (string.IsNullOrWhiteSpace(destination.HeroImageStoredFileName))
        {
            return;
        }

        try
        {
            var path = Path.Combine(_environment.ContentRootPath, "Uploads", "Packages", destination.HeroImageStoredFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting hero image for destination {DestinationPublicId}", destination.PublicId);
        }
    }

    private static string BuildLeadNotes(
        Destination destination,
        DestinationDeparture departure,
        string? message,
        string? referer)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Consulta desde destino web: {destination.Title}");
        builder.AppendLine($"Destino: {destination.Name}");
        builder.AppendLine($"Salida elegida: {departure.StartDate:dd/MM/yyyy}");
        builder.AppendLine($"Noches: {departure.Nights}");
        builder.AppendLine($"Transporte: {departure.TransportLabel}");
        builder.AppendLine($"Hotel: {departure.HotelName}");
        builder.AppendLine($"Regimen: {departure.MealPlan}");
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
            original = "destination-image";
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            original = original.Replace(invalidChar, '-');
        }

        return original.Length > 120 ? original[..120] : original;
    }

    private static bool MatchesHeroImageSignature(string extension, byte[] bytes)
    {
        if (bytes.Length < 12)
        {
            return false;
        }

        extension = extension.ToLowerInvariant();
        return extension switch
        {
            ".png" => bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47,
            ".jpg" or ".jpeg" => bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[^2] == 0xFF && bytes[^1] == 0xD9,
            ".webp" => bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                       bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50,
            _ => false
        };
    }
}
