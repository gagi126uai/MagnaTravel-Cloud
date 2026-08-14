using System.Collections.Generic;
using System.Security.Claims;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-053 (2026-08-13): <c>UpdatePromisedDatesAsync</c> (D3, "fecha prometida" — par manual que NUNCA
/// pisa StartDate/EndDate) y <c>RecalculateDatesAsync</c> (D4, botón "volver a calcular").
/// </summary>
public class Adr053PromisedDatesAndRecalculateDatesTests
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

    private static ReservaService CreateService(AppDbContext context, string userId = "vendedor-test")
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        return new ReservaService(
            context, CreateMapper(), settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: accessor);
    }

    // ADR-020 F4: Confirmed esta bajo candado — sin una autorizacion VIVA, EnsureReservaEditableAsync
    // rechaza con 409 antes de llegar al escritor unico. Los tests que ejercitan RecalculateDatesAsync
    // sobre una reserva Confirmed la necesitan (misma mecanica que "Sacar de viaje" usa en la practica).
    private static void AddLiveAuthorization(AppDbContext context, int reservaId)
    {
        context.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            ReservaId = reservaId,
            Reason = "autorizacion viva de test",
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        });
    }

    // ================================================================================================
    // PATCH /promised-dates
    // ================================================================================================

    [Fact]
    public async Task UpdatePromisedDates_SeteaCadaCampo_NuncaTocaStartDateEndDate()
    {
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            NumeroReserva = "F-ADR053-PD-1", Name = "Reserva prometida", Status = EstadoReserva.InManagement,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var promisedStart = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
        var promisedEnd = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc);
        var dto = await service.UpdatePromisedDatesAsync(
            reserva.Id.ToString(),
            new UpdatePromisedDatesRequest(PromisedStartDate: promisedStart, PromisedEndDate: promisedEnd),
            CancellationToken.None);

        Assert.Equal(promisedStart, dto.PromisedStartDate);
        Assert.Equal(promisedEnd, dto.PromisedEndDate);
        // StartDate/EndDate calculados NUNCA se tocan por este endpoint.
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), dto.StartDate);
        Assert.Equal(new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), dto.EndDate);
    }

    [Fact]
    public async Task UpdatePromisedDates_ClearBorraElValor()
    {
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            NumeroReserva = "F-ADR053-PD-2", Name = "Reserva prometida a borrar", Status = EstadoReserva.InManagement,
            PromisedStartDate = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var dto = await service.UpdatePromisedDatesAsync(
            reserva.Id.ToString(),
            new UpdatePromisedDatesRequest(PromisedStartDate: null, PromisedEndDate: null, ClearPromisedStartDate: true),
            CancellationToken.None);

        Assert.Null(dto.PromisedStartDate);
    }

    [Fact]
    public async Task UpdatePromisedDates_RegresoAntesQueSalida_Rechaza()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { NumeroReserva = "F-ADR053-PD-3", Name = "Reserva invalida", Status = EstadoReserva.InManagement };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdatePromisedDatesAsync(
            reserva.Id.ToString(),
            new UpdatePromisedDatesRequest(
                PromisedStartDate: new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
                PromisedEndDate: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Cancelled)]
    [InlineData(EstadoReserva.Lost)]
    [InlineData(EstadoReserva.PendingOperatorRefund)]
    public async Task UpdatePromisedDates_OnReadOnlyState_Rejected(string status)
    {
        await using var context = CreateContext();
        var reserva = new Reserva { NumeroReserva = "F-ADR053-PD-4", Name = "Reserva cerrada", Status = status };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdatePromisedDatesAsync(
            reserva.Id.ToString(),
            new UpdatePromisedDatesRequest(PromisedStartDate: DateTime.UtcNow, PromisedEndDate: null),
            CancellationToken.None));

        var reloaded = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.Null(reloaded.PromisedStartDate);
    }

    // ================================================================================================
    // POST /recalculate-dates
    // ================================================================================================

    [Fact]
    public async Task RecalculateDates_ApagaNeedsDateRecalculation_AunqueLaVentanaNoCambie()
    {
        // Caso "Sacar de viaje" sin servicios vigentes: la ventana ya es null/null (no hay "cambio" que
        // detectar), pero el boton tiene que apagar la marca igual (D4).
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            NumeroReserva = "F-ADR053-RD-1", Name = "Reserva sin servicios", Status = EstadoReserva.Confirmed,
            NeedsDateRecalculation = true,
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        AddLiveAuthorization(context, reserva.Id);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var dto = await service.RecalculateDatesAsync(reserva.Id.ToString(), CancellationToken.None);

        Assert.False(dto.NeedsDateRecalculation);
        Assert.False(dto.IsUnderCorrection);
        var reloaded = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.False(reloaded.NeedsDateRecalculation);
    }

    [Fact]
    public async Task RecalculateDates_ConServiciosVigentes_RecalculaYApagaLaMarca()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador Test" };
        var reserva = new Reserva
        {
            NumeroReserva = "F-ADR053-RD-2", Name = "Reserva con hotel", Status = EstadoReserva.Confirmed,
            NeedsDateRecalculation = true,
        };
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
        AddLiveAuthorization(context, reserva.Id);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var dto = await service.RecalculateDatesAsync(reserva.Id.ToString(), CancellationToken.None);

        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), dto.StartDate);
        Assert.Equal(new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), dto.EndDate);
        Assert.False(dto.NeedsDateRecalculation);
    }

    [Theory]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Cancelled)]
    public async Task RecalculateDates_OnReadOnlyState_Rejected(string status)
    {
        await using var context = CreateContext();
        var reserva = new Reserva { NumeroReserva = "F-ADR053-RD-3", Name = "Reserva cerrada", Status = status };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RecalculateDatesAsync(reserva.Id.ToString(), CancellationToken.None));
    }
}
