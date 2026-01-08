using System.Net;
using System.Net.Http.Json;
using TravelApi.Contracts.Reservations;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

public class ReservationsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ReservationsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateReservation_CreatesDraftReservation()
    {
        int customerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Customers.RemoveRange(dbContext.Customers);
            await dbContext.SaveChangesAsync();

            var customer = new Customer { FullName = "Cliente Test" };
            dbContext.Customers.Add(customer);
            await dbContext.SaveChangesAsync();
            customerId = customer.Id;
        }

        var client = _factory.CreateClient();
        var request = new CreateReservationRequest(
            "RES-1001",
            "Flight",
            new DateTime(2024, 8, 1),
            null,
            1000m,
            100m,
            1100m,
            "Proveedor",
            customerId);

        var response = await client.PostAsJsonAsync("/api/reservations", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<Reservation>();
        Assert.NotNull(payload);
        Assert.Equal(ReservationStatuses.Draft, payload!.Status);
    }

    [Fact]
    public async Task UpdateReservationStatus_AllowsDraftToConfirmed()
    {
        int reservationId;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Reservations.RemoveRange(dbContext.Reservations);
            dbContext.Customers.RemoveRange(dbContext.Customers);
            await dbContext.SaveChangesAsync();

            var customer = new Customer { FullName = "Cliente Test" };
            dbContext.Customers.Add(customer);
            await dbContext.SaveChangesAsync();

            var reservation = new Reservation
            {
                ReferenceCode = "RES-2001",
                Status = ReservationStatuses.Draft,
                ProductType = "Flight",
                DepartureDate = new DateTime(2024, 8, 1),
                BasePrice = 900m,
                Commission = 90m,
                TotalAmount = 990m,
                CustomerId = customer.Id
            };

            dbContext.Reservations.Add(reservation);
            await dbContext.SaveChangesAsync();
            reservationId = reservation.Id;
        }

        var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync(
            $"/api/reservations/{reservationId}/status",
            new UpdateReservationStatusRequest(ReservationStatuses.Confirmed));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<Reservation>();
        Assert.NotNull(payload);
        Assert.Equal(ReservationStatuses.Confirmed, payload!.Status);
    }

    [Fact]
    public async Task UpdateReservationStatus_ReturnsBadRequestForInvalidTransition()
    {
        int reservationId;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Reservations.RemoveRange(dbContext.Reservations);
            dbContext.Customers.RemoveRange(dbContext.Customers);
            await dbContext.SaveChangesAsync();

            var customer = new Customer { FullName = "Cliente Test" };
            dbContext.Customers.Add(customer);
            await dbContext.SaveChangesAsync();

            var reservation = new Reservation
            {
                ReferenceCode = "RES-3001",
                Status = ReservationStatuses.Draft,
                ProductType = "Flight",
                DepartureDate = new DateTime(2024, 8, 1),
                BasePrice = 900m,
                Commission = 90m,
                TotalAmount = 990m,
                CustomerId = customer.Id
            };

            dbContext.Reservations.Add(reservation);
            await dbContext.SaveChangesAsync();
            reservationId = reservation.Id;
        }

        var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync(
            $"/api/reservations/{reservationId}/status",
            new UpdateReservationStatusRequest(ReservationStatuses.Cancelled));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
