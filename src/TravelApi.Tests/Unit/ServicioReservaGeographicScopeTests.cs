using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Semaforo de DNI vencido para cabotaje (decision firmada del dueño, 2026-08-03): alta/edicion del
/// AMBITO GEOGRAFICO (<see cref="ServiceGeographicScope"/>) del servicio generico. El request lo manda
/// como texto legible ("Nacional"/"Internacional"); un texto vacio o no reconocido nunca corta el
/// alta/edicion (validacion SUAVE), solo no toca el campo.
/// </summary>
public class ServicioReservaGeographicScopeTests
{
    private static AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static ReservaService NewReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        var store = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);

        return new ReservaService(context, Mock.Of<IMapper>(), settings.Object, userManager, NullLogger<ReservaService>.Instance);
    }

    private static AddServiceRequest BuildRequest(string? geographicScope) => new(
        ServiceType: "Excursion",
        SupplierId: null,
        Description: "Excursion glaciar",
        ConfirmationNumber: null,
        DepartureDate: DateTime.UtcNow.AddDays(10),
        ReturnDate: null,
        SalePrice: 160m,
        NetCost: 100m,
        GeographicScope: geographicScope);

    [Fact]
    public async Task AddService_ConNacional_QuedaGuardadoComoDomestic()
    {
        await using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "Test" });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);
        var (created, _) = await service.AddServiceAsync(1, BuildRequest("Nacional"), CancellationToken.None);

        Assert.Equal(ServiceGeographicScope.Domestic, created.GeographicScope);
    }

    [Fact]
    public async Task AddService_ConInternacional_QuedaGuardadoComoInternational()
    {
        await using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "Test" });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);
        var (created, _) = await service.AddServiceAsync(1, BuildRequest("Internacional"), CancellationToken.None);

        Assert.Equal(ServiceGeographicScope.International, created.GeographicScope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cualquier-cosa-rara")]
    public async Task AddService_SinAmbitoOTextoNoReconocido_QuedaSinDefinir_NuncaCortaElAlta(string? geographicScope)
    {
        await using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "Test" });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);
        var (created, _) = await service.AddServiceAsync(1, BuildRequest(geographicScope), CancellationToken.None);

        Assert.Equal(ServiceGeographicScope.Undefined, created.GeographicScope);
    }

    [Fact]
    public async Task UpdateService_ConAmbitoNuevo_PisaElAnterior()
    {
        await using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "Test" });
        context.Servicios.Add(new ServicioReserva
        {
            Id = 10, ReservaId = 1, ServiceType = "Excursion", DepartureDate = DateTime.UtcNow.AddDays(10),
            SalePrice = 160m, NetCost = 100m, GeographicScope = ServiceGeographicScope.Domestic,
        });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);
        await service.UpdateServiceAsync(10, BuildRequest("Internacional"), CancellationToken.None);

        var stored = await context.Servicios.SingleAsync();
        Assert.Equal(ServiceGeographicScope.International, stored.GeographicScope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("texto-no-reconocido")]
    public async Task UpdateService_SinAmbitoOTextoNoReconocido_ConservaElAmbitoCargado_NuncaLoBorra(string? geographicScope)
    {
        // Anti-pisado (mismo criterio que OperatorPaymentDeadline): un form viejo que no manda el
        // ambito NO puede "volver a Sin definir" un servicio que ya lo tenia cargado.
        await using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "Test" });
        context.Servicios.Add(new ServicioReserva
        {
            Id = 10, ReservaId = 1, ServiceType = "Excursion", DepartureDate = DateTime.UtcNow.AddDays(10),
            SalePrice = 160m, NetCost = 100m, GeographicScope = ServiceGeographicScope.Domestic,
        });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);
        await service.UpdateServiceAsync(10, BuildRequest(geographicScope), CancellationToken.None);

        var stored = await context.Servicios.SingleAsync();
        Assert.Equal(ServiceGeographicScope.Domestic, stored.GeographicScope);
    }

    [Fact]
    public async Task UpdateService_ConTokenSinDefinir_VuelveASinDefinir()
    {
        // Fix del 2026-08-03: un vuelo/servicio marcado "Nacional" por error ahora SI puede volver
        // a "Sin definir" mandando el token ServiceGeographicScopeText.Cleared. Antes de este fix,
        // el unico texto que llegaba a mandar el front para "Sin definir" era vacio/null, que
        // ParseOrNull interpreta como "no toque el campo" (anti-pisado) — el aviso de DNI quedaba
        // prendido para siempre.
        await using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "Test" });
        context.Servicios.Add(new ServicioReserva
        {
            Id = 10, ReservaId = 1, ServiceType = "Excursion", DepartureDate = DateTime.UtcNow.AddDays(10),
            SalePrice = 160m, NetCost = 100m, GeographicScope = ServiceGeographicScope.Domestic,
        });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);
        await service.UpdateServiceAsync(10, BuildRequest(ServiceGeographicScopeText.Cleared), CancellationToken.None);

        var stored = await context.Servicios.SingleAsync();
        Assert.Equal(ServiceGeographicScope.Undefined, stored.GeographicScope);
    }
}
