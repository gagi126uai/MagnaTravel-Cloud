using System.Net;
using System.Net.Http.Json;
using TravelApi.Contracts.Quotes;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

public class QuotesControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public QuotesControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateQuote_ReturnsCreatedQuote()
    {
        int customerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Customers.RemoveRange(dbContext.Customers);
            await dbContext.SaveChangesAsync();

            var customer = new Customer { FullName = "Cliente Cotizacion" };
            dbContext.Customers.Add(customer);
            await dbContext.SaveChangesAsync();
            customerId = customer.Id;
        }

        var client = _factory.CreateClient();
        var request = new CreateQuoteRequest(
            "Q-100",
            customerId,
            QuoteStatuses.Draft,
            new CreateQuoteVersionRequest("Flight", Currency.USD, 1200m, null, "Inicial"));

        var response = await client.PostAsJsonAsync("/api/quotes", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<QuoteDetailDto>();
        Assert.NotNull(payload);
        Assert.Equal("Q-100", payload!.ReferenceCode);
    }

    [Fact]
    public async Task CreateVersion_AddsNewVersion()
    {
        int quoteId;
        int customerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Quotes.RemoveRange(dbContext.Quotes);
            dbContext.Customers.RemoveRange(dbContext.Customers);
            await dbContext.SaveChangesAsync();

            var customer = new Customer { FullName = "Cliente Cotizacion" };
            dbContext.Customers.Add(customer);
            await dbContext.SaveChangesAsync();
            customerId = customer.Id;

            var quote = new Quote
            {
                ReferenceCode = "Q-200",
                Status = QuoteStatuses.Draft,
                CustomerId = customerId,
                Versions = new List<QuoteVersion>
                {
                    new()
                    {
                        VersionNumber = 1,
                        ProductType = "Flight",
                        Currency = Currency.USD,
                        TotalAmount = 1000m
                    }
                }
            };

            dbContext.Quotes.Add(quote);
            await dbContext.SaveChangesAsync();
            quoteId = quote.Id;
        }

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/quotes/{quoteId}/versions",
            new CreateQuoteVersionRequest("Hotel", Currency.EUR, 1300m, null, "Update"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<QuoteVersionDto>();
        Assert.NotNull(payload);
        Assert.Equal(2, payload!.VersionNumber);
    }

    [Fact]
    public async Task GetQuotes_ReturnsLatestVersionData()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Quotes.RemoveRange(dbContext.Quotes);
            dbContext.Customers.RemoveRange(dbContext.Customers);
            await dbContext.SaveChangesAsync();

            var customer = new Customer { FullName = "Cliente Cotizacion" };
            dbContext.Customers.Add(customer);
            await dbContext.SaveChangesAsync();

            var quote = new Quote
            {
                ReferenceCode = "Q-300",
                Status = QuoteStatuses.Sent,
                CustomerId = customer.Id,
                Versions = new List<QuoteVersion>
                {
                    new()
                    {
                        VersionNumber = 1,
                        ProductType = "Flight",
                        Currency = Currency.USD,
                        TotalAmount = 900m
                    },
                    new()
                    {
                        VersionNumber = 2,
                        ProductType = "Flight",
                        Currency = Currency.USD,
                        TotalAmount = 950m
                    }
                }
            };

            dbContext.Quotes.Add(quote);
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/quotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<List<QuoteSummaryDto>>();
        Assert.NotNull(payload);
        Assert.Contains(payload!, quote => quote.ReferenceCode == "Q-300" && quote.LatestVersion == 2);
    }
}
