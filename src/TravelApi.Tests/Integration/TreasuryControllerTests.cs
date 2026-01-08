using System.Net;
using System.Net.Http.Json;
using TravelApi.Contracts.Treasury;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

public class TreasuryControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TreasuryControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateReceipt_ReturnsCreatedReceipt()
    {
        var client = _factory.CreateClient();
        var request = new CreateTreasuryReceiptRequest(
            "RC-100",
            "Transfer",
            Currency.USD,
            500m,
            null,
            "Ingreso");

        var response = await client.PostAsJsonAsync("/api/treasury/receipts", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<TreasuryReceiptDto>();
        Assert.NotNull(payload);
        Assert.Equal("RC-100", payload!.Reference);
    }

    [Fact]
    public async Task ApplyReceipt_CreatesApplication()
    {
        int receiptId;
        int reservationId;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.TreasuryReceipts.RemoveRange(dbContext.TreasuryReceipts);
            dbContext.Reservations.RemoveRange(dbContext.Reservations);
            dbContext.Customers.RemoveRange(dbContext.Customers);
            await dbContext.SaveChangesAsync();

            var customer = new Customer { FullName = "Cliente Test" };
            dbContext.Customers.Add(customer);
            await dbContext.SaveChangesAsync();

            var reservation = new Reservation
            {
                ReferenceCode = "RES-4001",
                Status = ReservationStatuses.Confirmed,
                ProductType = "Flight",
                DepartureDate = new DateTime(2024, 10, 1),
                BasePrice = 1000m,
                Commission = 100m,
                TotalAmount = 1100m,
                CustomerId = customer.Id
            };
            dbContext.Reservations.Add(reservation);

            var receipt = new TreasuryReceipt
            {
                Reference = "RC-200",
                Method = "Cash",
                Currency = Currency.USD,
                Amount = 600m
            };
            dbContext.TreasuryReceipts.Add(receipt);

            await dbContext.SaveChangesAsync();
            reservationId = reservation.Id;
            receiptId = receipt.Id;
        }

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/treasury/receipts/{receiptId}/applications",
            new ApplyTreasuryReceiptRequest(reservationId, 200m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<TreasuryApplicationDto>();
        Assert.NotNull(payload);
        Assert.Equal(reservationId, payload!.ReservationId);
    }

    [Fact]
    public async Task ApplyReceipt_ReturnsBadRequestWhenAmountExceedsRemaining()
    {
        int receiptId;
        int reservationId;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.TreasuryReceipts.RemoveRange(dbContext.TreasuryReceipts);
            dbContext.Reservations.RemoveRange(dbContext.Reservations);
            dbContext.Customers.RemoveRange(dbContext.Customers);
            await dbContext.SaveChangesAsync();

            var customer = new Customer { FullName = "Cliente Test" };
            dbContext.Customers.Add(customer);
            await dbContext.SaveChangesAsync();

            var reservation = new Reservation
            {
                ReferenceCode = "RES-5001",
                Status = ReservationStatuses.Confirmed,
                ProductType = "Flight",
                DepartureDate = new DateTime(2024, 10, 1),
                BasePrice = 1000m,
                Commission = 100m,
                TotalAmount = 1100m,
                CustomerId = customer.Id
            };
            dbContext.Reservations.Add(reservation);

            var receipt = new TreasuryReceipt
            {
                Reference = "RC-300",
                Method = "Cash",
                Currency = Currency.USD,
                Amount = 100m
            };
            dbContext.TreasuryReceipts.Add(receipt);

            await dbContext.SaveChangesAsync();
            reservationId = reservation.Id;
            receiptId = receipt.Id;
        }

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/treasury/receipts/{receiptId}/applications",
            new ApplyTreasuryReceiptRequest(reservationId, 200m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
