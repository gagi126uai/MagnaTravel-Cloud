using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Http;

/// <summary>
/// Regresion 2026-07-23: el endpoint POST /api/reservas/{id}/flights NUNCA tuvo un test HTTP end
/// to end (a diferencia de Hotel, que si tiene <c>HotelBookingsControllerNewCatalogProductValidationTests</c>).
/// El bug real (500 SIEMPRE en produccion) era la columna Postgres <c>FlightSegments.Status</c> en
/// <c>varchar(2)</c> — ver <see cref="Integration.FlightSegmentStatusColumnWidthIntegrationTests"/> para
/// el test que pinea ESO especificamente (necesita Postgres real; InMemory no aplica MaxLength).
///
/// <para>Este test complementa esa cobertura pineando el resto del pipeline (auth + model binding +
/// mapping + el resto del service) con una reserva en estado Presupuesto — el MISMO estado que
/// dispara <c>flight.Status = "Solicitado"</c> en <c>BookingService.CreateFlightAsync</c>. El
/// namespace NO contiene "Integration" a proposito: entra en la suite unit/InMemory rapida.</para>
/// </summary>
public class FlightSegmentsControllerCreateTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public FlightSegmentsControllerCreateTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Siembra una Reserva en Presupuesto (Budget) + un Supplier minimos. Presupuesto es el estado
    /// exacto que hace que el service fuerce el vuelo nuevo a "Solicitado" (ver
    /// ReservaCapacityRules.ShouldForceSolicitadoStatusAsync).
    /// </summary>
    private async Task<(Guid ReservaPublicId, Guid SupplierPublicId)> SeedReservaEnPresupuestoAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "Reserva presupuesto " + Guid.NewGuid().ToString("N")[..6],
            NumeroReserva = "F-PPTO-" + Guid.NewGuid().ToString("N")[..6],
            ResponsibleUserId = "test-user",
            Status = EstadoReserva.Budget,
        };
        var supplier = new Supplier
        {
            PublicId = Guid.NewGuid(),
            Name = "Aerolinea Test " + Guid.NewGuid().ToString("N")[..6],
        };
        db.Reservas.Add(reserva);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        return (reserva.PublicId, supplier.PublicId);
    }

    [Fact]
    public async Task POST_Flight_OnReservaEnPresupuesto_ReturnsOk_Not500()
    {
        var (reservaPublicId, supplierPublicId) = await SeedReservaEnPresupuestoAsync();

        // Admin default del TestAuthHandler (sin headers): bypass de permisos y ownership, igual
        // que el test analogo de Hotel — lo que esta bajo prueba es el pipeline completo del alta.
        var client = _factory.CreateClient();

        var request = new CreateFlightRequest(
            SupplierId: supplierPublicId.ToString(),
            AirlineCode: "AR",
            AirlineName: "Aerolineas Argentinas",
            FlightNumber: "1234",
            Origin: "AEP",
            OriginCity: "Buenos Aires",
            Destination: "IGR",
            DestinationCity: "Iguazu",
            DepartureTime: DateTime.UtcNow.Date.AddDays(15),
            ArrivalTime: DateTime.UtcNow.Date.AddDays(15).AddHours(2),
            CabinClass: "Economy",
            Baggage: "23kg",
            PNR: "ABC123",
            NetCost: 300m,
            SalePrice: 500m,
            Commission: 200m,
            Tax: 0m,
            Notes: null);

        var response = await client.PostAsJsonAsync($"/api/reservas/{reservaPublicId}/flights", request);

        // ASSERT CLAVE (el pin de la regresion): NO 500. Este endpoint jamas tuvo cobertura HTTP;
        // si algo del pipeline (binding, mapping, guards del service) vuelve a romper, este test
        // lo agarra antes de que llegue a produccion.
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<FlightSegmentDto>();
        Assert.NotNull(created);
        // En Presupuesto el vuelo nace "Solicitado" (WorkflowStatus derivado, no el codigo IATA crudo).
        Assert.Equal("Solicitado", created!.WorkflowStatus);
    }
}
