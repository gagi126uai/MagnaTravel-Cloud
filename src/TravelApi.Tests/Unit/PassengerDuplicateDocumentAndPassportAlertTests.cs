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
/// Plan 2026-07-31 (tarde), TANDA B — dos obras que comparten el mismo ReservaService REAL (con
/// AutoMapper real, no mockeado, para poder verificar los campos calculados del DTO):
/// <list type="bullet">
///   <item><b>B3</b>: freno de documento duplicado DENTRO de una misma reserva
///     (<see cref="TravelApi.Domain.Reservations.PassengerDuplicateDocumentGuard"/>).</item>
///   <item><b>B8/D2</b>: el semaforo de pasaporte vs las fechas del viaje llega calculado en el DTO,
///     tanto al guardar como al listar pasajeros (<c>PassportAlertLevel</c>/<c>PassportAlertText</c>).</item>
/// </list>
/// </summary>
public class PassengerDuplicateDocumentAndPassportAlertTests
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
        settings.Setup(s => s.GetEntityAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new TravelApi.Domain.Entities.OperationalFinanceSettings());
        return new ReservaService(
            context, NewMapper(), settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    private static Reserva ReservaSinFechas(int adults = 5)
        => new()
        {
            Id = 1, NumeroReserva = "F-1", Name = "Test",
            Status = EstadoReserva.Budget,
            AdultCount = adults, ChildCount = 0, InfantCount = 0
        };

    // ===================================================================================================
    // B3 — Freno de documento duplicado DENTRO de la misma reserva
    // ===================================================================================================

    [Fact]
    public async Task AddPassenger_MismoTipoYNumeroQueUnoYaCargado_Rechaza()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaSinFechas());
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Juan Perez", DocumentType = "DNI", DocumentNumber = "11222333" });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddPassengerAsync(
            reservaId: 1,
            new Passenger { FullName = "Otro Nombre", DocumentType = "DNI", DocumentNumber = "11222333" }));

        Assert.Contains("Juan Perez", ex.Message);
        Assert.Equal(1, await ctx.Passengers.CountAsync()); // el segundo NO se persistio
    }

    [Fact]
    public async Task AddPassenger_MismoNumero_TipoDesconocidoDeUnLado_EsSospechosoIgual()
    {
        // El plan lo pide explicito: "mismo numero + tipo desconocido = sospechoso igual". El pasajero ya
        // cargado no tiene tipo (legacy); el nuevo trae DNI con el mismo numero -> se frena igual, no se
        // deja pasar solo porque un lado no declaro el tipo.
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaSinFechas());
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Sin Tipo Cargado", DocumentType = null, DocumentNumber = "40111222" });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddPassengerAsync(
            reservaId: 1,
            new Passenger { FullName = "Nuevo Pasajero", DocumentType = "DNI", DocumentNumber = "40111222" }));
    }

    [Fact]
    public async Task AddPassenger_MismoNumero_TiposConocidosYDistintos_NoSeConsideraDuplicado()
    {
        // Dos tipos de documento DISTINTOS y AMBOS conocidos con el mismo numero: coincidencia posible
        // (aunque rara) de dos personas distintas, no se frena. El numero es puramente numerico para que
        // sea valido tanto como DNI (solo digitos) como Pasaporte (texto libre admite digitos).
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaSinFechas());
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Uno", DocumentType = "Pasaporte", DocumentNumber = "40111222" });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger { FullName = "Dos", DocumentType = "DNI", DocumentNumber = "40111222" });

        Assert.NotNull(dto);
        Assert.Equal(2, await ctx.Passengers.CountAsync());
    }

    [Fact]
    public async Task AddPassenger_NumerosDistintos_NoSeConsideraDuplicado()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaSinFechas());
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Uno", DocumentType = "DNI", DocumentNumber = "11111111" });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger { FullName = "Dos", DocumentType = "DNI", DocumentNumber = "22222222" });

        Assert.NotNull(dto);
    }

    [Fact]
    public async Task AddPassenger_MismoNumeroConEspaciosYMinusculas_SeDetectaIgual()
    {
        // B5 de esta misma obra: las comparaciones de identidad normalizan espacios/mayusculas.
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaSinFechas());
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Juan Perez", DocumentType = "dni", DocumentNumber = "11222333" });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddPassengerAsync(
            reservaId: 1,
            new Passenger { FullName = "Otro", DocumentType = "DNI", DocumentNumber = "  11222333  " }));
    }

    [Fact]
    public async Task UpdatePassenger_CambiarDocumentoAUnoQueYaTieneOtroPasajero_Rechaza()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaSinFechas());
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Titular", DocumentType = "DNI", DocumentNumber = "11111111" });
        ctx.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "Acompañante", DocumentType = "DNI", DocumentNumber = "22222222" });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var service = NewReservaService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdatePassengerAsync(
            passengerId: 2,
            new Passenger { Id = 2, FullName = "Acompañante", DocumentType = "DNI", DocumentNumber = "11111111" }));

        Assert.Contains("Titular", ex.Message);
    }

    [Fact]
    public async Task UpdatePassenger_SinCambiarElDocumento_NoDisparaElGuardContraSiMismo()
    {
        // Editar OTRO campo (ej. el nombre) sin tocar el documento no debe comparar contra si mismo y
        // fallar "duplicado".
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaSinFechas());
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Juan", DocumentType = "DNI", DocumentNumber = "11111111" });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var service = NewReservaService(ctx);

        var dto = await service.UpdatePassengerAsync(
            passengerId: 1,
            new Passenger { Id = 1, FullName = "Juan Carlos", DocumentType = "DNI", DocumentNumber = "11111111" });

        Assert.Equal("Juan Carlos", dto.FullName);
    }

    // ===================================================================================================
    // B8/D2 — Semaforo de pasaporte vs fechas del viaje, calculado en el DTO (T-13)
    // ===================================================================================================

    [Fact]
    public async Task AddPassenger_PasaporteVenceAntesDelFinDelViaje_DtoTraeNivelRojoYTextoDeViaje()
    {
        await using var ctx = NewContext();
        var reserva = ReservaSinFechas();
        reserva.StartDate = DateTime.UtcNow.Date.AddDays(10);
        reserva.EndDate = DateTime.UtcNow.Date.AddDays(20);
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

        Assert.Equal("Expired", dto.PassportAlertLevel);
        Assert.Equal(TravelApi.Domain.Helpers.PassportExpiryRules.ExpiredBeforeTripEndWarning, dto.PassportAlertText);
        // El riel viejo del aviso (toast) usa el MISMO texto nuevo (plan B8, T-6).
        Assert.Equal(dto.PassportAlertText, dto.Warning);
    }

    [Fact]
    public async Task AddPassenger_PasaporteHolgado_DtoNoTraeNivel()
    {
        await using var ctx = NewContext();
        var reserva = ReservaSinFechas();
        reserva.StartDate = DateTime.UtcNow.Date.AddDays(10);
        reserva.EndDate = DateTime.UtcNow.Date.AddDays(20);
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Viajero",
                PassportExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(3), DateTimeKind.Utc),
            });

        Assert.Null(dto.PassportAlertLevel);
        Assert.Null(dto.PassportAlertText);
        Assert.Null(dto.Warning);
    }

    [Fact]
    public async Task GetPassengers_RecalculaElSemaforoEnCadaLectura_NoSoloAlGuardar()
    {
        // El chip fijo de la fila (F11) se pinta desde el LISTADO, no solo al guardar (T-13). Este test
        // prueba justo eso: cargar un pasajero SIN fechas de viaje (sin aviso), despues la reserva
        // DECLARA fechas, y el listado debe mostrar el aviso sin haber vuelto a guardar el pasajero.
        await using var ctx = NewContext();
        var reserva = ReservaSinFechas();
        ctx.Reservas.Add(reserva);
        ctx.Passengers.Add(new Passenger
        {
            Id = 1, ReservaId = 1, FullName = "Viajero",
            PassportExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(15), DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var antes = (await service.GetPassengersAsync(1)).Single();
        Assert.Null(antes.PassportAlertLevel); // sin fechas de viaje todavia, y el pasaporte no esta vencido HOY

        reserva.StartDate = DateTime.UtcNow.Date.AddDays(10);
        reserva.EndDate = DateTime.UtcNow.Date.AddDays(20);
        await ctx.SaveChangesAsync();

        var despues = (await service.GetPassengersAsync(1)).Single();
        Assert.Equal("Expired", despues.PassportAlertLevel);
    }
}
