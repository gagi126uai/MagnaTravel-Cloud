using System;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Firmado 16/08 (decision del dueño, punto a/b): <c>ReservaDto.VentaPorMoneda</c> -- total del viaje y
/// "por persona" (sobre los pasajeros DECLARADOS), UNA linea por moneda, calculado en
/// <c>ReservaService.GetReservaByIdAsync</c>. Cubre: moneda unica, dos monedas sin mezclarse (regla P-3),
/// declarados=0 -> porPersona null, y el mismo redondeo comercial (AwayFromZero) que usa el PDF de
/// presupuesto.
/// </summary>
public class ReservaVentaPorMonedaTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IMapper CreateMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ReservaService CreateReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        return new ReservaService(
            context, CreateMapper(), settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: null);
    }

    [Fact]
    public async Task MonedaUnica_ConDeclarados_DivideElTotalPorLosDeclarados()
    {
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            NumeroReserva = $"F-VPM-{Guid.NewGuid():N}"[..14],
            Name = "Reserva ARS",
            Status = EstadoReserva.Budget,
            AdultCount = 2,
            ChildCount = 0,
            InfantCount = 0,
        };
        reserva.Servicios.Add(new ServicioReserva { SalePrice = 1000m, Currency = "ARS" });
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context);
        var dto = await service.GetReservaByIdAsync(reserva.Id);

        var linea = Assert.Single(dto.VentaPorMoneda);
        Assert.Equal("ARS", linea.Currency);
        Assert.Equal(1000m, linea.Total);
        Assert.Equal(500m, linea.PerPerson);
    }

    [Fact]
    public async Task DosMonedas_CadaUnaConSuPropioTotalYPorPersona_NuncaSeMezclan()
    {
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            NumeroReserva = $"F-VPM-{Guid.NewGuid():N}"[..14],
            Name = "Reserva multimoneda",
            Status = EstadoReserva.Budget,
            AdultCount = 2,
            ChildCount = 0,
            InfantCount = 0,
        };
        reserva.Servicios.Add(new ServicioReserva { SalePrice = 1000m, Currency = "ARS" });
        reserva.Servicios.Add(new ServicioReserva { SalePrice = 500m, Currency = "USD" });
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context);
        var dto = await service.GetReservaByIdAsync(reserva.Id);

        Assert.Equal(2, dto.VentaPorMoneda.Count);

        var lineaArs = dto.VentaPorMoneda.Single(l => l.Currency == "ARS");
        Assert.Equal(1000m, lineaArs.Total);
        Assert.Equal(500m, lineaArs.PerPerson);

        var lineaUsd = dto.VentaPorMoneda.Single(l => l.Currency == "USD");
        Assert.Equal(500m, lineaUsd.Total);
        Assert.Equal(250m, lineaUsd.PerPerson);
    }

    [Fact]
    public async Task SinPasajerosDeclarados_PorPersonaQuedaNull_NoDivideEntreCero()
    {
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            NumeroReserva = $"F-VPM-{Guid.NewGuid():N}"[..14],
            Name = "Reserva sin declarados",
            Status = EstadoReserva.Budget,
            AdultCount = 0,
            ChildCount = 0,
            InfantCount = 0,
        };
        reserva.Servicios.Add(new ServicioReserva { SalePrice = 1000m, Currency = "ARS" });
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context);
        var dto = await service.GetReservaByIdAsync(reserva.Id);

        var linea = Assert.Single(dto.VentaPorMoneda);
        Assert.Equal(1000m, linea.Total);
        Assert.Null(linea.PerPerson);
    }

    [Fact]
    public async Task ServicioCancelado_NoEntraEnElTotal()
    {
        // Mejora pedida por el reviewer (16/08): el XML-doc promete "venta de los servicios
        // VIVOS" — este test lo cubre directo: un servicio cancelado no suma a VentaPorMoneda.
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            NumeroReserva = $"F-VPM-{Guid.NewGuid():N}"[..14],
            Name = "Reserva con cancelado",
            Status = EstadoReserva.Budget,
            AdultCount = 2,
            ChildCount = 0,
            InfantCount = 0,
        };
        reserva.Servicios.Add(new ServicioReserva { SalePrice = 1000m, Currency = "ARS" });
        reserva.Servicios.Add(new ServicioReserva { SalePrice = 400m, Currency = "ARS", Status = WorkflowStatuses.Cancelado });
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context);
        var dto = await service.GetReservaByIdAsync(reserva.Id);

        var linea = Assert.Single(dto.VentaPorMoneda);
        Assert.Equal(1000m, linea.Total);
        Assert.Equal(500m, linea.PerPerson);
    }

    [Fact]
    public async Task Redondeo_MidpointVaParaArriba_MismoCriterioQueElPdf()
    {
        // 3 / 8 = 0.375 -> AwayFromZero redondea el tercer decimal (5) para afuera: 0.38.
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            NumeroReserva = $"F-VPM-{Guid.NewGuid():N}"[..14],
            Name = "Reserva redondeo",
            Status = EstadoReserva.Budget,
            AdultCount = 8,
            ChildCount = 0,
            InfantCount = 0,
        };
        reserva.Servicios.Add(new ServicioReserva { SalePrice = 3m, Currency = "ARS" });
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context);
        var dto = await service.GetReservaByIdAsync(reserva.Id);

        var linea = Assert.Single(dto.VentaPorMoneda);
        Assert.Equal(0.38m, linea.PerPerson);
    }
}
