using System;
using System.Linq;
using System.Threading;
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
/// Semaforo de DNI vencido para cabotaje (decision firmada del dueño, 2026-08-03): tests de CABLEADO
/// (ReservaService real + AutoMapper real + InMemory), espejo de
/// <see cref="PassengerDuplicateDocumentAndPassportAlertTests"/>. La REGLA pura ya esta cubierta en
/// <see cref="DniExpiryRulesTests"/>; aca se prueba el plomero: la llave de agencia, la consulta de
/// "hay servicio Nacional" y que el DTO del servicio expone el ambito como STRING legible.
/// </summary>
public class DniExpiryAlertWiringTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IMapper NewMapper()
        => new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ReservaService NewReservaService(AppDbContext context, bool enableDomesticDniExpiryAlert = false)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings
                {
                    EnableDomesticDniExpiryAlert = enableDomesticDniExpiryAlert
                });
        return new ReservaService(
            context, NewMapper(), settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    private static Reserva ReservaConFechasYServicioNacional(bool domestic, int adults = 5)
    {
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "Test",
            Status = EstadoReserva.Budget,
            AdultCount = adults, ChildCount = 0, InfantCount = 0,
            StartDate = DateTime.UtcNow.Date.AddDays(10),
            EndDate = DateTime.UtcNow.Date.AddDays(20),
        };
        reserva.Servicios.Add(new ServicioReserva
        {
            ReservaId = 1,
            DepartureDate = reserva.StartDate.Value,
            GeographicScope = domestic ? ServiceGeographicScope.Domestic : ServiceGeographicScope.International,
        });
        return reserva;
    }

    /// <summary>
    /// Fix 2026-08-03: espejo de <see cref="ReservaConFechasYServicioNacional"/> pero con un VUELO REAL
    /// (<see cref="FlightSegment"/>) en vez del servicio generico — el camino que usa de verdad la
    /// ficha de vuelo. Antes de este fix <c>ResolveDniAlertContextAsync</c> solo miraba <c>Servicios</c>
    /// y el semaforo jamas se prendia por un vuelo cargado como Nacional.
    /// </summary>
    private static Reserva ReservaConFechasYVueloNacional(bool domestic, int adults = 5)
    {
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "Test",
            Status = EstadoReserva.Budget,
            AdultCount = adults, ChildCount = 0, InfantCount = 0,
            StartDate = DateTime.UtcNow.Date.AddDays(10),
            EndDate = DateTime.UtcNow.Date.AddDays(20),
        };
        reserva.FlightSegments.Add(new FlightSegment
        {
            ReservaId = 1,
            DepartureTime = reserva.StartDate.Value,
            GeographicScope = domestic ? ServiceGeographicScope.Domestic : ServiceGeographicScope.International,
        });
        return reserva;
    }

    [Fact]
    public async Task LlaveApagada_DniVencidoConServicioNacional_DtoNoTraeAviso()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaConFechasYServicioNacional(domestic: true));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx, enableDomesticDniExpiryAlert: false);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                DocumentType = "DNI",
                DocumentNumber = "30111222",
                DocumentExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
            });

        Assert.Null(dto.DniAlertLevel);
        Assert.Null(dto.DniAlertText);
    }

    [Fact]
    public async Task LlavePrendida_DniVencidoConServicioNacional_DtoTraeAvisoRojo()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaConFechasYServicioNacional(domestic: true));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx, enableDomesticDniExpiryAlert: true);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                DocumentType = "DNI",
                DocumentNumber = "30111222",
                DocumentExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc), // vence EN MEDIO del viaje
            });

        Assert.Equal("Expired", dto.DniAlertLevel);
        Assert.Equal(TravelApi.Domain.Helpers.DniExpiryRules.ExpiredBeforeTripEndWarning, dto.DniAlertText);
    }

    [Fact]
    public async Task LlavePrendida_SinServicioNacionalEnLaReserva_DtoNoTraeAviso()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaConFechasYServicioNacional(domestic: false)); // solo Internacional
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx, enableDomesticDniExpiryAlert: true);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                DocumentType = "DNI",
                DocumentNumber = "30111222",
                DocumentExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
            });

        Assert.Null(dto.DniAlertLevel);
        Assert.Null(dto.DniAlertText);
    }

    [Fact]
    public async Task LlavePrendida_TipoDeDocumentoNoDni_DtoNoTraeAviso()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaConFechasYServicioNacional(domestic: true));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx, enableDomesticDniExpiryAlert: true);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                DocumentType = "Pasaporte",
                DocumentNumber = "AB123456",
                DocumentExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
            });

        Assert.Null(dto.DniAlertLevel);
        Assert.Null(dto.DniAlertText);
    }

    [Fact]
    public async Task GetPassengers_RecalculaElAvisoDeDniEnCadaLectura_NoSoloAlGuardar()
    {
        // Mismo espiritu que el test gemelo de pasaporte (T-13): el chip se pinta desde el LISTADO.
        // Se carga el pasajero SIN servicio Nacional todavia (sin aviso) y despues se agrega el tramo
        // Nacional; el listado tiene que mostrar el aviso sin haber vuelto a guardar el pasajero.
        await using var ctx = NewContext();
        var reserva = ReservaConFechasYServicioNacional(domestic: false);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx, enableDomesticDniExpiryAlert: true);

        var passengerDto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                DocumentType = "DNI",
                DocumentNumber = "30111222",
                DocumentExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
            });
        Assert.Null(passengerDto.DniAlertLevel); // todavia sin tramo Nacional

        var servicio = await ctx.Servicios.FirstAsync(s => s.ReservaId == 1);
        servicio.GeographicScope = ServiceGeographicScope.Domestic;
        await ctx.SaveChangesAsync();

        var despues = (await service.GetPassengersAsync(1)).Single();
        Assert.Equal("Expired", despues.DniAlertLevel);
    }

    [Fact]
    public async Task LlavePrendida_DniVencidoConVueloRealNacional_DtoTraeAvisoRojo()
    {
        // Fix 2026-08-03: mismo escenario que "LlavePrendida_DniVencidoConServicioNacional_DtoTraeAvisoRojo"
        // pero el ambito Nacional viene de un FlightSegment (el camino real de carga de un vuelo), no del
        // servicio generico. Antes de este fix este test daba null: el semaforo nunca miraba FlightSegments.
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaConFechasYVueloNacional(domestic: true));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx, enableDomesticDniExpiryAlert: true);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                DocumentType = "DNI",
                DocumentNumber = "30111222",
                DocumentExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc), // vence EN MEDIO del viaje
            });

        Assert.Equal("Expired", dto.DniAlertLevel);
        Assert.Equal(TravelApi.Domain.Helpers.DniExpiryRules.ExpiredBeforeTripEndWarning, dto.DniAlertText);
    }

    [Fact]
    public async Task LlavePrendida_SoloVueloInternacionalSinServicioNacional_DtoNoTraeAviso()
    {
        // Contraparte del test anterior: un vuelo Internacional NO prende el semaforo (solo importa
        // si HAY un vuelo/servicio Nacional en la reserva).
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaConFechasYVueloNacional(domestic: false));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx, enableDomesticDniExpiryAlert: true);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                DocumentType = "DNI",
                DocumentNumber = "30111222",
                DocumentExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
            });

        Assert.Null(dto.DniAlertLevel);
        Assert.Null(dto.DniAlertText);
    }

    [Fact]
    public void ServicioReservaDto_GeographicScope_ViajaComoStringLegible_NuncaElEnteroDelEnum()
    {
        var mapper = NewMapper();

        var nacional = mapper.Map<TravelApi.Application.DTOs.ServicioReservaDto>(
            new ServicioReserva { GeographicScope = ServiceGeographicScope.Domestic });
        var internacional = mapper.Map<TravelApi.Application.DTOs.ServicioReservaDto>(
            new ServicioReserva { GeographicScope = ServiceGeographicScope.International });
        var sinDefinir = mapper.Map<TravelApi.Application.DTOs.ServicioReservaDto>(
            new ServicioReserva { GeographicScope = ServiceGeographicScope.Undefined });

        Assert.Equal("Nacional", nacional.GeographicScope);
        Assert.Equal("Internacional", internacional.GeographicScope);
        Assert.Null(sinDefinir.GeographicScope);
    }
}
