using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1.15 Fase 2a (FIX 4): el dashboard enmascara costos sin cobranzas.see_cost
/// y filtra ReservasPendientes / ProximosViajes sin reservas.view_all.
/// </summary>
public class ReportServiceDashboardScopingTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IBnaExchangeRateService> _bnaMock;

    public ReportServiceDashboardScopingTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _bnaMock = new Mock<IBnaExchangeRateService>();
        _bnaMock.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((BnaUsdSellerRateDto?)null);
    }

    private static IHttpContextAccessor BuildContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var r in roles) claims.Add(new Claim(ClaimTypes.Role, r));
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private static async Task SeedAsync(AppDbContext context)
    {
        var startMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        // 2 reservas: una mia (vendedor-A), una ajena (vendedor-B). Ambas con balance > 0.
        context.Reservas.AddRange(
            new Reserva
            {
                Id = 1,
                NumeroReserva = "F-DASH-0001",
                Name = "Reserva mia",
                Status = EstadoReserva.Confirmed,
                ResponsibleUserId = "vendedor-A",
                CreatedAt = startMonth.AddDays(2),
                TotalSale = 1000m,
                TotalCost = 600m,
                Balance = 300m,
                StartDate = DateTime.UtcNow.AddDays(3)
            },
            new Reserva
            {
                Id = 2,
                NumeroReserva = "F-DASH-0002",
                Name = "Reserva ajena",
                Status = EstadoReserva.Confirmed,
                ResponsibleUserId = "vendedor-B",
                CreatedAt = startMonth.AddDays(2),
                TotalSale = 2000m,
                TotalCost = 1200m,
                Balance = 800m,
                StartDate = DateTime.UtcNow.AddDays(4)
            });
        await context.SaveChangesAsync();

        // ADR-021 Capa 6: el top-N de deudoras se lee de la tabla hija ReservaMoneyByCurrency (no del
        // escalar surrogate). En produccion el persister la mantiene sincronizada; aca la sembramos a
        // mano espejando el saldo ARS de cada reserva (ambas mono-ARS).
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.ARS, TotalSale = 1000m, ConfirmedSale = 1000m, TotalCost = 600m, TotalPaid = 700m, Balance = 300m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = Monedas.ARS, TotalSale = 2000m, ConfirmedSale = 2000m, TotalCost = 1200m, TotalPaid = 1200m, Balance = 800m });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Dashboard_WithoutSeeCost_MasksCostosAndMargen()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.ReportesView);

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        Assert.Equal(0m, dto.CostosDelMes);
        Assert.Equal(0m, dto.MargenBruto);
        Assert.Equal(0m, dto.PagosProveedores);
        // Trend tambien debe enmascarar costs y profit.
        Assert.All(dto.TendenciaHistorica, m =>
        {
            Assert.Equal(0m, m.Costs);
            Assert.Equal(0m, m.Profit);
        });
    }

    [Fact]
    public async Task Dashboard_WithSeeCost_ReturnsCostosAndMargen()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("colaborador-1", "Colaborador");
        var resolver = BuildResolver("colaborador-1",
            Permissions.ReportesView, Permissions.CobranzasSeeCost, Permissions.ReservasViewAll);

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        // Cost = 600 + 1200 = 1800 (ambas reservas del mes).
        Assert.Equal(1800m, dto.CostosDelMes);
        // Margen = 3000 - 1800 = 1200.
        Assert.Equal(1200m, dto.MargenBruto);
    }

    [Fact]
    public async Task Dashboard_VendedorWithoutViewAll_FiltersPendingToOwn()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.ReportesView);

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        // Solo mi reserva con balance > 0.
        Assert.Single(dto.ReservasPendientes);
        Assert.Equal("F-DASH-0001", dto.ReservasPendientes[0].NumeroReserva);

        // Solo mi proximo viaje.
        Assert.Single(dto.ProximosViajes);
        Assert.Equal("F-DASH-0001", dto.ProximosViajes[0].NumeroReserva);
    }

    [Fact]
    public async Task Dashboard_AdminBypass_SeesAllPendingAndCosts()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        Assert.Equal(2, dto.ReservasPendientes.Count);
        Assert.Equal(1800m, dto.CostosDelMes);
    }

    // ============================================================================================
    // ADR-021 Capa 6 (multimoneda) — REGRESION de fuga de costo POR MONEDA en el dashboard.
    //
    // El gap que marcaron los reviewers: los escalares CostosDelMes/MargenBruto/PagosProveedores ya
    // estaban enmascarados, pero faltaba pinear que los desgloses POR MONEDA del dashboard
    // (PorMoneda.CostosDelMes / PagosProveedores / CuentasPorPagar) tambien queden VACIOS sin
    // cobranzas.see_cost. Sin esto, un usuario sin permiso podria ver el costo/deuda de proveedor de
    // una moneda aunque el escalar mostrara 0 — fuga critica.
    // ============================================================================================

    /// <summary>
    /// Siembra datos multimoneda del MES en curso para el dashboard: una reserva con servicio ARS y
    /// USD (tabla hija ReservaMoneyByCurrency, fuente de CostosDelMes/CuentasPorPagar por moneda), un
    /// pago a proveedor en cada moneda (SupplierPayments, fuente de PagosProveedores por moneda) y
    /// saldo de proveedor en cada moneda (SupplierBalanceByCurrency, fuente de CuentasPorPagar).
    /// </summary>
    private static async Task SeedMultiCurrencyAsync(AppDbContext context)
    {
        var startMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var thisMonth = startMonth.AddDays(2);

        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-DASH-MC-0001",
            Name = "Reserva multimoneda",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-A",
            CreatedAt = thisMonth,
            // Escalares surrogate (no relevantes para los desgloses por moneda).
            TotalSale = 1200m,
            TotalCost = 750m,
            Balance = 1200m
        });
        await context.SaveChangesAsync();

        // Tabla hija: venta/costo por moneda (CostosDelMes por moneda).
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.ARS, TotalSale = 1000m, ConfirmedSale = 1000m, TotalCost = 600m, TotalPaid = 0m, Balance = 1000m },
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.USD, TotalSale = 200m, ConfirmedSale = 200m, TotalCost = 150m, TotalPaid = 0m, Balance = 200m });

        // Pagos a proveedor del mes en cada moneda real (PagosProveedores por moneda).
        context.SupplierPayments.AddRange(
            new SupplierPayment { Amount = 300m, Currency = Monedas.ARS, PaidAt = thisMonth },
            new SupplierPayment { Amount = 50m, Currency = Monedas.USD, PaidAt = thisMonth });

        // Deuda a proveedor por moneda (CuentasPorPagar por moneda).
        context.SupplierBalanceByCurrency.AddRange(
            new SupplierBalanceByCurrency { SupplierId = 1, Currency = Monedas.ARS, ConfirmedPurchases = 600m, TotalPaid = 300m, Balance = 300m },
            new SupplierBalanceByCurrency { SupplierId = 1, Currency = Monedas.USD, ConfirmedPurchases = 150m, TotalPaid = 50m, Balance = 100m });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Dashboard_ByCurrency_WithoutSeeCost_MasksCostAndPayablesPerCurrency()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedMultiCurrencyAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.ReportesView); // sin see_cost

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        // Sin permiso de costos: los desgloses POR MONEDA de costo/deuda quedan VACIOS (no filtran USD).
        Assert.Empty(dto.PorMoneda.CostosDelMes);
        Assert.Empty(dto.PorMoneda.PagosProveedores);
        Assert.Empty(dto.PorMoneda.CuentasPorPagar);

        // Lo que NO es costo sigue presente por moneda (cobros/ventas/saldo del cliente).
        Assert.NotEmpty(dto.PorMoneda.VentasDelMes);
        Assert.NotEmpty(dto.PorMoneda.SaldoPendiente);
    }

    [Fact]
    public async Task Dashboard_ByCurrency_WithSeeCost_ShowsCostAndPayablesPerCurrency()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedMultiCurrencyAsync(context);

        var accessor = BuildContextAccessor("colaborador-1", "Colaborador");
        var resolver = BuildResolver("colaborador-1",
            Permissions.ReportesView, Permissions.CobranzasSeeCost, Permissions.ReservasViewAll);

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        // Costos del mes por moneda.
        Assert.Equal(600m, dto.PorMoneda.CostosDelMes.Single(x => x.Currency == Monedas.ARS).Amount);
        Assert.Equal(150m, dto.PorMoneda.CostosDelMes.Single(x => x.Currency == Monedas.USD).Amount);

        // Pagos a proveedor por moneda.
        Assert.Equal(300m, dto.PorMoneda.PagosProveedores.Single(x => x.Currency == Monedas.ARS).Amount);
        Assert.Equal(50m, dto.PorMoneda.PagosProveedores.Single(x => x.Currency == Monedas.USD).Amount);

        // Cuentas por pagar por moneda.
        Assert.Equal(300m, dto.PorMoneda.CuentasPorPagar.Single(x => x.Currency == Monedas.ARS).Amount);
        Assert.Equal(100m, dto.PorMoneda.CuentasPorPagar.Single(x => x.Currency == Monedas.USD).Amount);
    }
}
