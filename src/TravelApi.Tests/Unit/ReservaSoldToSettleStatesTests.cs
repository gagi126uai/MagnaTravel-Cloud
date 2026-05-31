using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Rediseño maquina de estados Reserva (Fase A+B, 2026-05-30): tests del flag
/// EnableSoldToSettleStates sobre UpdateStatusAsync y el job de lifecycle.
///
/// Estructura:
///  - Bloque "flag OFF": el comportamiento debe ser byte-identico al historico (no romper).
///  - Bloque "flag ON": cadena nueva Budget -> Sold -> Confirmed -> Traveling -> ToSettle ->
///    Closed, con forwards permitidos, prohibidos y gates relocalizados.
///
/// NOTA: estos tests usan InMemory + Moq y NO se ejecutan local (se cuelga). Los corre el
/// reviewer/QA. Estan escritos para documentar y verificar el contrato del flag.
/// </summary>
public class ReservaSoldToSettleStatesTests
{
    private readonly Mock<IMapper> _mapperMock = new();

    // ---- Helpers de construccion ----

    private static DbContextOptions<AppDbContext> NewDbOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    /// <summary>
    /// Mock del settings service que devuelve una entidad con el flag en el valor pedido.
    /// El resto de los defaults (RequireFullPaymentForOperativeStatus, etc.) quedan en sus
    /// valores por defecto de la entidad.
    /// </summary>
    private static Mock<IOperationalFinanceSettingsService> SettingsMock(bool soldToSettleEnabled)
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableSoldToSettleStates = soldToSettleEnabled });
        return mock;
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private ReservaService BuildService(AppDbContext context, bool soldToSettleEnabled) =>
        new(context, _mapperMock.Object, SettingsMock(soldToSettleEnabled).Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

    /// <summary>
    /// Servicio generico CONFIRMADO con fechas futuras. Sirve para pasar los gates de
    /// readiness (hay servicio) y de servicios-sin-confirmar (esta en "Confirmado").
    /// </summary>
    private static ServicioReserva ConfirmedService(int id, int reservaId, DateTime? departure = null) => new()
    {
        Id = id,
        ReservaId = reservaId,
        ServiceType = "Hotel",
        ProductType = "Hotel",
        Description = "Servicio test",
        ConfirmationNumber = "ABC123",
        Status = "Confirmado",
        DepartureDate = departure ?? DateTime.UtcNow.AddDays(15),
        SalePrice = 150m,
        NetCost = 100m,
        Commission = 50m,
        CreatedAt = DateTime.UtcNow
    };

    // =====================================================================
    // BLOQUE FLAG OFF — byte-identico al comportamiento historico
    // =====================================================================

    [Fact]
    public async Task FlagOff_BudgetToConfirmedDirect_Works()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Budget });
        context.Servicios.Add(ConfirmedService(1, 1));
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: false);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Confirmed);

        Assert.Equal(EstadoReserva.Confirmed, result.Status);
    }

    [Fact]
    public async Task FlagOff_ConfirmedToBudget_BlockedWhenHasPayments()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Confirmed });
        context.Payments.Add(new Payment { Id = 1, ReservaId = 1, Amount = 100, Status = "Paid" });
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateStatusAsync(1, EstadoReserva.Budget));
    }

    [Fact]
    public async Task FlagOff_TravelingToClosed_BlockedWhenBalancePositive()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Traveling, Balance = 500m });
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateStatusAsync(1, EstadoReserva.Closed));
    }

    [Fact]
    public async Task FlagOff_QuoteConversion_BornsConfirmed()
    {
        using var context = new AppDbContext(NewDbOptions());
        var customer = new Customer { Id = 1, FullName = "Cliente" };
        context.Customers.Add(customer);
        context.Quotes.Add(new Quote { Id = 1, Title = "Cotizacion", CustomerId = 1, TotalSale = 1000m, TotalCost = 800m });
        await context.SaveChangesAsync();

        var quoteService = new QuoteService(context, new Mock<IEntityReferenceResolver>().Object, SettingsMock(false).Object);
        await quoteService.ConvertToFileAsync(1, CancellationToken.None);

        var reserva = await context.Reservas.FirstAsync();
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);
    }

    // FIX 1 (B1): con el flag OFF, Sold/ToSettle NO existen para el sistema. Un POST directo con
    // status="Sold"/"ToSettle" debe rebotar con ArgumentException (mismo error que un estado
    // desconocido), NO escribir la reserva en un estado que el resto del sistema OFF no entiende.
    [Theory]
    [InlineData(EstadoReserva.Sold)]
    [InlineData(EstadoReserva.ToSettle)]
    public async Task FlagOff_RejectsNewStates_WithArgumentException(string newState)
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Budget });
        context.Servicios.Add(ConfirmedService(1, 1));
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: false);

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateStatusAsync(1, newState));

        // La reserva no se movio: sigue en Budget.
        var reserva = await context.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Budget, reserva!.Status);
    }

    // =====================================================================
    // BLOQUE FLAG ON — cadena nueva
    // =====================================================================

    [Fact]
    public async Task FlagOn_BudgetToSold_Works()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Budget });
        context.Servicios.Add(ConfirmedService(1, 1));
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Sold);

        Assert.Equal(EstadoReserva.Sold, result.Status);
    }

    [Fact]
    public async Task FlagOn_BudgetToSold_BlockedWhenNoServices()
    {
        using var context = new AppDbContext(NewDbOptions());
        // Sin servicios: el gate de readiness (relocalizado a Budget->Sold) debe bloquear.
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Budget });
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateStatusAsync(1, EstadoReserva.Sold));
    }

    [Fact]
    public async Task FlagOn_BudgetToConfirmedDirect_IsForbidden()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Budget });
        context.Servicios.Add(ConfirmedService(1, 1));
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        // INV-SM-01: el salto directo Budget->Confirmed esta prohibido en el ciclo nuevo.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateStatusAsync(1, EstadoReserva.Confirmed));
    }

    [Fact]
    public async Task FlagOn_SoldToConfirmed_Works_WhenServicesConfirmed()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Sold });
        context.Servicios.Add(ConfirmedService(1, 1));
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Confirmed);

        Assert.Equal(EstadoReserva.Confirmed, result.Status);
    }

    [Fact]
    public async Task FlagOn_SoldToConfirmed_Blocked_WhenServiceUnconfirmed()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Sold });
        // Servicio en "Solicitado": el gate de operador (relocalizado a Sold->Confirmed) bloquea.
        var svc = ConfirmedService(1, 1);
        svc.Status = "Solicitado";
        context.Servicios.Add(svc);
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateStatusAsync(1, EstadoReserva.Confirmed));
    }

    [Fact]
    public async Task FlagOn_TravelingToToSettle_Works()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Traveling, Balance = 999m });
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        // Traveling -> ToSettle NO tiene gate de balance: pasa aunque haya saldo pendiente.
        var result = await service.UpdateStatusAsync(1, EstadoReserva.ToSettle);

        Assert.Equal(EstadoReserva.ToSettle, result.Status);
    }

    [Fact]
    public async Task FlagOn_TravelingToClosedDirect_Works_WhenBalanceZero()
    {
        // Rediseño 2026-05-31: ToSettle pasa a ser un desvio MANUAL OPCIONAL, asi que el cierre
        // por DEFAULT ahora es Traveling -> Closed directo (igual que el clasico), con el mismo
        // gate de balance.
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Traveling, Balance = 0m });
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Closed, actorUserId: "user-1");

        Assert.Equal(EstadoReserva.Closed, result.Status);
        Assert.NotNull(result.ClosedAt);

        // Deja rastro auditable como cualquier transicion forward de la cadena nueva.
        var log = await context.ReservaStatusChangeLogs.SingleAsync();
        Assert.Equal(EstadoReserva.Traveling, log.FromStatus);
        Assert.Equal(EstadoReserva.Closed, log.ToStatus);
        Assert.Equal("Forward", log.Direction);
        Assert.Equal("user-1", log.ByUserId);
    }

    [Fact]
    public async Task FlagOn_TravelingToClosedDirect_BlockedWhenBalancePositive()
    {
        // El cierre directo Traveling->Closed comparte el gate de balance del cierre clasico.
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Traveling, Balance = 500m });
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateStatusAsync(1, EstadoReserva.Closed));
    }

    [Fact]
    public async Task FlagOn_TravelingToToSettle_StillAllowed_ManualDetour()
    {
        // ToSettle sigue siendo un destino forward valido desde Traveling: es el desvio manual
        // opcional para apartar la reserva a liquidar. Sin gate de balance.
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Traveling, Balance = 333m });
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.ToSettle);

        Assert.Equal(EstadoReserva.ToSettle, result.Status);
    }

    [Fact]
    public async Task FlagOn_ToSettleToClosed_Works_WhenBalanceZero()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.ToSettle, Balance = 0m });
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Closed);

        Assert.Equal(EstadoReserva.Closed, result.Status);
        Assert.NotNull(result.ClosedAt);
    }

    [Fact]
    public async Task FlagOn_ToSettleToClosed_Blocked_WhenBalancePositive()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.ToSettle, Balance = 250m });
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateStatusAsync(1, EstadoReserva.Closed));
    }

    [Fact]
    public async Task FlagOn_QuoteConversion_BornsSold()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Customers.Add(new Customer { Id = 1, FullName = "Cliente" });
        context.Quotes.Add(new Quote { Id = 1, Title = "Cotizacion", CustomerId = 1, TotalSale = 1000m, TotalCost = 800m });
        await context.SaveChangesAsync();

        var quoteService = new QuoteService(context, new Mock<IEntityReferenceResolver>().Object, SettingsMock(true).Object);
        await quoteService.ConvertToFileAsync(1, CancellationToken.None);

        var reserva = await context.Reservas.FirstAsync();
        Assert.Equal(EstadoReserva.Sold, reserva.Status);
    }

    [Theory]
    [InlineData(EstadoReserva.Sold)]
    [InlineData(EstadoReserva.ToSettle)]
    public async Task ValidStatuses_AcceptsNewStates_WhenFlagOn(string newState)
    {
        // B1 del review: con el flag ON la whitelist acepta Sold/ToSettle como strings validos.
        // Probamos via una transicion legal para que NO rebote en validStatuses.
        using var context = new AppDbContext(NewDbOptions());
        // Sembramos en el estado predecesor del estado objetivo para que la transicion sea legal.
        var fromStatus = newState == EstadoReserva.Sold ? EstadoReserva.Budget : EstadoReserva.Traveling;
        var reserva = new Reserva { Id = 1, Name = "Test", Status = fromStatus, Balance = 0m };
        context.Reservas.Add(reserva);
        if (fromStatus == EstadoReserva.Budget)
            context.Servicios.Add(ConfirmedService(1, 1)); // readiness para Budget->Sold
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        var result = await service.UpdateStatusAsync(1, newState);
        Assert.Equal(newState, result.Status);
    }

    // =====================================================================
    // BLOQUE LIFECYCLE JOB
    // =====================================================================

    private ReservaLifecycleAutomationService BuildLifecycle(AppDbContext context, bool soldToSettleEnabled) =>
        new(context, NullLogger<ReservaLifecycleAutomationService>.Instance, SettingsMock(soldToSettleEnabled).Object);

    [Fact]
    public async Task Lifecycle_FlagOff_ClosesTravelingWhenEndedAndZeroBalance()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            Name = "Test",
            Status = EstadoReserva.Traveling,
            EndDate = DateTime.UtcNow.AddDays(-2),
            Balance = 0m
        });
        await context.SaveChangesAsync();

        var job = BuildLifecycle(context, soldToSettleEnabled: false);
        await job.RunDailyDetailedAsync();

        var reserva = await context.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Closed, reserva!.Status);
    }

    [Fact]
    public async Task Lifecycle_FlagOn_ClosesTravelingDirectlyWhenEndedAndZeroBalance()
    {
        // Rediseño 2026-05-31: con flag ON el job cierra Traveling -> Closed directo (EndDate
        // pasada + Balance == 0), IGUAL que el clasico. Ya NO mete a nadie en ToSettle.
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            Name = "Test",
            Status = EstadoReserva.Traveling,
            EndDate = DateTime.UtcNow.AddDays(-2),
            Balance = 0m
        });
        await context.SaveChangesAsync();

        var job = BuildLifecycle(context, soldToSettleEnabled: true);
        await job.RunDailyDetailedAsync();

        var reserva = await context.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Closed, reserva!.Status);
        Assert.NotNull(reserva.ClosedAt);

        // El job NO crea ninguna fila en ToSettle.
        Assert.False(await context.Reservas.AnyAsync(r => r.Status == EstadoReserva.ToSettle));
    }

    [Fact]
    public async Task Lifecycle_FlagOn_LeavesTravelingWithDebtUntouched()
    {
        // Con saldo pendiente, el viaje terminado NO cierra (mismo gate que el clasico) y tampoco
        // se desvia a ToSettle: el job no toca ToSettle en absoluto.
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            Name = "Test",
            Status = EstadoReserva.Traveling,
            EndDate = DateTime.UtcNow.AddDays(-2),
            Balance = 1234m
        });
        await context.SaveChangesAsync();

        var job = BuildLifecycle(context, soldToSettleEnabled: true);
        await job.RunDailyDetailedAsync();

        var reserva = await context.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Traveling, reserva!.Status);
    }

    [Fact]
    public async Task Lifecycle_FlagOn_DoesNotTouchToSettleReservas()
    {
        // ToSettle es un desvio manual: el job NUNCA lo auto-cierra, ni siquiera con Balance == 0.
        // Cerrar una reserva apartada a liquidar es decision manual del usuario.
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            Name = "Test",
            Status = EstadoReserva.ToSettle,
            EndDate = DateTime.UtcNow.AddDays(-5),
            Balance = 0m
        });
        await context.SaveChangesAsync();

        var job = BuildLifecycle(context, soldToSettleEnabled: true);
        await job.RunDailyDetailedAsync();

        var reserva = await context.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.ToSettle, reserva!.Status);
        Assert.Null(reserva.ClosedAt);
    }

    // =====================================================================
    // Revert Closed -> Traveling limpia ClosedAt
    // =====================================================================

    [Fact]
    public async Task FlagOn_RevertClosedToTraveling_ClearsClosedAt()
    {
        // Rediseño 2026-05-31: como ToSettle es opcional, el revert de Closed vuelve a Traveling
        // (el estado anterior real garantizado), NO a ToSettle. Una reserva pudo cerrar directo
        // Traveling->Closed sin pasar nunca por ToSettle.
        using var context = new AppDbContext(NewDbOptions());
        var closedAt = DateTime.UtcNow.AddDays(-1);
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            Name = "Test",
            Status = EstadoReserva.Closed,
            Balance = 0m,
            ClosedAt = closedAt
        });
        await context.SaveChangesAsync();

        // El revert llama GetReservaByIdAsync (mapea a DTO). El mapper mock por defecto devuelve
        // null y ApplyEconomicFlags reventaria; configuramos que devuelva un DTO vacio para que
        // el camino post-revert no rompa. Nosotros assertamos sobre la entidad en la base.
        _mapperMock.Setup(m => m.Map<TravelApi.Application.DTOs.ReservaDto>(It.IsAny<object>()))
            .Returns(new TravelApi.Application.DTOs.ReservaDto());

        var service = BuildService(context, soldToSettleEnabled: true);

        // En el ciclo nuevo, el revert de Closed va a Traveling. Admin bypassa la autorizacion.
        var request = new TravelApi.Application.DTOs.RevertStatusRequest(
            TargetStatus: EstadoReserva.Traveling,
            AuthorizedBySuperiorUserId: null,
            Reason: null);
        await service.RevertStatusAsync("1", request, actorUserId: "admin-1", actorUserName: "Admin", actorIsAdmin: true, CancellationToken.None);

        var reserva = await context.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Traveling, reserva!.Status);
        Assert.Null(reserva.ClosedAt); // re-abrir borra el ClosedAt
    }

    [Fact]
    public async Task FlagOn_RevertClosedToToSettle_IsForbidden()
    {
        // El revert de Closed ya NO va a ToSettle (es destino invalido en la matriz nueva).
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            Name = "Test",
            Status = EstadoReserva.Closed,
            Balance = 0m,
            ClosedAt = DateTime.UtcNow.AddDays(-1)
        });
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        var request = new TravelApi.Application.DTOs.RevertStatusRequest(
            TargetStatus: EstadoReserva.ToSettle,
            AuthorizedBySuperiorUserId: null,
            Reason: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RevertStatusAsync("1", request, actorUserId: "admin-1", actorUserName: "Admin", actorIsAdmin: true, CancellationToken.None));
    }

    // =====================================================================
    // FIX 5 (A1) — las transiciones forward de la cadena nueva dejan rastro en ReservaStatusChangeLog
    // =====================================================================

    [Fact]
    public async Task FlagOn_ForwardTransition_WritesChangeLog()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Budget });
        context.Servicios.Add(ConfirmedService(1, 1));
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        // Budget -> Sold via la entrada interna que recibe el actor.
        await service.UpdateStatusAsync(1, EstadoReserva.Sold, actorUserId: "user-1");

        var log = await context.ReservaStatusChangeLogs.SingleAsync();
        Assert.Equal(1, log.ReservaId);
        Assert.Equal(EstadoReserva.Budget, log.FromStatus);
        Assert.Equal(EstadoReserva.Sold, log.ToStatus);
        Assert.Equal("Forward", log.Direction);
        Assert.Equal("user-1", log.ByUserId);
    }

    [Fact]
    public async Task FlagOn_TravelingToToSettleManual_WritesChangeLog()
    {
        // BUG fix (review UI, 2026-05-30): con el flag ON, una reserva En viaje no tenia boton
        // manual para avanzar a "A liquidar" (solo lo hacia el job auto). Aca verificamos el lado
        // backend del fix: la transicion manual Traveling -> ToSettle es legal Y deja rastro
        // auditable (Direction="Forward") como cualquier otra transicion de la cadena nueva.
        using var context = new AppDbContext(NewDbOptions());
        // Con saldo pendiente a proposito: la transicion NO tiene gate de balance.
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Traveling, Balance = 777m });
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: true);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.ToSettle, actorUserId: "operador-1");

        Assert.Equal(EstadoReserva.ToSettle, result.Status);

        var log = await context.ReservaStatusChangeLogs.SingleAsync();
        Assert.Equal(1, log.ReservaId);
        Assert.Equal(EstadoReserva.Traveling, log.FromStatus);
        Assert.Equal(EstadoReserva.ToSettle, log.ToStatus);
        Assert.Equal("Forward", log.Direction);
        Assert.Equal("operador-1", log.ByUserId);
    }

    [Fact]
    public async Task FlagOff_ForwardTransition_DoesNotWriteChangeLog()
    {
        // El camino clasico (flag OFF) NO se loguea (deuda preexistente, fuera de scope del FIX 5).
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Budget });
        context.Servicios.Add(ConfirmedService(1, 1));
        await context.SaveChangesAsync();

        var service = BuildService(context, soldToSettleEnabled: false);

        await service.UpdateStatusAsync(1, EstadoReserva.Confirmed, actorUserId: "user-1");

        Assert.False(await context.ReservaStatusChangeLogs.AnyAsync());
    }

    [Fact]
    public async Task Lifecycle_FlagOn_TravelingToClosed_WritesChangeLogWithSystemActor()
    {
        // Rediseño 2026-05-31: el job cierra Traveling -> Closed (no a ToSettle), pero igual deja
        // rastro auditable con actor "sistema" porque es una transicion de la cadena nueva.
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            Name = "Test",
            Status = EstadoReserva.Traveling,
            EndDate = DateTime.UtcNow.AddDays(-2),
            Balance = 0m
        });
        await context.SaveChangesAsync();

        var job = BuildLifecycle(context, soldToSettleEnabled: true);
        await job.RunDailyDetailedAsync();

        var log = await context.ReservaStatusChangeLogs.SingleAsync(l => l.ToStatus == EstadoReserva.Closed);
        Assert.Equal(EstadoReserva.Traveling, log.FromStatus);
        Assert.Equal("Forward", log.Direction);
        Assert.Equal("system:lifecycle", log.ByUserId);
        Assert.Equal("Sistema (lifecycle)", log.ByUserName);
    }

    // =====================================================================
    // FIX 4 (C1) — guard fiscal: con flag ON, NO se puede facturar una reserva Vendida (Sold)
    // =====================================================================

    /// <summary>
    /// Arma un InvoiceService con dependencias minimas (mocks). El guard de Sold corre apenas se
    /// resuelve la reserva + se lee el settings, antes de cualquier logica AFIP, asi que no hace
    /// falta configurar el resto de los mocks para el camino que probamos.
    /// </summary>
    private InvoiceService BuildInvoiceService(AppDbContext context, bool soldToSettleEnabled)
    {
        var resolver = new Mock<IEntityReferenceResolver>();
        // El request trae ReservaId = "1"; resolvemos al id interno 1.
        resolver.Setup(r => r.ResolveRequiredIdAsync<Reserva>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        return new InvoiceService(
            context,
            resolver.Object,
            new Mock<IAfipService>().Object,
            new Mock<IInvoicePdfService>().Object,
            _mapperMock.Object,
            new Mock<Hangfire.IBackgroundJobClient>().Object,
            NullLogger<InvoiceService>.Instance,
            SettingsMock(soldToSettleEnabled).Object,
            BuildUserManager());
    }

    [Fact]
    public async Task FlagOn_CreateInvoice_BlockedWhenReservaIsSold()
    {
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Sold, Balance = 0m });
        await context.SaveChangesAsync();

        var invoiceService = BuildInvoiceService(context, soldToSettleEnabled: true);
        var request = new TravelApi.Application.DTOs.CreateInvoiceRequest { ReservaId = "1" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => invoiceService.CreateAsync(request, userId: "u1", userName: "User", CancellationToken.None));

        Assert.Contains("Vendida", ex.Message);

        // No se creo ninguna Invoice.
        Assert.False(await context.Invoices.AnyAsync());
    }

    [Fact]
    public async Task FlagOff_CreateInvoice_NotBlockedBySoldGuard()
    {
        // Con el flag OFF el estado Sold no existe en el flujo normal, pero validamos que el guard
        // NO dispara: una reserva en Confirmed (estado normal facturable) no rebota por el guard de
        // Sold. (Puede rebotar mas adelante por otras reglas, pero NO con el mensaje de "Vendida").
        using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Confirmed, Balance = 0m });
        await context.SaveChangesAsync();

        var invoiceService = BuildInvoiceService(context, soldToSettleEnabled: false);
        var request = new TravelApi.Application.DTOs.CreateInvoiceRequest { ReservaId = "1" };

        // Si llega a tirar excepcion, NO debe ser la del guard de Sold.
        try
        {
            await invoiceService.CreateAsync(request, userId: "u1", userName: "User", CancellationToken.None);
        }
        catch (Exception ex)
        {
            Assert.DoesNotContain("Vendida", ex.Message);
        }
    }
}
