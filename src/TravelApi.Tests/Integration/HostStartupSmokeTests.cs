using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Red de seguridad PRE-refactor C17 (eliminar TravelReservations.Api).
///
/// Verifica que el host de TravelApi:
///  - Levanta sin errores con el bypass de auth para tests.
///  - Expone /health 200.
///  - Resuelve via DI los 6 servicios de reservas que hoy se registran de
///    forma condicional (HttpProxy vs in-process). Sin Services:Reservations:BaseUrl
///    seteado, Program.cs cae en el else y registra las implementaciones reales.
///    Tras el refactor C17 esa branch desaparece, pero las implementaciones
///    concretas deben seguir resolviendo igual.
///  - GET /api/reservas autenticado responde 200.
///  - POST /api/reservas crea una reserva en estado Budget y se persiste.
///
/// Los tests comparten la misma factory via IClassFixture para amortizar el
/// costo de boot del host (~30-50s en frio por la inicializacion de Hangfire).
/// La InMemory DB es compartida en consecuencia, asi que los asserts de cantidad
/// se hacen sobre conteos relativos (delta) y se ordena la ejecucion para que
/// los tests que crean filas sean independientes entre si en lo que asertan.
/// </summary>
public class HostStartupSmokeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public HostStartupSmokeTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_RespondsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("ok", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReservationServices_ResolveFromContainer_InProcessMode()
    {
        // Validacion explicita de la rama in-process del registro condicional.
        // Si el refactor C17 rompe alguna registracion, este test cae.
        using var scope = _factory.Services.CreateScope();

        var reservaService = scope.ServiceProvider.GetRequiredService<IReservaService>();
        var paymentService = scope.ServiceProvider.GetRequiredService<IPaymentService>();
        var bookingService = scope.ServiceProvider.GetRequiredService<IBookingService>();
        var voucherService = scope.ServiceProvider.GetRequiredService<IVoucherService>();
        var attachmentService = scope.ServiceProvider.GetRequiredService<IAttachmentService>();
        var timelineService = scope.ServiceProvider.GetRequiredService<ITimelineService>();

        // Pin: hoy (sin Services:Reservations:BaseUrl) las implementaciones
        // deben ser las concretas, no los HttpProxy. Tras el refactor C17 esto
        // sigue siendo cierto (no hay otra implementacion).
        Assert.IsType<ReservaService>(reservaService);
        Assert.IsType<PaymentService>(paymentService);
        Assert.IsType<BookingService>(bookingService);
        Assert.IsType<VoucherService>(voucherService);
        Assert.IsType<AttachmentService>(attachmentService);
        Assert.IsType<TimelineService>(timelineService);
    }

    [Fact]
    public async Task GetReservas_Authenticated_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/reservas");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        // ReservaListPageDto es un objeto. No fijamos shape exacta para no
        // acoplarnos a un DTO que puede evolucionar; chequeamos que sea JSON
        // valido con un objeto raiz.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task PostReserva_CreatesBudgetReservation_AndPersistsToDb()
    {
        var client = _factory.CreateClient();

        // Sembrar el customer pagador (PayerId espera un PublicId GUID).
        var customerPublicId = Guid.NewGuid();
        var reservaName = "Smoke trip " + Guid.NewGuid().ToString("N")[..8];

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Customers.Add(new Customer
            {
                PublicId = customerPublicId,
                FullName = "Cliente smoke " + customerPublicId.ToString("N")[..6]
            });
            await db.SaveChangesAsync();
        }

        var request = new CreateReservaRequest
        {
            Name = reservaName,
            PayerId = customerPublicId.ToString(),
            StartDate = DateTime.UtcNow.AddDays(15),
            Description = "Created via smoke test"
        };

        var response = await client.PostAsJsonAsync("/api/reservas", request);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK,
            $"Esperado 201/200, recibido {response.StatusCode}. Body: {await response.Content.ReadAsStringAsync()}");

        // Validar persistencia desde el lado del DB. Buscamos por nombre unico
        // para no chocar con datos sembrados por otros tests que comparten la
        // misma factory (y por ende la misma InMemory DB).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reserva = await db.Reservas.FirstOrDefaultAsync(r => r.Name == reservaName);
            Assert.NotNull(reserva);
            Assert.Equal(EstadoReserva.Budget, reserva!.Status);
            Assert.StartsWith($"F-{DateTime.Now.Year}-", reserva.NumeroReserva);
        }
    }
}
