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
/// FIX #41, pieza backend (Tanda 3 del barrido de PROD, 2026-07-23): el parametro <c>search</c> de
/// <c>TreasuryService.GetMovementsAsync</c> (buscador de la pantalla de Caja) nunca tuvo un test propio.
/// Este archivo blinda el invariante "items.Count coincide con totalCount cuando el search filtra a un
/// subconjunto" — count y pagina se calculan sobre la MISMA IQueryable ya filtrada
/// (<c>PagedQueryExtensions.ToPagedResponseAsync</c>), asi que no deberia haber fan-out por los joins de
/// navegacion (todos son FK 1:1, no colecciones). Si algun dia alguien agrega un <c>Include</c> de
/// coleccion aca, este test lo va a pintar.
/// </summary>
public class TreasuryServiceMovementsSearchTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static TreasuryService CreateService(AppDbContext context)
        => new(context, Mock.Of<IEntityReferenceResolver>());

    private static async Task<ManualCashMovement> SeedManualMovementEntryAsync(
        AppDbContext context, string description, DateTime occurredAt)
    {
        var movement = new ManualCashMovement
        {
            Direction = CashMovementDirections.Expense,
            Amount = 100m,
            Category = "Otros",
            Description = description,
            OccurredAt = occurredAt,
        };
        context.ManualCashMovements.Add(movement);
        await context.SaveChangesAsync();

        context.CashLedgerEntries.Add(new CashLedgerEntry
        {
            Direction = CashMovementDirections.Expense,
            Amount = 100m,
            Currency = "ARS",
            Method = "Cash",
            OccurredAt = occurredAt,
            SourceType = CashLedgerSourceTypes.ManualAdjustment,
            ManualCashMovementId = movement.Id,
        });
        await context.SaveChangesAsync();

        return movement;
    }

    [Fact]
    public async Task Search_que_filtra_a_un_subconjunto_ItemsCountCoincideConTotalCount()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        await SeedManualMovementEntryAsync(context, "Gasto de imprenta alfa", now.AddDays(-1));
        await SeedManualMovementEntryAsync(context, "Gasto de limpieza beta", now.AddDays(-2));
        await SeedManualMovementEntryAsync(context, "Otro gasto alfa numero dos", now.AddDays(-3));

        var service = CreateService(context);
        var page = await service.GetMovementsAsync(
            new TreasuryMovementsQuery { Search = "alfa", PageSize = 25 }, CancellationToken.None);

        // Las DOS filas "alfa" matchean; la de "beta" queda afuera.
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, m => Assert.Contains("alfa", m.Description, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_sin_coincidencias_DevuelveVacioConsistente()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        await SeedManualMovementEntryAsync(context, "Gasto de imprenta", now);

        var service = CreateService(context);
        var page = await service.GetMovementsAsync(
            new TreasuryMovementsQuery { Search = "inexistente-zzz", PageSize = 25 }, CancellationToken.None);

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Search_vacio_no_filtra_TraeTodosLosMovimientos()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        await SeedManualMovementEntryAsync(context, "Gasto uno", now.AddDays(-1));
        await SeedManualMovementEntryAsync(context, "Gasto dos", now.AddDays(-2));

        var service = CreateService(context);
        var page = await service.GetMovementsAsync(
            new TreasuryMovementsQuery { Search = null, PageSize = 25 }, CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
    }
}
