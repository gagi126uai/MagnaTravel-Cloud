using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Auditoria de negocio 2026-06-12 (item 7): el ranking de vendedores debe ser HONESTO.
/// Fija el comportamiento nuevo de GetSellerRankingAsync:
///  - atribuye por ResponsibleUserId (vendedor responsable), no por quien creo el file;
///  - mide ConfirmedSale (venta confirmada), no TotalSale (presupuesto);
///  - excluye Budget, Cancelled y PendingOperatorRefund;
///  - reservas sin responsable caen en el bucket "Sin asignar".
/// </summary>
public class SellerRankingHonestyTests
{
    [Fact]
    public async Task Ranking_AttributesToResponsibleSeller_NotFileCreator()
    {
        using var db = CreateDbContext();
        // Reserva cuyo responsable es "seller-A" (no importa quien la creo).
        db.Reservas.Add(ReservaConfirmedSale(1, responsibleUserId: "seller-A", responsibleName: "Vendedor A", confirmedSale: 1000m, totalCost: 600m));
        await db.SaveChangesAsync();

        var ranking = await BuildReports(db).GetSellerRankingAsync(null, null, CancellationToken.None);

        var row = Assert.Single(ranking);
        Assert.Equal("seller-A", row.UserId);
        Assert.Equal("Vendedor A", row.SellerName);
        Assert.Equal(1000m, row.TotalSales);
        Assert.Equal(600m, row.TotalCosts);
        Assert.Equal(400m, row.GrossMargin);
    }

    [Fact]
    public async Task Ranking_MeasuresConfirmedSale_NotBudgetTotalSale()
    {
        using var db = CreateDbContext();
        // TotalSale (presupuesto) = 5000, pero solo ConfirmedSale = 1200 es venta confirmada.
        var reserva = ReservaConfirmedSale(1, "seller-A", "Vendedor A", confirmedSale: 1200m, totalCost: 800m);
        reserva.TotalSale = 5000m;
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        var ranking = await BuildReports(db).GetSellerRankingAsync(null, null, CancellationToken.None);

        var row = Assert.Single(ranking);
        // El ranking refleja 1200 (confirmado), NO 5000 (presupuesto).
        Assert.Equal(1200m, row.TotalSales);
    }

    [Fact]
    public async Task Ranking_ReservaWithoutConfirmedServices_ContributesZeroSale()
    {
        using var db = CreateDbContext();
        // Reserva del vendedor sin nada confirmado todavia: ConfirmedSale = 0 aunque tenga presupuesto.
        var reserva = ReservaConfirmedSale(1, "seller-A", "Vendedor A", confirmedSale: 0m, totalCost: 0m);
        reserva.TotalSale = 3000m;
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        var ranking = await BuildReports(db).GetSellerRankingAsync(null, null, CancellationToken.None);

        var row = Assert.Single(ranking);
        Assert.Equal("seller-A", row.UserId);
        Assert.Equal(0m, row.TotalSales);
        // 1 reserva contada, pero aporta 0 a la venta.
        Assert.Equal(1, row.ReservasCreated);
    }

    [Fact]
    public async Task Ranking_ExcludesPendingOperatorRefund_AndBudget_AndCancelled()
    {
        using var db = CreateDbContext();
        db.Reservas.Add(ReservaConfirmedSale(1, "seller-A", "Vendedor A", 1000m, 600m, EstadoReserva.Confirmed));
        db.Reservas.Add(ReservaConfirmedSale(2, "seller-A", "Vendedor A", 2000m, 1000m, EstadoReserva.PendingOperatorRefund));
        db.Reservas.Add(ReservaConfirmedSale(3, "seller-A", "Vendedor A", 3000m, 1500m, EstadoReserva.Budget));
        db.Reservas.Add(ReservaConfirmedSale(4, "seller-A", "Vendedor A", 4000m, 2000m, EstadoReserva.Cancelled));
        await db.SaveChangesAsync();

        var ranking = await BuildReports(db).GetSellerRankingAsync(null, null, CancellationToken.None);

        var row = Assert.Single(ranking);
        // Solo la Confirmed (1000) cuenta. Las otras tres estan excluidas.
        Assert.Equal(1000m, row.TotalSales);
        Assert.Equal(1, row.ReservasCreated);
    }

    [Fact]
    public async Task Ranking_ReservasWithoutResponsible_BucketAsUnassigned()
    {
        using var db = CreateDbContext();
        db.Reservas.Add(ReservaConfirmedSale(1, responsibleUserId: null, responsibleName: null, confirmedSale: 700m, totalCost: 300m));
        await db.SaveChangesAsync();

        var ranking = await BuildReports(db).GetSellerRankingAsync(null, null, CancellationToken.None);

        var row = Assert.Single(ranking);
        Assert.Equal("Sin asignar", row.SellerName);
        Assert.Equal(700m, row.TotalSales);
    }

    [Fact]
    public async Task Ranking_GroupsMultipleReservasPerSeller()
    {
        using var db = CreateDbContext();
        db.Reservas.Add(ReservaConfirmedSale(1, "seller-A", "Vendedor A", 1000m, 600m));
        db.Reservas.Add(ReservaConfirmedSale(2, "seller-A", "Vendedor A", 500m, 200m));
        db.Reservas.Add(ReservaConfirmedSale(3, "seller-B", "Vendedor B", 800m, 500m));
        await db.SaveChangesAsync();

        var ranking = await BuildReports(db).GetSellerRankingAsync(null, null, CancellationToken.None);

        Assert.Equal(2, ranking.Count);
        // Orden descendente por venta confirmada: A (1500) antes que B (800).
        Assert.Equal("seller-A", ranking[0].UserId);
        Assert.Equal(1500m, ranking[0].TotalSales);
        Assert.Equal(2, ranking[0].ReservasCreated);
        Assert.Equal("seller-B", ranking[1].UserId);
        Assert.Equal(800m, ranking[1].TotalSales);
    }

    // ===== helpers =====

    private static Reserva ReservaConfirmedSale(
        int id,
        string? responsibleUserId,
        string? responsibleName,
        decimal confirmedSale,
        decimal totalCost,
        string status = EstadoReserva.Confirmed) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        NumeroReserva = $"R-{id}",
        Name = $"Reserva {id}",
        Status = status,
        ResponsibleUserId = responsibleUserId,
        ResponsibleUserName = responsibleName,
        ConfirmedSale = confirmedSale,
        TotalCost = totalCost,
        // Dentro del periodo por defecto (desde 1-ene del anio actual): usamos "ahora".
        CreatedAt = DateTime.UtcNow
    };

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static ReportService BuildReports(AppDbContext db)
    {
        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((BnaUsdSellerRateDto?)null);
        return new ReportService(db, bna.Object);
    }
}
