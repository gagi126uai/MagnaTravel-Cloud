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
}
