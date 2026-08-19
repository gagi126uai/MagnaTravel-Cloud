using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// R2/R3 (spec dashboard 2026-08-18, "Ritmo de cobros y pagos"): <c>GET /reports/cashflow</c> dejo de
/// ser Admin-only y de mezclar ARS+USD en un solo numero. Estos tests blindan las dos reglas nuevas:
/// separar por moneda (P-3) y enmascarar los pagos a operadores sin cobranzas.see_cost (mismo criterio
/// que <c>ReportServiceDashboardScopingTests</c> ya usa para <c>GET /reports/dashboard</c>).
/// </summary>
public class ReportServiceCashFlowScopingTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IBnaExchangeRateService> _bnaMock;

    public ReportServiceCashFlowScopingTests()
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

    // ============================================================================================
    // (a) R2: un cobro en ARS y uno en USD el mismo dia NUNCA se suman en un solo numero por moneda.
    // ============================================================================================

    [Fact]
    public async Task CashFlow_SeparaCobrosPorMoneda_UnPagoArsYUnoUsdNoSeSuman()
    {
        await using var context = new AppDbContext(_dbOptions);
        var today = DateTime.UtcNow.Date;

        context.Payments.AddRange(
            new Payment { Amount = 1000m, Currency = Monedas.ARS, PaidAt = today, AffectsCash = true },
            new Payment { Amount = 50m, Currency = Monedas.USD, PaidAt = today, AffectsCash = true });
        await context.SaveChangesAsync();

        // Admin: bypass total de permisos, ve todas las monedas y toda la agencia.
        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");
        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);

        var result = await service.GetCashFlowProjectionAsync(90, CancellationToken.None);

        var todayEntry = result.Historical.Single(d => d.Date.Date == today);

        // Cada moneda en su propia linea, nunca mezcladas.
        Assert.Equal(1000m, todayEntry.CashInByCurrency.Single(c => c.Currency == Monedas.ARS).Amount);
        Assert.Equal(50m, todayEntry.CashInByCurrency.Single(c => c.Currency == Monedas.USD).Amount);

        // El escalar legacy (compat con AnalyticsPage.jsx) SI suma todo — es el campo viejo, documentado
        // como tal; el consumidor nuevo tiene que usar CashInByCurrency, nunca este.
        Assert.Equal(1050m, todayEntry.CashIn);
    }

    // ============================================================================================
    // (b) R3: sin cobranzas.see_cost, los pagos a operadores (informacion de costo) se esconden.
    // ============================================================================================

    [Fact]
    public async Task CashFlow_SinSeeCost_EnmascaraPagosAOperadores()
    {
        await using var context = new AppDbContext(_dbOptions);
        var today = DateTime.UtcNow.Date;

        context.SupplierPayments.Add(new SupplierPayment { Amount = 400m, Currency = Monedas.ARS, PaidAt = today });
        await context.SaveChangesAsync();

        // reportes.view + reservas.view_all (para aislar SOLO el efecto de see_cost), SIN cobranzas.see_cost.
        var accessor = BuildContextAccessor("colaborador-1", "Colaborador");
        var resolver = BuildResolver("colaborador-1", Permissions.ReportesView, Permissions.ReservasViewAll);
        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);

        var result = await service.GetCashFlowProjectionAsync(90, CancellationToken.None);

        var todayEntry = result.Historical.Single(d => d.Date.Date == today);

        // Va OMITIDO (lista vacia), no en $0 con la lista presente.
        Assert.Empty(todayEntry.CashOutByCurrency);
        Assert.Equal(0m, todayEntry.CashOut);

        // El saldo acumulado tiene que quedar en 0 (no en -400): si mostrara el saldo real con el pago
        // sin enmascarar, cualquiera podria despejar el costo real restando el cobro (visible, en este
        // caso 0) del cambio de saldo dia a dia — la fuga que R3 tiene que evitar.
        Assert.Equal(0m, todayEntry.RunningBalance);
    }

    // ============================================================================================
    // (c) Lote 2: sin reservas.view_all, la serie se acota a las reservas del vendedor.
    // ============================================================================================

    [Fact]
    public async Task CashFlow_SinViewAll_AcotaLaSerieAlaCarteraDelVendedor()
    {
        await using var context = new AppDbContext(_dbOptions);
        var today = DateTime.UtcNow.Date;

        context.Reservas.AddRange(
            new Reserva
            {
                Id = 1, NumeroReserva = "F-CASHFLOW-0001", Name = "Reserva de vendedor-A",
                Status = EstadoReserva.Confirmed, ResponsibleUserId = "vendedor-A",
                CreatedAt = today, TotalSale = 500m, TotalCost = 300m, Balance = 0m,
            },
            new Reserva
            {
                Id = 2, NumeroReserva = "F-CASHFLOW-0002", Name = "Reserva de vendedor-B",
                Status = EstadoReserva.Confirmed, ResponsibleUserId = "vendedor-B",
                CreatedAt = today, TotalSale = 700m, TotalCost = 400m, Balance = 0m,
            });
        await context.SaveChangesAsync();

        context.Payments.AddRange(
            new Payment { ReservaId = 1, Amount = 500m, Currency = Monedas.ARS, PaidAt = today, AffectsCash = true },
            new Payment { ReservaId = 2, Amount = 700m, Currency = Monedas.ARS, PaidAt = today, AffectsCash = true });
        await context.SaveChangesAsync();

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.ReportesView); // sin reservas.view_all
        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);

        var result = await service.GetCashFlowProjectionAsync(90, CancellationToken.None);

        var todayEntry = result.Historical.Single(d => d.Date.Date == today);

        // Solo el cobro de SU reserva (500), no el de la reserva ajena (700) ni la suma (1200).
        Assert.Equal(500m, todayEntry.CashInByCurrency.Single(c => c.Currency == Monedas.ARS).Amount);
        Assert.Equal(500m, todayEntry.CashIn);
    }

    // ============================================================================================
    // (d) Limpieza chica 2026-08-19: los saldos "Actual/30/60/90 dias" del cashflow tambien
    // separados por moneda (antes solo la serie dia a dia lo estaba, los 4 saldos resumen seguian
    // mezclando ARS+USD en CurrentBalance/ProjectedBalanceNN — deuda anotada en AnalyticsPage).
    // Los campos nuevos son ADITIVOS (T-8): los escalares viejos siguen andando igual.
    // ============================================================================================

    [Fact]
    public async Task CashFlow_SaldosPorMoneda_CoincidenConElRunningBalanceDelDiaCorrespondiente()
    {
        await using var context = new AppDbContext(_dbOptions);
        var today = DateTime.UtcNow.Date;

        context.Payments.Add(new Payment { Amount = 1000m, Currency = Monedas.ARS, PaidAt = today, AffectsCash = true });
        await context.SaveChangesAsync();

        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");
        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);

        // 90 dias: la proyeccion siempre tiene al menos 90 entradas (GetCashFlowProjectionAsync usa
        // Math.Max(days, 90)), asi que projected[29]/[59]/[89] existen sin fallback.
        var result = await service.GetCashFlowProjectionAsync(90, CancellationToken.None);

        Assert.Equal(result.Historical[^1].RunningBalanceByCurrency, result.CurrentBalanceByCurrency);
        Assert.Equal(result.Projected[29].RunningBalanceByCurrency, result.ProjectedBalance30ByCurrency);
        Assert.Equal(result.Projected[59].RunningBalanceByCurrency, result.ProjectedBalance60ByCurrency);
        Assert.Equal(result.Projected[89].RunningBalanceByCurrency, result.ProjectedBalance90ByCurrency);

        // Una sola moneda con movimiento (ARS): el saldo actual por moneda tiene que coincidir con
        // el escalar legacy, porque no hay ninguna otra moneda que el escalar este mezclando.
        var currentArs = result.CurrentBalanceByCurrency.Single(c => c.Currency == Monedas.ARS).Amount;
        Assert.Equal(result.CurrentBalance, currentArs);
    }
}
