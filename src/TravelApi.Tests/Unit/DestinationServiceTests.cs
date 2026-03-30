using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

public class DestinationServiceTests : IDisposable
{
    private readonly string _contentRootPath;
    private readonly Mock<IWebHostEnvironment> _environmentMock = new();
    private readonly Mock<ILogger<DestinationService>> _loggerMock = new();

    public DestinationServiceTests()
    {
        _contentRootPath = Path.Combine(Path.GetTempPath(), "destination-service-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRootPath);
        _environmentMock.SetupGet(environment => environment.ContentRootPath).Returns(_contentRootPath);
    }

    [Fact]
    public async Task PublishAsync_ShouldRejectDestination_WhenHeroImageIsMissing()
    {
        await using var context = CreateContext();
        var country = BuildCountry();
        var destination = BuildPublishedReadyDestination(country);
        destination.HeroImageStoredFileName = null;
        destination.HeroImageFileName = null;
        destination.HeroImageContentType = null;
        destination.HeroImageFileSize = null;
        destination.IsPublished = false;
        destination.PublishedAt = null;

        context.Countries.Add(country);
        context.Destinations.Add(destination);
        await context.SaveChangesAsync();

        var service = CreateDestinationService(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(destination.Id, CancellationToken.None));

        Assert.Contains("imagen principal", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPublicPackageBySlugAsync_ShouldReturnOnlyPublishedActiveDepartures()
    {
        await using var context = CreateContext();
        var country = BuildCountry();
        var destination = BuildPublishedReadyDestination(country);
        destination.Departures.Add(new DestinationDeparture
        {
            StartDate = new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc),
            Nights = 10,
            TransportLabel = "Aereo",
            HotelName = "Hotel oculto",
            MealPlan = "AI",
            RoomBase = "Triple",
            Currency = "USD",
            SalePrice = 2400m,
            IsPrimary = false,
            IsActive = false,
            CreatedAt = DateTime.UtcNow
        });

        context.Countries.Add(country);
        context.Destinations.Add(destination);
        await context.SaveChangesAsync();

        var service = CreateDestinationService(context);
        var result = await service.GetPublicPackageBySlugAsync(destination.Slug, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(destination.Title, result!.Title);
        Assert.Single(result.Departures);
        Assert.Equal(destination.Departures.Single(departure => departure.IsPrimary).PublicId, result.PrimaryDeparture.PublicId);
        Assert.All(result.Departures, departure => Assert.NotEqual("Hotel oculto", departure.HotelName));
    }

    [Fact]
    public async Task CreatePublicLeadAsync_ShouldCreateLeadWithSelectedDepartureContext()
    {
        await using var context = CreateContext();
        var country = BuildCountry();
        var destination = BuildPublishedReadyDestination(country);
        var alternateDeparture = new DestinationDeparture
        {
            StartDate = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
            Nights = 12,
            TransportLabel = "Aereo",
            HotelName = "VIK Hotel Arena Blanca",
            MealPlan = "All Inclusive",
            RoomBase = "Doble",
            Currency = "USD",
            SalePrice = 2600m,
            IsPrimary = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        destination.Departures.Add(alternateDeparture);

        context.Countries.Add(country);
        context.Destinations.Add(destination);
        await context.SaveChangesAsync();

        var service = CreateDestinationService(context);
        await service.CreatePublicLeadAsync(
            destination.Slug,
            new PublicPackageLeadRequest(
                "Ana Perez",
                "+54 9 11 5555-5555",
                "ana@example.com",
                "Quiero mas info del destino.",
                alternateDeparture.PublicId,
                null),
            "https://www.hostinger-site.test/landing",
            CancellationToken.None);

        var lead = await context.Leads.SingleAsync();

        Assert.Equal("Ana Perez", lead.FullName);
        Assert.Equal("Web", lead.Source);
        Assert.Equal(LeadStatus.New, lead.Status);
        Assert.Equal(destination.Title, lead.InterestedIn);
        Assert.Contains("12 noches", lead.TravelDates);
        Assert.Contains("VIK Hotel Arena Blanca", lead.Notes);
        Assert.Contains("Referer: https://www.hostinger-site.test/landing", lead.Notes);
    }

    [Fact]
    public async Task PublishAsync_ShouldRejectDuplicateDestinationNameInSameCountry()
    {
        await using var context = CreateContext();
        var country = BuildCountry();
        var publishedDestination = BuildPublishedReadyDestination(country);

        var conflictingDestination = BuildPublishedReadyDestination(country, slug: "punta-cana-mayo", title: "Punta Cana Mayo");
        conflictingDestination.IsPublished = false;
        conflictingDestination.PublishedAt = null;

        context.Countries.Add(country);
        context.Destinations.AddRange(publishedDestination, conflictingDestination);
        await context.SaveChangesAsync();

        var service = CreateDestinationService(context);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(conflictingDestination.Id, CancellationToken.None));

        Assert.Contains("mismo nombre", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPublicCountryBySlugAsync_ShouldReturnPublishedDestinationsOrderedByDisplayOrder()
    {
        await using var context = CreateContext();
        var country = BuildCountry();
        var firstDestination = BuildPublishedReadyDestination(country, slug: "punta-cana", destinationName: "Punta Cana", title: "Punta Cana", displayOrder: 2);
        var secondDestination = BuildPublishedReadyDestination(country, slug: "bayahibe", destinationName: "Bayahibe", title: "Bayahibe", displayOrder: 1);

        context.Countries.Add(country);
        context.Destinations.AddRange(firstDestination, secondDestination);
        await context.SaveChangesAsync();

        var service = new CountryService(context);
        var result = await service.GetPublicCountryBySlugAsync(country.Slug, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(country.Slug, result!.CountrySlug);
        Assert.Equal(new[] { "bayahibe", "punta-cana" }, result.Destinations.Select(item => item.PackageSlug).ToArray());
        Assert.Equal(new[] { "Bayahibe", "Punta Cana" }, result.Destinations.Select(item => item.Destination).ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRootPath))
        {
            Directory.Delete(_contentRootPath, recursive: true);
        }
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private DestinationService CreateDestinationService(AppDbContext context)
    {
        return new DestinationService(context, _environmentMock.Object, _loggerMock.Object);
    }

    private static Country BuildCountry(string name = "Republica Dominicana", string slug = "republica-dominicana")
    {
        return new Country
        {
            Name = name,
            Slug = slug,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static Destination BuildPublishedReadyDestination(
        Country country,
        string slug = "punta-cana",
        string destinationName = "Punta Cana",
        string title = "Punta Cana",
        int displayOrder = 0)
    {
        var destination = new Destination
        {
            Country = country,
            CountryId = country.Id,
            Name = destinationName,
            Title = title,
            Slug = slug,
            Tagline = "Vivi el Caribe",
            DisplayOrder = displayOrder,
            GeneralInfo = "Incluye alojamiento, asistencia y coordinacion comercial.",
            HeroImageFileName = "hero.jpg",
            HeroImageStoredFileName = Path.Combine("2026", "hero.jpg"),
            HeroImageContentType = "image/jpeg",
            HeroImageFileSize = 1024,
            IsPublished = true,
            PublishedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        destination.Departures.Add(new DestinationDeparture
        {
            StartDate = new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc),
            Nights = 9,
            TransportLabel = "Aereo",
            HotelName = "VIK Hotel Arena Blanca",
            MealPlan = "All Inclusive",
            RoomBase = "Doble",
            Currency = "USD",
            SalePrice = 2006m,
            IsPrimary = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        return destination;
    }
}
