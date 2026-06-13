using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
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
/// ADR-027 (auditoria ERP, hallazgo #10): "confirmada con cambios". Cuando el operador confirma un
/// servicio con OTRO precio/condicion, el vendedor edita el servicio para reflejarlo; si la reserva ya
/// estaba en estado VIVO (InManagement/Confirmed/Traveling/ToSettle), queda MARCADA para revision del
/// dueño y el saldo se ajusta solo. Estos tests cubren:
///   - el trigger (edicion de precio en estado vivo marca; en Cotizacion/Presupuesto NO marca);
///   - idempotencia de la fecha (segunda edicion no pisa ChangesPendingSince);
///   - el acuse (limpia el flag + registra quien/cuando);
///   - el saldo se ajusta igual y el flag queda en la misma operacion.
///
/// Usa el path GENERICO (ReservaService.UpdateServiceAsync), que es el chokepoint mas simple de armar a
/// mano; los 5 tipados (BookingService) comparten exactamente la misma logica via UpdateBalanceAsync.
/// </summary>
public class Adr027ConfirmedWithChangesTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static ReservaService CreateService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        const string userId = "vendedor-test";
        var accessor = BuildHttpContextAccessor(userId, "Admin"); // Admin: ve costos, sin masking
        var resolver = BuildResolver(userId, Permissions.CobranzasSeeCost);

        return new ReservaService(
            context,
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            settings.Object,
            BuildUserManager(),
            NullLogger<ReservaService>.Instance,
            resolver,
            accessor);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
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

    // Reserva + un servicio generico resuelto (venta 150, costo 100). El servicio nace "Confirmado" para
    // que sume a ConfirmedSale y el saldo cambie cuando movemos el precio.
    private static async Task<(Reserva reserva, ServicioReserva service)> SeedAsync(AppDbContext context, string status)
    {
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "R-2027-1",
            Name = "Reserva test",
            Status = status,
            ResponsibleUserId = "vendedor-A"
        };
        var service = new ServicioReserva
        {
            Id = 10,
            ReservaId = reserva.Id,
            ServiceType = "Excursion",
            ProductType = "Excursion",
            Description = "Excursion glaciar",
            Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(10),
            SalePrice = 150m,
            NetCost = 100m,
            Commission = 50m,
            CreatedAt = DateTime.UtcNow
        };
        context.Reservas.Add(reserva);
        context.Servicios.Add(service);
        await context.SaveChangesAsync();
        return (reserva, service);
    }

    private static AddServiceRequest Edit(decimal salePrice, decimal netCost = 100m) => new(
        ServiceType: "Excursion",
        SupplierId: null,
        Description: "Excursion glaciar",
        ConfirmationNumber: null,
        DepartureDate: DateTime.UtcNow.AddDays(10),
        ReturnDate: null,
        SalePrice: salePrice,
        NetCost: netCost,
        RateId: null);

    // ============================= TRIGGER =============================

    [Theory]
    [InlineData(EstadoReserva.InManagement)]
    [InlineData(EstadoReserva.Confirmed)]
    [InlineData(EstadoReserva.Traveling)]
    [InlineData(EstadoReserva.ToSettle)]
    public async Task EditingSalePrice_OnLiveReserva_MarksUnacknowledgedChanges(string liveStatus)
    {
        await using var context = CreateContext();
        var (reserva, service) = await SeedAsync(context, liveStatus);
        var sut = CreateService(context);

        await sut.UpdateServiceAsync(service.Id, Edit(salePrice: 200m), CancellationToken.None);

        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.True(stored.HasUnacknowledgedChanges);
        Assert.NotNull(stored.ChangesPendingSince);
    }

    [Theory]
    [InlineData(EstadoReserva.Quotation)]
    [InlineData(EstadoReserva.Budget)]
    public async Task EditingSalePrice_DuringQuotationOrBudget_DoesNotMark(string earlyStatus)
    {
        await using var context = CreateContext();
        var (reserva, service) = await SeedAsync(context, earlyStatus);
        var sut = CreateService(context);

        await sut.UpdateServiceAsync(service.Id, Edit(salePrice: 200m), CancellationToken.None);

        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.False(stored.HasUnacknowledgedChanges);
        Assert.Null(stored.ChangesPendingSince);
    }

    [Fact]
    public async Task EditingNetCostOnly_OnLiveReserva_AlsoMarks()
    {
        await using var context = CreateContext();
        var (reserva, service) = await SeedAsync(context, EstadoReserva.Confirmed);
        var sut = CreateService(context);

        // Misma venta, distinto costo: tambien es "el operador confirmo con otra condicion".
        await sut.UpdateServiceAsync(service.Id, Edit(salePrice: 150m, netCost: 120m), CancellationToken.None);

        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.True(stored.HasUnacknowledgedChanges);
    }

    [Fact]
    public async Task EditingWithNoPriceOrCostChange_OnLiveReserva_DoesNotMark()
    {
        await using var context = CreateContext();
        var (reserva, service) = await SeedAsync(context, EstadoReserva.Confirmed);
        var sut = CreateService(context);

        // Mismo precio y mismo costo: edicion de otra cosa (descripcion, etc.) NO abre un pendiente.
        await sut.UpdateServiceAsync(service.Id, Edit(salePrice: 150m, netCost: 100m), CancellationToken.None);

        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.False(stored.HasUnacknowledgedChanges);
    }

    [Fact]
    public async Task SecondEdit_DoesNotOverwriteFirstPendingDate()
    {
        await using var context = CreateContext();
        var (reserva, service) = await SeedAsync(context, EstadoReserva.Confirmed);
        var sut = CreateService(context);

        await sut.UpdateServiceAsync(service.Id, Edit(salePrice: 200m), CancellationToken.None);
        var firstPending = (await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id)).ChangesPendingSince;
        Assert.NotNull(firstPending);

        // Una segunda edicion mientras sigue sin acusar NO reinicia el reloj de "desde cuando".
        await sut.UpdateServiceAsync(service.Id, Edit(salePrice: 250m), CancellationToken.None);
        var secondPending = (await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id)).ChangesPendingSince;

        Assert.Equal(firstPending, secondPending);
    }

    [Fact]
    public async Task EditingSalePrice_AdjustsCustomerBalance_AndMarksInSameOperation()
    {
        await using var context = CreateContext();
        var (reserva, service) = await SeedAsync(context, EstadoReserva.Confirmed);
        var sut = CreateService(context);

        // Venta confirmada inicial = 150 (servicio Confirmado), sin pagos => saldo 150.
        await sut.UpdateServiceAsync(service.Id, Edit(salePrice: 220m), CancellationToken.None);

        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        // El saldo se ajusto solo (ReservaMoneyPersister): venta confirmada paso a 220.
        Assert.Equal(220m, stored.ConfirmedSale);
        Assert.Equal(220m, stored.Balance);
        // ...y la marca quedo en la MISMA operacion.
        Assert.True(stored.HasUnacknowledgedChanges);
    }

    // ============================= ACUSE =============================

    [Fact]
    public async Task AcknowledgeChanges_ClearsFlag_AndRecordsWhoAndWhen()
    {
        await using var context = CreateContext();
        var (reserva, service) = await SeedAsync(context, EstadoReserva.Confirmed);
        var sut = CreateService(context);
        await sut.UpdateServiceAsync(service.Id, Edit(salePrice: 200m), CancellationToken.None);

        var before = DateTime.UtcNow;
        var dto = await sut.AcknowledgeChangesAsync(
            reserva.PublicId.ToString(), actorUserId: "owner-1", actorUserName: "Gaston", CancellationToken.None);

        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.False(stored.HasUnacknowledgedChanges);
        Assert.Null(stored.ChangesPendingSince);
        Assert.Equal("owner-1", stored.ChangesAckByUserId);
        Assert.Equal("Gaston", stored.ChangesAckByUserName);
        Assert.NotNull(stored.ChangesAckAt);
        Assert.True(stored.ChangesAckAt!.Value >= before);
        // El DTO devuelto refleja el estado limpio.
        Assert.False(dto.HasUnacknowledgedChanges);
    }

    [Fact]
    public async Task AcknowledgeChanges_OnUnmarkedReserva_IsNoOp()
    {
        await using var context = CreateContext();
        var (reserva, _) = await SeedAsync(context, EstadoReserva.Confirmed);
        var sut = CreateService(context);

        // Nunca se marco: acusar es idempotente, no escribe auditoria falsa.
        var dto = await sut.AcknowledgeChangesAsync(
            reserva.PublicId.ToString(), actorUserId: "owner-1", actorUserName: "Gaston", CancellationToken.None);

        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.False(stored.HasUnacknowledgedChanges);
        Assert.Null(stored.ChangesAckByUserId);
        Assert.Null(stored.ChangesAckAt);
        Assert.False(dto.HasUnacknowledgedChanges);
    }

    [Fact]
    public async Task AcknowledgeChanges_UnknownReserva_Throws()
    {
        await using var context = CreateContext();
        var sut = CreateService(context);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sut.AcknowledgeChangesAsync(Guid.NewGuid().ToString(), "owner-1", "Gaston", CancellationToken.None));
    }

    // ============================= DTO =============================

    [Fact]
    public async Task ReservaDto_ExposesFlagAndPendingDate()
    {
        await using var context = CreateContext();
        var (reserva, service) = await SeedAsync(context, EstadoReserva.Confirmed);
        var sut = CreateService(context);
        await sut.UpdateServiceAsync(service.Id, Edit(salePrice: 200m), CancellationToken.None);

        var dto = await sut.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.True(dto.HasUnacknowledgedChanges);
        Assert.NotNull(dto.ChangesPendingSince);
    }
}
