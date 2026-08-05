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
/// Obra "gate ámbito" (decisión firmada del dueño, 2026-08-05): tests de CABLEADO (ReservaService real +
/// AutoMapper real + InMemory), espejo de <see cref="DniExpiryAlertWiringTests"/>. Las reglas PURAS ya
/// están cubiertas en <see cref="PassportAlertScopeGateTests"/> y
/// <see cref="MinorTravelAuthorizationRulesTests"/>; acá se prueba el plomero: que ReservaService arma
/// bien el ámbito compartido a partir de Servicios/FlightSegments y lo reparte a los dos avisos (PARTE 1:
/// gate de pasaporte; PARTE 3: chip de menores).
/// </summary>
public class PassportScopeGateAndMinorAlertWiringTests
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

    private static ReservaService NewReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        return new ReservaService(
            context, NewMapper(), settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    private static Reserva ReservaConFechas(int adults = 5)
        => new()
        {
            Id = 1, NumeroReserva = "F-1", Name = "Test",
            Status = EstadoReserva.Budget,
            AdultCount = adults, ChildCount = 0, InfantCount = 0,
            StartDate = DateTime.UtcNow.Date.AddDays(10),
            EndDate = DateTime.UtcNow.Date.AddDays(20),
        };

    private static void AddServicioConAmbito(Reserva reserva, ServiceGeographicScope scope)
    {
        reserva.Servicios.Add(new ServicioReserva
        {
            ReservaId = reserva.Id,
            DepartureDate = reserva.StartDate!.Value,
            GeographicScope = scope,
        });
    }

    /// <summary>
    /// Fix B2 del review de backend (2026-08-05): variante con Status explicito, para probar que el gate
    /// de pasaporte y el chip de menores IGNORAN los servicios CANCELADOS (mismo filtro "vivo" que
    /// <see cref="TravelApi.Infrastructure.Services.UpcomingStartCalculator"/>).
    /// </summary>
    private static void AddServicioConAmbitoYEstado(Reserva reserva, ServiceGeographicScope scope, string status)
    {
        reserva.Servicios.Add(new ServicioReserva
        {
            ReservaId = reserva.Id,
            DepartureDate = reserva.StartDate!.Value,
            GeographicScope = scope,
            Status = status,
        });
    }

    // ===================================================================================================
    // PARTE 1 — Gate del chip de pasaporte por ámbito del servicio
    // ===================================================================================================

    [Fact]
    public async Task ReservaTodoNacionalConAmbitoDefinido_ApagaElAvisoDePasaporte_AunqueEsteVencido()
    {
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbito(reserva, ServiceGeographicScope.Domestic);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                PassportExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc), // vence EN MEDIO del viaje
            });

        Assert.Null(dto.PassportAlertLevel);
        Assert.Null(dto.PassportAlertText);
        Assert.Null(dto.Warning);
    }

    [Fact]
    public async Task ReservaConTramoInternacional_AvisaDePasaporte()
    {
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbito(reserva, ServiceGeographicScope.International);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                PassportExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
            });

        Assert.Equal("Expired", dto.PassportAlertLevel);
    }

    [Fact]
    public async Task ReservaConServicioSinDefinirElAmbito_SigueAvisandoDePasaporte_ReglaConservadora()
    {
        // Aunque el resto de los servicios sea Nacional, un solo servicio SIN dato de ámbito mantiene
        // el aviso prendido (decisión firmada: la falta de dato nunca apaga un aviso que hoy existe).
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbito(reserva, ServiceGeographicScope.Domestic);
        AddServicioConAmbito(reserva, ServiceGeographicScope.Undefined);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                PassportExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
            });

        Assert.Equal("Expired", dto.PassportAlertLevel);
    }

    [Fact]
    public async Task ReservaMixtaNacionalEInternacional_AvisaDePasaporte()
    {
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbito(reserva, ServiceGeographicScope.Domestic);
        AddServicioConAmbito(reserva, ServiceGeographicScope.International);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                PassportExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
            });

        Assert.Equal("Expired", dto.PassportAlertLevel);
    }

    [Fact]
    public async Task GetPassengers_RecalculaElGateDePasaporteEnCadaLectura_NoSoloAlGuardar()
    {
        // Se carga el pasajero con un tramo Internacional (aviso prendido) y despues TODOS los servicios
        // pasan a Nacional; el listado tiene que apagar el aviso sin haber vuelto a guardar el pasajero.
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbito(reserva, ServiceGeographicScope.International);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                PassportExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
            });

        var antes = (await service.GetPassengersAsync(1)).Single();
        Assert.Equal("Expired", antes.PassportAlertLevel);

        var servicio = await ctx.Servicios.FirstAsync(s => s.ReservaId == 1);
        servicio.GeographicScope = ServiceGeographicScope.Domestic;
        await ctx.SaveChangesAsync();

        var despues = (await service.GetPassengersAsync(1)).Single();
        Assert.Null(despues.PassportAlertLevel);
    }

    [Fact]
    public async Task ReservaSinNingunServicioCargado_SigueAvisandoDePasaporte_ComportamientoHistorico()
    {
        // Sin servicios en absoluto (reserva recien creada): el aviso de pasaporte no se toca, sigue
        // funcionando exactamente como antes de esta obra.
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                PassportExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
            });

        Assert.Equal("Expired", dto.PassportAlertLevel);
    }

    [Fact]
    public async Task ReservaConUnicoVueloRealNacional_ApagaElAvisoDePasaporte_MismoGateQueElServicioGenerico()
    {
        // Test (3) pedido por el reviewer: el gate de pasaporte tiene que mirar TAMBIEN FlightSegments
        // (el camino real de la ficha de vuelo), no solo el servicio generico — mismo fix 2026-08-03 del
        // semaforo de DNI, ahora probado a nivel cableado para el gate de pasaporte.
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        reserva.FlightSegments.Add(new FlightSegment
        {
            ReservaId = reserva.Id,
            DepartureTime = reserva.StartDate!.Value,
            GeographicScope = ServiceGeographicScope.Domestic,
        });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                PassportExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
            });

        Assert.Null(dto.PassportAlertLevel);
    }

    [Fact]
    public async Task ReservaConUnicoServicioNacionalCancelado_SigueAvisandoDePasaporte_FixB2()
    {
        // Fix B2 del review: un servicio Nacional CANCELADO no cuenta para el gate (mismo criterio F-2
        // que UpcomingStartCalculator). Con el unico servicio cancelado, la reserva queda "sin servicio
        // vivo con ambito" -> el gate se comporta como reserva sin servicios -> sigue avisando
        // (conservador). Antes del fix, este caso APAGABA el aviso (anti-conservador).
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbitoYEstado(reserva, ServiceGeographicScope.Domestic, ReservationStatuses.Cancelled);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                PassportExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
            });

        Assert.Equal("Expired", dto.PassportAlertLevel);
    }

    // ===================================================================================================
    // PARTE 3 — Chip de menores en tramo internacional
    // ===================================================================================================

    [Fact]
    public async Task MenorDeEdadConTramoInternacional_DtoTraeChipDeMenores()
    {
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbito(reserva, ServiceGeographicScope.International);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Menor Viajero",
                BirthDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(-10), DateTimeKind.Utc), // 10 años
            });

        Assert.Equal("Notice", dto.MinorAlertLevel);
        Assert.Equal(
            TravelApi.Domain.Helpers.MinorTravelAuthorizationRules.RequiresExitAuthorizationCheckWarning,
            dto.MinorAlertText);
    }

    [Fact]
    public async Task MenorDeEdadSoloConTramoNacional_NoTraeChipDeMenores()
    {
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbito(reserva, ServiceGeographicScope.Domestic);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Menor Viajero",
                BirthDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(-10), DateTimeKind.Utc),
            });

        Assert.Null(dto.MinorAlertLevel);
        Assert.Null(dto.MinorAlertText);
    }

    [Fact]
    public async Task MenorDeEdadConServicioSinDefinirElAmbito_NoTraeChipDeMenores_ADiferenciaDelPasaporte()
    {
        // A diferencia del gate de pasaporte, el ámbito SinDefinir NO prende este chip nuevo (decisión
        // firmada: acá no había ningún aviso hoy, así que "sin dato" no inventa uno nuevo).
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbito(reserva, ServiceGeographicScope.Undefined);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Menor Viajero",
                BirthDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(-10), DateTimeKind.Utc),
            });

        Assert.Null(dto.MinorAlertLevel);
        Assert.Null(dto.MinorAlertText);
    }

    [Fact]
    public async Task MayorDeEdadConTramoInternacional_NoTraeChipDeMenores()
    {
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbito(reserva, ServiceGeographicScope.International);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Adulto Viajero",
                BirthDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(-30), DateTimeKind.Utc),
            });

        Assert.Null(dto.MinorAlertLevel);
    }

    [Fact]
    public async Task PasajeroSinFechaDeNacimiento_NoTraeChipDeMenores_SilencioTotal()
    {
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbito(reserva, ServiceGeographicScope.International);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger { FullName = "Sin Fecha" });

        Assert.Null(dto.MinorAlertLevel);
    }

    [Fact]
    public async Task GetPassengers_RecalculaElChipDeMenoresEnCadaLectura_NoSoloAlGuardar()
    {
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbito(reserva, ServiceGeographicScope.Domestic);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Menor Viajero",
                BirthDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(-10), DateTimeKind.Utc),
            });

        var antes = (await service.GetPassengersAsync(1)).Single();
        Assert.Null(antes.MinorAlertLevel); // todavia sin tramo Internacional

        var servicio = await ctx.Servicios.FirstAsync(s => s.ReservaId == 1);
        servicio.GeographicScope = ServiceGeographicScope.International;
        await ctx.SaveChangesAsync();

        var despues = (await service.GetPassengersAsync(1)).Single();
        Assert.Equal("Notice", despues.MinorAlertLevel);
    }

    [Fact]
    public async Task TramoInternacionalCargadoComoVueloReal_TambienPrendeElChipDeMenores()
    {
        // Mismo fix 2026-08-03 del semaforo de DNI: el ambito tambien se carga desde FlightSegments (el
        // camino real de la ficha de vuelo), no solo desde el servicio generico.
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        reserva.FlightSegments.Add(new FlightSegment
        {
            ReservaId = reserva.Id,
            DepartureTime = reserva.StartDate!.Value,
            GeographicScope = ServiceGeographicScope.International,
        });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Menor Viajero",
                BirthDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(-10), DateTimeKind.Utc),
            });

        Assert.Equal("Notice", dto.MinorAlertLevel);
    }

    [Fact]
    public async Task MenorConFechaDeNacimientoYReservaSinFechasDeViaje_DtoTraeChipDeMenores()
    {
        // Test (4) pedido por el reviewer: a nivel CABLEADO (no solo en la regla pura), un menor en una
        // reserva sin StartDate/EndDate cargados (fallback a "hoy", ver MinorTravelAuthorizationRules)
        // igual tiene que traer el chip si hay un tramo Internacional.
        await using var ctx = NewContext();
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "Test",
            Status = EstadoReserva.Budget,
            AdultCount = 0, ChildCount = 1, InfantCount = 0,
            // Sin StartDate/EndDate a proposito.
        };
        reserva.Servicios.Add(new ServicioReserva
        {
            ReservaId = reserva.Id,
            DepartureDate = DateTime.UtcNow.Date.AddDays(10),
            GeographicScope = ServiceGeographicScope.International,
        });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Menor Sin Fechas De Viaje",
                BirthDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(-10), DateTimeKind.Utc), // 10 años
            });

        Assert.Equal("Notice", dto.MinorAlertLevel);
    }

    [Fact]
    public async Task ReservaConUnicoServicioInternacionalCancelado_NoTraeChipDeMenores_FixB2()
    {
        // Fix B2 del review: un servicio Internacional CANCELADO no cuenta para el chip de menores (mismo
        // criterio F-2 que UpcomingStartCalculator). Antes del fix, este caso dejaba el chip prendido
        // "para siempre" aunque el unico tramo internacional ya no exista.
        await using var ctx = NewContext();
        var reserva = ReservaConFechas();
        AddServicioConAmbitoYEstado(reserva, ServiceGeographicScope.International, ReservationStatuses.Cancelled);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Menor Viajero",
                BirthDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(-10), DateTimeKind.Utc),
            });

        Assert.Null(dto.MinorAlertLevel);
    }
}
