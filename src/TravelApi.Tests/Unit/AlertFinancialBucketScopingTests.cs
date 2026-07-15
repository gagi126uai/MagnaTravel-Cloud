using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Privacidad 2026-06-17 (fix SIN flag): los dos buckets financieros de /api/alerts dejan de ser
/// "admin-only, todo o nada" y respetan cada uno su regla (guia-ux-gaston.md):
///   - UrgentTrips ("viajó/terminó y debe") = deuda del CLIENTE -> scope por DUEÑO. El admin ve todas;
///     el vendedor SOLO las de SUS reservas; un no-admin sin identidad no ve ninguna (fail-closed). El
///     MONTO que el cliente debe NO es costo: se muestra aunque el caller no tenga cobranzas.see_cost.
///   - SupplierDebts ("le debemos al operador") = COSTO -> solo visible con cobranzas.see_cost; es deuda
///     agregada de la agencia, no se scopea por vendedor.
/// </summary>
public class AlertFinancialBucketScopingTests
{
    private const string SellerA = "vendedor-A";
    private const string SellerB = "vendedor-B";

    private static DbContextOptions<AppDbContext> NewDbOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static Mock<IOperationalFinanceSettingsService> SettingsMock()
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { UpcomingUnpaidReservationAlertDays = 30 });
        return mock;
    }

    private static AlertService NewService(AppDbContext ctx) =>
        new(ctx, SettingsMock().Object, NullLogger<AlertService>.Instance);

    // Reserva "urgente": salida proxima (dentro de la ventana de 30 dias) con saldo impago.
    private static Reserva UrgentReserva(int id, string? ownerUserId) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        NumeroReserva = $"R-{id}",
        Name = $"Reserva {id}",
        Status = EstadoReserva.Confirmed,
        Balance = 100m,
        TotalSale = 1000m,
        StartDate = DateTime.UtcNow.Date.AddDays(5),
        ResponsibleUserId = ownerUserId
    };

    private static List<string> UrgentReservaNumbers(object urgentTrips)
    {
        var nums = new List<string>();
        foreach (var item in (IEnumerable)urgentTrips)
        {
            if (item.GetType().GetProperty("NumeroReserva")?.GetValue(item) is string n)
                nums.Add(n);
        }
        nums.Sort(StringComparer.Ordinal);
        return nums;
    }

    [Fact]
    public async Task UrgentTrips_Seller_SeesOnlyOwnReservas()
    {
        await using var ctx = new AppDbContext(NewDbOptions());
        ctx.Reservas.Add(UrgentReserva(1, SellerA)); // suya
        ctx.Reservas.Add(UrgentReserva(2, SellerB)); // de otro vendedor
        await ctx.SaveChangesAsync();

        dynamic result = await NewService(ctx).GetAlertsAsync(
            new AlertCallerContext(SellerA, IsAdmin: false), CancellationToken.None);

        Assert.Equal(new[] { "R-1" }, UrgentReservaNumbers(result.UrgentTrips));
    }

    [Fact]
    public async Task UrgentTrips_Seller_DoesNotSeeUnownedReserva()
    {
        await using var ctx = new AppDbContext(NewDbOptions());
        ctx.Reservas.Add(UrgentReserva(1, ownerUserId: null)); // sin dueño asignado
        await ctx.SaveChangesAsync();

        dynamic result = await NewService(ctx).GetAlertsAsync(
            new AlertCallerContext(SellerA, IsAdmin: false), CancellationToken.None);

        Assert.Empty((IEnumerable)result.UrgentTrips);
    }

    [Fact]
    public async Task UrgentTrips_NonAdminWithoutIdentity_SeesNothing_FailClosed()
    {
        // Borde critico: un no-admin SIN UserId no debe ver las reservas sin dueño (ResponsibleUserId == null).
        // Sin la guarda fail-closed, el predicado "ResponsibleUserId == null" las matchearia todas.
        await using var ctx = new AppDbContext(NewDbOptions());
        ctx.Reservas.Add(UrgentReserva(1, ownerUserId: null));
        await ctx.SaveChangesAsync();

        dynamic result = await NewService(ctx).GetAlertsAsync(
            new AlertCallerContext(UserId: null, IsAdmin: false, CanSeeCost: true), CancellationToken.None);

        Assert.Empty((IEnumerable)result.UrgentTrips);
    }

    [Fact]
    public async Task UrgentTrips_Admin_SeesAllRegardlessOfOwner()
    {
        await using var ctx = new AppDbContext(NewDbOptions());
        ctx.Reservas.Add(UrgentReserva(1, SellerA));
        ctx.Reservas.Add(UrgentReserva(2, SellerB));
        ctx.Reservas.Add(UrgentReserva(3, ownerUserId: null));
        await ctx.SaveChangesAsync();

        dynamic result = await NewService(ctx).GetAlertsAsync(
            new AlertCallerContext("admin-test", IsAdmin: true, CanSeeCost: true), CancellationToken.None);

        Assert.Equal(new[] { "R-1", "R-2", "R-3" }, UrgentReservaNumbers(result.UrgentTrips));
    }

    [Fact]
    public async Task UrgentTrips_SellerWithSeeCost_StillDoesNotSeeOtherSellersReserva()
    {
        // El permiso de ver costos NO anula el scope por dueño en UrgentTrips: la deuda del cliente
        // es por-vendedor sin importar el see_cost. Un vendedor con permiso de costos sigue viendo
        // SOLO lo suyo.
        await using var ctx = new AppDbContext(NewDbOptions());
        ctx.Reservas.Add(UrgentReserva(1, SellerA)); // suya
        ctx.Reservas.Add(UrgentReserva(2, SellerB)); // ajena
        await ctx.SaveChangesAsync();

        dynamic result = await NewService(ctx).GetAlertsAsync(
            new AlertCallerContext(SellerA, IsAdmin: false, CanSeeCost: true), CancellationToken.None);

        Assert.Equal(new[] { "R-1" }, UrgentReservaNumbers(result.UrgentTrips));
    }

    [Fact]
    public async Task UrgentTrips_NonAdminWithEmptyUserId_SeesNothing_FailClosed()
    {
        // Borde gemelo del anterior: UserId == "" (string vacio, no null) tambien debe cortar a vacio.
        // !string.IsNullOrEmpty cubre ambos; sin esta guarda, "ResponsibleUserId == \"\"" no matchearia,
        // pero el contrato fail-closed debe ser explicito ante cualquier identidad ausente.
        await using var ctx = new AppDbContext(NewDbOptions());
        ctx.Reservas.Add(UrgentReserva(1, ownerUserId: null));
        ctx.Reservas.Add(UrgentReserva(2, ownerUserId: ""));
        await ctx.SaveChangesAsync();

        dynamic result = await NewService(ctx).GetAlertsAsync(
            new AlertCallerContext(UserId: "", IsAdmin: false, CanSeeCost: true), CancellationToken.None);

        Assert.Empty((IEnumerable)result.UrgentTrips);
    }

    [Fact]
    public async Task UrgentTrips_ClientDebt_VisibleWithoutSeeCost()
    {
        // Lo que el cliente debe NO es un costo: un vendedor SIN cobranzas.see_cost ve la deuda de SU reserva.
        await using var ctx = new AppDbContext(NewDbOptions());
        ctx.Reservas.Add(UrgentReserva(1, SellerA));
        await ctx.SaveChangesAsync();

        dynamic result = await NewService(ctx).GetAlertsAsync(
            new AlertCallerContext(SellerA, IsAdmin: false, CanSeeCost: false), CancellationToken.None);

        Assert.Single((IEnumerable)result.UrgentTrips);
    }

    [Fact]
    public async Task SupplierDebts_HiddenWithoutSeeCost()
    {
        await using var ctx = new AppDbContext(NewDbOptions());
        ctx.Suppliers.Add(new Supplier { Id = 1, Name = "Mayorista", CurrentBalance = 500m, IsActive = true });
        ctx.SupplierBalanceByCurrency.Add(new SupplierBalanceByCurrency
        {
            SupplierId = 1, Currency = "ARS", Balance = 500m
        });
        await ctx.SaveChangesAsync();

        dynamic result = await NewService(ctx).GetAlertsAsync(
            new AlertCallerContext(SellerA, IsAdmin: false, CanSeeCost: false), CancellationToken.None);

        Assert.Empty((IEnumerable)result.SupplierDebts);
    }

    [Fact]
    public async Task SupplierDebts_VisibleWithSeeCost_NotScopedByOwner()
    {
        // Deuda al operador = agregada de la agencia: un vendedor CON permiso de ver costos la ve completa,
        // sin importar de quien sean las reservas (no se scopea por dueño, a diferencia de UrgentTrips).
        await using var ctx = new AppDbContext(NewDbOptions());
        ctx.Suppliers.Add(new Supplier { Id = 1, Name = "Mayorista", CurrentBalance = 500m, IsActive = true });
        ctx.SupplierBalanceByCurrency.Add(new SupplierBalanceByCurrency
        {
            SupplierId = 1, Currency = "ARS", Balance = 500m
        });
        await ctx.SaveChangesAsync();

        dynamic result = await NewService(ctx).GetAlertsAsync(
            new AlertCallerContext(SellerA, IsAdmin: false, CanSeeCost: true), CancellationToken.None);

        Assert.Single((IEnumerable)result.SupplierDebts);
    }
}
