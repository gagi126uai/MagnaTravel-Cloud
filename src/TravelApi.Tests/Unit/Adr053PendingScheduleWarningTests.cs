using System.Collections.Generic;
using System.Security.Claims;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-053 D2.1 (2026-08-13): ciclo de vida del aviso suave efímero <c>ReservaDto.ScheduleWarning</c>
/// (persistencia <c>PendingScheduleWarning</c>/<c>PendingScheduleWarningByUserId</c>, consumo-al-leer en
/// <c>GetReservaByIdAsync</c>). Cubre: consumo por el mismo actor, NO-consumo por otro usuario, NO-consumo
/// por un Admin distinto del autor (B6 — ya no hay excepción de alcance por rol), y "actor null = sin
/// aviso" (B7 — el job de reparación nunca deja pendiente).
/// </summary>
public class Adr053PendingScheduleWarningTests
{
    private const string Author = "vendedor-autor";
    private const string OtherUser = "vendedor-otro";

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

    private static ReservaService CreateReservaService(AppDbContext context, string? callerUserId)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        IHttpContextAccessor? accessor = null;
        if (callerUserId != null)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, callerUserId) };
            accessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
            };
        }

        return new ReservaService(
            context, CreateMapper(), settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: accessor);
    }

    /// <summary>Siembra una reserva CON un pendiente escrito por <see cref="Author"/>.</summary>
    private static async Task<int> SeedReservaConPendienteAsync(AppDbContext context)
    {
        var reserva = new Reserva
        {
            NumeroReserva = $"F-ADR053-PSW-{Guid.NewGuid():N}"[..14],
            Name = "Reserva con aviso pendiente",
            Status = EstadoReserva.InManagement,
            PendingScheduleWarning = "Con este cambio, el viaje pasa a terminar el 10/10 — ¿la fecha del servicio está bien?",
            PendingScheduleWarningByUserId = Author,
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return reserva.Id;
    }

    [Fact]
    public async Task ElAutor_LoConsumeYSeLimpia()
    {
        await using var context = CreateContext();
        var reservaId = await SeedReservaConPendienteAsync(context);
        var service = CreateReservaService(context, callerUserId: Author);

        var dto = await service.GetReservaByIdAsync(reservaId);

        Assert.NotNull(dto.ScheduleWarning);
        var reloaded = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.Null(reloaded.PendingScheduleWarning);
        Assert.Null(reloaded.PendingScheduleWarningByUserId);
    }

    [Fact]
    public async Task SegundoGet_DelMismoAutor_YaNoTraeNada()
    {
        await using var context = CreateContext();
        var reservaId = await SeedReservaConPendienteAsync(context);
        var service = CreateReservaService(context, callerUserId: Author);

        await service.GetReservaByIdAsync(reservaId); // primer GET: consume
        var segundo = await service.GetReservaByIdAsync(reservaId);

        Assert.Null(segundo.ScheduleWarning);
    }

    [Fact]
    public async Task OtroUsuario_NoLoConsume_QuedaIntactoParaElAutor()
    {
        await using var context = CreateContext();
        var reservaId = await SeedReservaConPendienteAsync(context);
        var service = CreateReservaService(context, callerUserId: OtherUser);

        var dto = await service.GetReservaByIdAsync(reservaId);

        Assert.Null(dto.ScheduleWarning); // otro usuario NO lo ve
        var reloaded = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.NotNull(reloaded.PendingScheduleWarning); // sigue ahi para el autor real
        Assert.Equal(Author, reloaded.PendingScheduleWarningByUserId);
    }

    [Fact]
    public async Task AdminDistintoDelAutor_TampocoLoConsume_B6()
    {
        // B6 (round 3): se elimino la excepcion "o el caller es Admin/view_all". Un admin que mira la
        // MISMA reserva que otro vendedor edito NO consume el aviso ajeno.
        await using var context = CreateContext();
        var reservaId = await SeedReservaConPendienteAsync(context);
        var adminUserId = "admin-otro";
        var service = CreateReservaService(context, callerUserId: adminUserId);

        var dto = await service.GetReservaByIdAsync(reservaId);

        Assert.Null(dto.ScheduleWarning);
        var reloaded = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.NotNull(reloaded.PendingScheduleWarning);
    }

    [Fact]
    public async Task CallerSinUserId_NoLoConsume_NullSafe_B7()
    {
        // B7: comparacion null-safe. Un caller sin UserId (HttpContext sin claim) NUNCA matchea, ni
        // siquiera contra un pendiente que (por diseño, no debería pasar salvo dato viejo) tampoco tuviera dueño.
        await using var context = CreateContext();
        var reservaId = await SeedReservaConPendienteAsync(context);
        var service = CreateReservaService(context, callerUserId: null);

        var dto = await service.GetReservaByIdAsync(reservaId);

        Assert.Null(dto.ScheduleWarning);
        var reloaded = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.NotNull(reloaded.PendingScheduleWarning);
    }

    [Fact]
    public async Task PendienteSinDueño_NuncaMatcheaAUnCallerConUserIdNull()
    {
        // Caso borde (dato viejo, nunca lo escribe el job de reparacion a proposito): un pendiente CON
        // texto pero SIN PendingScheduleWarningByUserId. Un caller sin UserId tampoco debe consumirlo.
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            NumeroReserva = $"F-ADR053-PSW-{Guid.NewGuid():N}"[..14],
            Name = "Pendiente huerfano",
            Status = EstadoReserva.InManagement,
            PendingScheduleWarning = "Aviso huerfano",
            PendingScheduleWarningByUserId = null,
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        var service = CreateReservaService(context, callerUserId: null);

        var dto = await service.GetReservaByIdAsync(reserva.Id);

        Assert.Null(dto.ScheduleWarning);
    }

    [Fact]
    public async Task DosEdicionesSeguidas_SoloSobreviveElAvisoDeLaSegunda()
    {
        // Last-write-wins (D2.1, aceptado a proposito): el actor edita el hotel (aviso A) y, ANTES de su
        // proximo GET, edita tambien el vuelo (aviso B) — solo sobrevive B.
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador Test" };
        var reserva = new Reserva { NumeroReserva = $"F-ADR053-PSW-{Guid.NewGuid():N}"[..14], Name = "Doble edicion", Status = EstadoReserva.InManagement };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        // Primera mutacion: se carga un hotel que corre el FIN del viaje al 12/04.
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, HotelName = "Hotel",
            CheckIn = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckOut = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc),
            Status = "Confirmado",
        });
        await context.SaveChangesAsync();
        var (_, _, changedByFirst) = await ReservaScheduleCalculator.RecalculateAndPersistAsync(
            context, reserva.Id, Author, "Vendedor Autor", CancellationToken.None);
        Assert.True(changedByFirst);
        var afterFirst = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.Contains("12/04", afterFirst.PendingScheduleWarning);

        // Segunda mutacion (ANTES de que el actor vea la primera): se carga un vuelo que corre el INICIO
        // del viaje mas atras todavia.
        context.FlightSegments.Add(new FlightSegment
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "HK",
            DepartureTime = new DateTime(2026, 3, 20, 10, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();
        var (_, _, changedBySecond) = await ReservaScheduleCalculator.RecalculateAndPersistAsync(
            context, reserva.Id, Author, "Vendedor Autor", CancellationToken.None);
        Assert.True(changedBySecond);

        var service = CreateReservaService(context, callerUserId: Author);
        var dto = await service.GetReservaByIdAsync(reserva.Id);

        // Solo sobrevive el aviso de la SEGUNDA edicion (menciona el nuevo inicio, no el fin de la primera).
        Assert.Contains("20/03", dto.ScheduleWarning);
        Assert.DoesNotContain("12/04", dto.ScheduleWarning);
    }

    // ================================================================================================
    // B7: "actor null = sin aviso" — el job de reparacion (RecalculateAndPersistAsync con actor null)
    // NUNCA deja un PendingScheduleWarning, aunque la ventana SI haya cambiado.
    // ================================================================================================

    [Fact]
    public async Task RecalculateAndPersist_ConActorNull_NuncaEscribeAviso_AunqueLaVentanaCambie()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador Test" };
        var reserva = new Reserva { NumeroReserva = $"F-ADR053-PSW-{Guid.NewGuid():N}"[..14], Name = "Job sin actor", Status = EstadoReserva.Traveling };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, HotelName = "Hotel",
            CheckIn = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckOut = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
            Status = "Confirmado",
        });
        await context.SaveChangesAsync();

        var (_, end, changed) = await ReservaScheduleCalculator.RecalculateAndPersistAsync(
            context, reserva.Id, actorUserId: null, actorUserName: null, CancellationToken.None);

        Assert.True(changed);
        Assert.NotNull(end);
        var reloaded = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.Null(reloaded.PendingScheduleWarning);
        Assert.Null(reloaded.PendingScheduleWarningByUserId);
    }
}
