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

public class CatalogPackageServiceTests : IDisposable
{
    private readonly string _contentRootPath;
    private readonly Mock<IWebHostEnvironment> _environmentMock = new();
    private readonly Mock<ILogger<CatalogPackageService>> _loggerMock = new();

    public CatalogPackageServiceTests()
    {
        _contentRootPath = Path.Combine(Path.GetTempPath(), "catalog-package-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRootPath);
        _environmentMock.SetupGet(environment => environment.ContentRootPath).Returns(_contentRootPath);
    }

    [Fact]
    public async Task PublishAsync_ShouldRejectPackage_WhenHeroImageIsMissing()
    {
        await using var context = CreateContext();
        var package = BuildPublishedReadyPackage();
        package.HeroImageStoredFileName = null;
        package.HeroImageFileName = null;
        package.HeroImageContentType = null;

        context.CatalogPackages.Add(package);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(package.Id, CancellationToken.None));

        Assert.Contains("imagen principal", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPublicPackageBySlugAsync_ShouldReturnOnlyPublishedActiveDepartures()
    {
        await using var context = CreateContext();
        var package = BuildPublishedReadyPackage();
        package.Departures.Add(new CatalogPackageDeparture
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

        context.CatalogPackages.Add(package);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var result = await service.GetPublicPackageBySlugAsync(package.Slug, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(package.Title, result!.Title);
        Assert.Single(result.Departures);
        Assert.Equal(package.Departures.Single(departure => departure.IsPrimary).PublicId, result.PrimaryDeparture.PublicId);
        Assert.All(result.Departures, departure => Assert.NotEqual("Hotel oculto", departure.HotelName));
    }

    [Fact]
    public async Task CreatePublicLeadAsync_ShouldCreateLeadWithSelectedDepartureContext()
    {
        await using var context = CreateContext();
        var package = BuildPublishedReadyPackage();
        var alternateDeparture = new CatalogPackageDeparture
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
        package.Departures.Add(alternateDeparture);

        context.CatalogPackages.Add(package);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.CreatePublicLeadAsync(
            package.Slug,
            new PublicPackageLeadRequest(
                "Ana Perez",
                "+54 9 11 5555-5555",
                "ana@example.com",
                "Quiero mas info del paquete.",
                alternateDeparture.PublicId,
                null),
            "https://www.hostinger-site.test/landing",
            CancellationToken.None);

        var lead = await context.Leads.SingleAsync();

        Assert.Equal("Ana Perez", lead.FullName);
        Assert.Equal("Web", lead.Source);
        Assert.Equal(LeadStatus.New, lead.Status);
        Assert.Equal(package.Title, lead.InterestedIn);
        Assert.Contains("12 noches", lead.TravelDates);
        Assert.Contains("VIK Hotel Arena Blanca", lead.Notes);
        Assert.Contains("Referer: https://www.hostinger-site.test/landing", lead.Notes);
    }

    [Fact]
    public async Task GetPublicCountryBySlugAsync_ShouldReturnPublishedDestinationsOrderedByDestinationOrder()
    {
        await using var context = CreateContext();
        var firstPackage = BuildPublishedReadyPackage();
        firstPackage.Title = "Punta Cana";
        firstPackage.Slug = "punta-cana";
        firstPackage.Destination = "Punta Cana";
        firstPackage.DestinationOrder = 2;

        var secondPackage = BuildPublishedReadyPackage();
        secondPackage.Title = "Bayahibe";
        secondPackage.Slug = "bayahibe";
        secondPackage.Destination = "Bayahibe";
        secondPackage.DestinationOrder = 1;

        context.CatalogPackages.AddRange(firstPackage, secondPackage);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var result = await service.GetPublicCountryBySlugAsync("republica-dominicana", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("republica-dominicana", result!.CountrySlug);
        Assert.Equal("bayahibe", result.SelectedPackageSlug);
        Assert.Equal(new[] { "bayahibe", "punta-cana" }, result.Destinations.Select(item => item.PackageSlug).ToArray());
        Assert.Equal(new[] { "Bayahibe", "Punta Cana" }, result.Packages.Select(item => item.Title).ToArray());
    }

    [Fact]
    public async Task PublishAsync_ShouldRejectDuplicatePublishedDestinationInSameCountry()
    {
        await using var context = CreateContext();
        var publishedPackage = BuildPublishedReadyPackage();

        var conflictingPackage = BuildPublishedReadyPackage();
        conflictingPackage.Title = "Punta Cana Mayo";
        conflictingPackage.Slug = "punta-cana-mayo";
        conflictingPackage.IsPublished = false;
        conflictingPackage.PublishedAt = null;

        context.CatalogPackages.AddRange(publishedPackage, conflictingPackage);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(conflictingPackage.Id, CancellationToken.None));

        Assert.Contains("mismo pais y destino", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    private CatalogPackageService CreateService(AppDbContext context)
    {
        return new CatalogPackageService(context, _environmentMock.Object, _loggerMock.Object);
    }

    private static CatalogPackage BuildPublishedReadyPackage()
    {
        return new CatalogPackage
        {
            Title = "Punta Cana",
            Slug = "punta-cana",
            Tagline = "Vivi el Caribe",
            Destination = "Punta Cana",
            CountryName = "Republica Dominicana",
            CountrySlug = "republica-dominicana",
            DestinationOrder = 0,
            GeneralInfo = "Incluye alojamiento, asistencia y coordinacion comercial.",
            HeroImageFileName = "hero.jpg",
            HeroImageStoredFileName = Path.Combine("2026", "hero.jpg"),
            HeroImageContentType = "image/jpeg",
            HeroImageFileSize = 1024,
            IsPublished = true,
            PublishedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            Departures =
            {
                new CatalogPackageDeparture
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
                }
            }
        };
    }
}
