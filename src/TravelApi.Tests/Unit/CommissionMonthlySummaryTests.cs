using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Auditoria ERP 2026-06-13 (decision del dueño): tests del resumen MENSUAL de comisiones por vendedor
/// (pantalla "Comisiones", admin-only). Cubre: agrupacion por vendedor + moneda, filtro por mes (sobre
/// CreatedAt), exclusion de filas en 0 (tope cero), y validacion del periodo. Usa InMemory: las filas
/// CommissionAccrual se siembran directo (no se ejercita el persister, eso lo cubre SellerCommissionAccrualTests).
/// </summary>
public class CommissionMonthlySummaryTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static CommissionAccrual Accrual(string sellerId, string sellerName, string currency, decimal amount, DateTime createdAt)
        => new()
        {
            SellerUserId = sellerId,
            SellerName = sellerName,
            ReservaId = 1,
            Currency = currency,
            Amount = amount,
            RatePercent = 10m,
            Status = CommissionAccrualStatus.Devengada,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

    [Fact]
    public async Task MonthlySummary_GroupsBySellerAndCurrency_SumsAmounts()
    {
        await using var db = NewContext();
        var jun = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);

        db.CommissionAccruals.AddRange(
            Accrual("seller-1", "Ana", "ARS", 100m, jun),
            Accrual("seller-1", "Ana", "ARS", 50m, jun),   // misma moneda -> suma a 150
            Accrual("seller-1", "Ana", "USD", 20m, jun),   // otra moneda -> renglon aparte
            Accrual("seller-2", "Beto", "ARS", 70m, jun));
        await db.SaveChangesAsync();

        var service = new CommissionService(db);
        var summary = await service.GetMonthlySummaryAsync(2026, 6, CancellationToken.None);

        Assert.Equal(2026, summary.Year);
        Assert.Equal(6, summary.Month);
        Assert.Equal(2, summary.Sellers.Count);

        var ana = summary.Sellers.Single(s => s.SellerUserId == "seller-1");
        Assert.Equal(150m, ana.TotalsByCurrency.Single(t => t.Currency == "ARS").Amount);
        Assert.Equal(20m, ana.TotalsByCurrency.Single(t => t.Currency == "USD").Amount);

        var beto = summary.Sellers.Single(s => s.SellerUserId == "seller-2");
        Assert.Equal(70m, Assert.Single(beto.TotalsByCurrency).Amount);
    }

    [Fact]
    public async Task MonthlySummary_ExcludesOtherMonths()
    {
        await using var db = NewContext();
        db.CommissionAccruals.AddRange(
            Accrual("seller-1", "Ana", "ARS", 100m, new DateTime(2026, 6, 30, 23, 0, 0, DateTimeKind.Utc)), // junio
            Accrual("seller-1", "Ana", "ARS", 999m, new DateTime(2026, 7, 1, 1, 0, 0, DateTimeKind.Utc)),   // julio (fuera)
            Accrual("seller-1", "Ana", "ARS", 999m, new DateTime(2026, 5, 31, 23, 0, 0, DateTimeKind.Utc))); // mayo (fuera)
        await db.SaveChangesAsync();

        var service = new CommissionService(db);
        var summary = await service.GetMonthlySummaryAsync(2026, 6, CancellationToken.None);

        var ana = Assert.Single(summary.Sellers);
        Assert.Equal(100m, Assert.Single(ana.TotalsByCurrency).Amount);
    }

    [Fact]
    public async Task MonthlySummary_ExcludesZeroedAccruals()
    {
        // Tope cero: una comision revertida a 0 (cancelacion / saldo positivo) NO debe figurar en el resumen.
        await using var db = NewContext();
        var jun = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        db.CommissionAccruals.AddRange(
            Accrual("seller-1", "Ana", "ARS", 0m, jun),
            Accrual("seller-2", "Beto", "ARS", 80m, jun));
        await db.SaveChangesAsync();

        var service = new CommissionService(db);
        var summary = await service.GetMonthlySummaryAsync(2026, 6, CancellationToken.None);

        var seller = Assert.Single(summary.Sellers);
        Assert.Equal("seller-2", seller.SellerUserId);
    }

    [Fact]
    public async Task MonthlySummary_EmptyMonth_ReturnsNoSellers()
    {
        await using var db = NewContext();
        var service = new CommissionService(db);

        var summary = await service.GetMonthlySummaryAsync(2026, 6, CancellationToken.None);

        Assert.Empty(summary.Sellers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public async Task MonthlySummary_InvalidMonth_Throws(int month)
    {
        await using var db = NewContext();
        var service = new CommissionService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetMonthlySummaryAsync(2026, month, CancellationToken.None));
    }
}
