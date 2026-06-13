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
/// Navegacion por mes de la pantalla de Caja (2026-06-13): el resumen y los movimientos aceptan un MES puntual
/// (year/month). Reglas que cubren estos tests:
///   - un mes pasado solo cuenta SUS movimientos (tope superior = mes siguiente exclusivo);
///   - sin year/month el comportamiento es IDENTICO al historico (byte-equivalencia con el calculo viejo),
///     porque el dashboard reusa el mismo metodo sin esos argumentos;
///   - year/month invalidos tiran ArgumentException (el controlador lo mapea a 400, nunca 500);
///   - el enmascarado de costo (cobranzas.see_cost) se preserva exacto, con y sin mes.
///
/// <para><b>Nota InMemory</b>: igual que los demas tests de tesoreria, se siembran asientos del Libro de Caja
/// (CashLedgerEntry) directamente, porque la caja se lee del libro (ADR-022 capa 4). El provider InMemory no
/// aplica CHECK ni indices unicos; aca se verifica el COMPORTAMIENTO de lectura (que numero da el resumen).</para>
/// </summary>
public class TreasuryMonthNavigationTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    // Harness de permisos (mismo patron que Adr022Tanda3Tests / SupplierService).
    private static IHttpContextAccessor BuildHttpContextAccessor(string userId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "Test");
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    /// <summary>Tesoreria con un caller que SI ve costos (tiene cobranzas.see_cost).</summary>
    private static TreasuryService BuildTreasuryCanSeeCost(AppDbContext context)
    {
        const string userId = "see-cost-user";
        return new TreasuryService(
            context, null!, financePositionService: null,
            httpContextAccessor: BuildHttpContextAccessor(userId),
            permissionResolver: BuildResolver(userId, Permissions.CobranzasSeeCost));
    }

    /// <summary>Tesoreria con un caller que NO ve costos (sin el permiso) -> egresos de proveedor enmascarados.</summary>
    private static TreasuryService BuildTreasuryNoCost(AppDbContext context)
    {
        const string userId = "no-cost-user";
        return new TreasuryService(
            context, null!, financePositionService: null,
            httpContextAccessor: BuildHttpContextAccessor(userId),
            permissionResolver: BuildResolver(userId /* sin permisos */));
    }

    private static CashLedgerEntry Ledger(
        string direction, decimal amount, string sourceType, DateTime occurredAt,
        string currency = "ARS", int? supplierPaymentId = null) => new()
    {
        Direction = direction,
        Amount = amount,
        Currency = currency,
        Method = "Transfer",
        OccurredAt = occurredAt,
        SourceType = sourceType,
        SupplierPaymentId = supplierPaymentId,
    };

    // Fechas fijas dentro de tres meses distintos (UTC, mediodia para no rozar bordes de mes).
    private static readonly DateTime March = new(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime April = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime May = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);

    // ====================================================================================
    // cash-summary acotado a un mes
    // ====================================================================================

    [Fact]
    public async Task CashSummary_PastMonth_OnlyCountsThatMonth_NoBleedFromOtherMonths()
    {
        await using var context = CreateContext();
        // Un cobro en cada uno de tres meses distintos.
        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 100m, CashLedgerSourceTypes.CustomerPayment, March),
            Ledger(CashMovementDirections.Income, 200m, CashLedgerSourceTypes.CustomerPayment, April),
            Ledger(CashMovementDirections.Income, 400m, CashLedgerSourceTypes.CustomerPayment, May));
        await context.SaveChangesAsync();

        var service = BuildTreasuryCanSeeCost(context);

        var marchSummary = await service.GetCashSummaryAsync(year: 2026, month: 3);
        var aprilSummary = await service.GetCashSummaryAsync(year: 2026, month: 4);
        var maySummary = await service.GetCashSummaryAsync(year: 2026, month: 5);

        // Cada mes ve SOLO su cobro: el tope superior (mes siguiente) corta el arrastre de meses posteriores.
        Assert.Equal(100m, marchSummary.CashInThisMonth);
        Assert.Equal(200m, aprilSummary.CashInThisMonth);
        Assert.Equal(400m, maySummary.CashInThisMonth);
    }

    [Fact]
    public async Task CashSummary_BoundaryFirstDayOfNextMonth_BelongsToNextMonth()
    {
        await using var context = CreateContext();
        // El primer instante de abril NO debe contar en marzo (rango [marzo, abril) es exclusivo en el tope).
        var firstOfApril = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        context.CashLedgerEntries.Add(
            Ledger(CashMovementDirections.Income, 99m, CashLedgerSourceTypes.CustomerPayment, firstOfApril));
        await context.SaveChangesAsync();

        var service = BuildTreasuryCanSeeCost(context);

        var marchSummary = await service.GetCashSummaryAsync(year: 2026, month: 3);
        var aprilSummary = await service.GetCashSummaryAsync(year: 2026, month: 4);

        Assert.Equal(0m, marchSummary.CashInThisMonth);
        Assert.Equal(99m, aprilSummary.CashInThisMonth);
    }

    [Fact]
    public async Task CashSummary_NoParams_EqualsCurrentMonth_ByteEquivalent()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        var startOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonth = startOfThisMonth.AddDays(-2); // cae en el mes anterior

        // Un cobro de este mes (debe contar) y uno del mes pasado (no debe contar sin params).
        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 300m, CashLedgerSourceTypes.CustomerPayment, now),
            Ledger(CashMovementDirections.Income, 999m, CashLedgerSourceTypes.CustomerPayment, lastMonth));
        await context.SaveChangesAsync();

        var service = BuildTreasuryCanSeeCost(context);

        // Sin params == mes actual explicito: mismo resultado (byte-equivalencia del default).
        var noParams = await service.GetCashSummaryAsync();
        var explicitCurrentMonth = await service.GetCashSummaryAsync(year: now.Year, month: now.Month);

        Assert.Equal(300m, noParams.CashInThisMonth);
        Assert.Equal(noParams.CashInThisMonth, explicitCurrentMonth.CashInThisMonth);
        Assert.Equal(noParams.CashOutThisMonth, explicitCurrentMonth.CashOutThisMonth);
        Assert.Equal(noParams.NetCashThisMonth, explicitCurrentMonth.NetCashThisMonth);
    }

    [Theory]
    [InlineData(2026, 0)]    // mes fuera de rango
    [InlineData(2026, 13)]   // mes fuera de rango
    [InlineData(1999, 5)]    // año absurdo (bajo)
    [InlineData(2101, 5)]    // año absurdo (alto)
    [InlineData(2026, null)] // solo year
    [InlineData(null, 5)]    // solo month
    public async Task CashSummary_InvalidYearMonth_Throws(int? year, int? month)
    {
        await using var context = CreateContext();
        var service = BuildTreasuryCanSeeCost(context);

        // El controlador mapea ArgumentException -> 400. Aca verificamos que el servicio la lanza (no 500).
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetCashSummaryAsync(year, month));
    }

    [Fact]
    public async Task CashSummary_MaskingPreserved_WithAndWithoutMonth()
    {
        await using var context = CreateContext();
        // Mes de marzo: un cobro (venta, visible) y un pago a proveedor (costo, se enmascara sin see_cost).
        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 500m, CashLedgerSourceTypes.CustomerPayment, March),
            Ledger(CashMovementDirections.Expense, 120m, CashLedgerSourceTypes.SupplierPayment, March));
        await context.SaveChangesAsync();

        // Con mes explicito: sin see_cost se tapa la salida; con see_cost se ve.
        var hiddenWithMonth = await BuildTreasuryNoCost(context).GetCashSummaryAsync(year: 2026, month: 3);
        var shownWithMonth = await BuildTreasuryCanSeeCost(context).GetCashSummaryAsync(year: 2026, month: 3);

        Assert.Equal(500m, hiddenWithMonth.CashInThisMonth);
        Assert.Equal(0m, hiddenWithMonth.CashOutThisMonth);              // enmascarado: salida = 0
        Assert.Equal(500m, hiddenWithMonth.NetCashThisMonth);            // neto = entrada (no se filtra por resta)
        Assert.Equal(500m, shownWithMonth.CashInThisMonth);
        Assert.Equal(120m, shownWithMonth.CashOutThisMonth);
        Assert.Equal(380m, shownWithMonth.NetCashThisMonth);
    }

    // ====================================================================================
    // movements acotado al mismo mes
    // ====================================================================================

    [Fact]
    public async Task Movements_FilteredByMonth_OnlyReturnsThatMonth()
    {
        await using var context = CreateContext();
        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 100m, CashLedgerSourceTypes.CustomerPayment, March),
            Ledger(CashMovementDirections.Income, 200m, CashLedgerSourceTypes.CustomerPayment, April),
            Ledger(CashMovementDirections.Income, 400m, CashLedgerSourceTypes.CustomerPayment, May));
        await context.SaveChangesAsync();

        var service = BuildTreasuryCanSeeCost(context);

        var aprilPage = await service.GetMovementsAsync(
            new TreasuryMovementsQuery { Year = 2026, Month = 4 }, CancellationToken.None);

        Assert.Equal(1, aprilPage.TotalCount);
        Assert.Equal(200m, aprilPage.Items.Single().Amount);
    }

    [Fact]
    public async Task Movements_NoMonth_ReturnsAll_HistoricBehavior()
    {
        await using var context = CreateContext();
        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 100m, CashLedgerSourceTypes.CustomerPayment, March),
            Ledger(CashMovementDirections.Income, 200m, CashLedgerSourceTypes.CustomerPayment, April),
            Ledger(CashMovementDirections.Income, 400m, CashLedgerSourceTypes.CustomerPayment, May));
        await context.SaveChangesAsync();

        var service = BuildTreasuryCanSeeCost(context);

        // Sin year/month: se devuelven todos (comportamiento historico, sin filtro de fecha).
        var allPage = await service.GetMovementsAsync(
            new TreasuryMovementsQuery(), CancellationToken.None);

        Assert.Equal(3, allPage.TotalCount);
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(2026, null)]
    [InlineData(null, 5)]
    public async Task Movements_InvalidYearMonth_Throws(int? year, int? month)
    {
        await using var context = CreateContext();
        var service = BuildTreasuryCanSeeCost(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetMovementsAsync(
                new TreasuryMovementsQuery { Year = year, Month = month }, CancellationToken.None));
    }

    [Fact]
    public async Task Movements_MaskingPreserved_WithMonth()
    {
        await using var context = CreateContext();
        context.CashLedgerEntries.Add(
            Ledger(CashMovementDirections.Expense, 120m, CashLedgerSourceTypes.SupplierPayment, March, supplierPaymentId: 1));
        await context.SaveChangesAsync();

        // Sin see_cost: el pago a proveedor del mes sigue apareciendo, pero con monto enmascarado a 0.
        var hidden = await BuildTreasuryNoCost(context).GetMovementsAsync(
            new TreasuryMovementsQuery { Year = 2026, Month = 3 }, CancellationToken.None);
        var shown = await BuildTreasuryCanSeeCost(context).GetMovementsAsync(
            new TreasuryMovementsQuery { Year = 2026, Month = 3 }, CancellationToken.None);

        Assert.Equal(0m, hidden.Items.Single(m => m.SourceType == "SupplierPayment").Amount);
        Assert.Equal(120m, shown.Items.Single(m => m.SourceType == "SupplierPayment").Amount);
    }
}
