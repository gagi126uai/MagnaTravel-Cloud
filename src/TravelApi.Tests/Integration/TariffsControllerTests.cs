using System.Net;
using System.Net.Http.Json;
using TravelApi.Contracts.Tariffs;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

public class TariffsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TariffsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetTariffs_ReturnsSeededTariff()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Tariffs.RemoveRange(dbContext.Tariffs);
            await dbContext.SaveChangesAsync();

            dbContext.Tariffs.Add(new Tariff
            {
                Name = "Tarifa Verano",
                ProductType = "Flight",
                Currency = Currency.USD,
                DefaultPrice = 1500m,
                IsActive = true
            });
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/tariffs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<List<TariffSummaryDto>>();
        Assert.NotNull(payload);
        Assert.Contains(payload!, tariff => tariff.Name == "Tarifa Verano");
    }

    [Fact]
    public async Task CreateTariff_ReturnsCreatedTariff()
    {
        var client = _factory.CreateClient();
        var request = new CreateTariffRequest(
            "Tarifa Invierno",
            "Promo",
            "Hotel",
            Currency.ARS,
            800m,
            true);

        var response = await client.PostAsJsonAsync("/api/tariffs", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<TariffSummaryDto>();
        Assert.NotNull(payload);
        Assert.Equal("Hotel", payload!.ProductType);
    }

    [Fact]
    public async Task CreateValidity_ReturnsCreatedValidity()
    {
        int tariffId;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Tariffs.RemoveRange(dbContext.Tariffs);
            await dbContext.SaveChangesAsync();

            var tariff = new Tariff
            {
                Name = "Tarifa Primavera",
                ProductType = "Package",
                Currency = Currency.USD,
                DefaultPrice = 1200m,
                IsActive = true
            };
            dbContext.Tariffs.Add(tariff);
            await dbContext.SaveChangesAsync();
            tariffId = tariff.Id;
        }

        var client = _factory.CreateClient();
        var request = new CreateTariffValidityRequest(
            new DateTime(2024, 9, 1),
            new DateTime(2024, 9, 30),
            1500m,
            true,
            "Promo");

        var response = await client.PostAsJsonAsync($"/api/tariffs/{tariffId}/validities", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<TariffValidityDto>();
        Assert.NotNull(payload);
        Assert.Equal(1500m, payload!.Price);
    }
}
