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

    // ================================================================================================
    // H14 (barrido E2E 2026-07-25): el contra-asiento de una anulacion/edicion no traia ninguna marca
    // visual — el front no podia distinguir "esta fila sigue vigente" de "esta fila ya quedo sin efecto".
    // Estos tests blindan que GetMovementsAsync expone (a) PublicId propio de CADA fila (no el del
    // origen, que el manual y su contra-asiento COMPARTEN) y (b) IsAnnulled=true en AMBAS filas del par.
    // ================================================================================================

    [Fact]
    public async Task GetMovementsAsync_MovimientoManualSinAnular_TraePublicIdPropioEIsAnnulledFalse()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        var created = await service.CreateManualMovementAsync(
            new UpsertManualCashMovementRequest
            {
                Direction = CashMovementDirections.Expense,
                Amount = 500m,
                OccurredAt = DateTime.UtcNow,
                Method = "Cash",
                Category = "Otros",
                Description = "Gasto vigente",
            },
            createdBy: "cajero-1",
            CancellationToken.None);

        var page = await service.GetMovementsAsync(
            new TreasuryMovementsQuery { PageSize = 25 }, CancellationToken.None);

        var row = Assert.Single(page.Items);
        Assert.False(row.IsAnnulled);
        Assert.NotEqual(Guid.Empty, row.PublicId);
        // El PublicId de la FILA no es el mismo que el PublicId del movimiento manual (origen):
        // son identificadores de cosas distintas (el asiento vs. el movimiento que lo origino).
        Assert.NotEqual(created.PublicId, row.PublicId);
    }

    [Fact]
    public async Task GetMovementsAsync_TrasEditarUnManual_ElParOriginalYContraAsiento_QuedanMarcadosAnulados()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        var created = await service.CreateManualMovementAsync(
            new UpsertManualCashMovementRequest
            {
                Direction = CashMovementDirections.Expense,
                Amount = 500m,
                OccurredAt = DateTime.UtcNow,
                Method = "Cash",
                Category = "Otros",
                Description = "Gasto original",
            },
            createdBy: "cajero-1",
            CancellationToken.None);

        // Editar el monto de un manual reversa el asiento viejo e inserta uno nuevo (ADR-022 §4.5):
        // el resultado son 3 filas en el Libro de Caja (original anulado, contra-asiento, asiento nuevo).
        var manualEntity = await context.ManualCashMovements.SingleAsync(m => m.PublicId == created.PublicId);
        await service.UpdateManualMovementAsync(
            manualEntity.Id,
            new UpsertManualCashMovementRequest
            {
                Direction = CashMovementDirections.Expense,
                Amount = 800m, // distinto -> dispara la reversa (mismo monto no dispara nada nuevo)
                OccurredAt = DateTime.UtcNow,
                Method = "Cash",
                Category = "Otros",
                Description = "Gasto corregido",
            },
            CancellationToken.None);

        var page = await service.GetMovementsAsync(
            new TreasuryMovementsQuery { PageSize = 25 }, CancellationToken.None);

        Assert.Equal(3, page.Items.Count);

        // Se distinguen las 3 filas por Direction/Amount (no por orden, que depende del reloj):
        //  - original: Expense $500 (el asiento viejo, ahora reemplazado).
        //  - contra-asiento: Income $500 (CashLedgerEntryFactory.Reverse INVIERTE la Direction).
        //  - nuevo: Expense $800 (el monto corregido, post-edicion).
        var original = Assert.Single(page.Items, m => m.Direction == CashMovementDirections.Expense && m.Amount == 500m);
        var contraAsiento = Assert.Single(page.Items, m => m.Direction == CashMovementDirections.Income);
        var nuevo = Assert.Single(page.Items, m => m.Direction == CashMovementDirections.Expense && m.Amount == 800m);

        Assert.True(original.IsAnnulled, "el asiento viejo quedo reemplazado: debe marcarse anulado.");
        Assert.True(contraAsiento.IsAnnulled, "el contra-asiento tambien debe marcarse anulado (es el reverso, no un movimiento vivo).");
        Assert.False(nuevo.IsAnnulled, "el asiento nuevo (post-edicion) sigue vigente.");

        // Las 3 filas son asientos DISTINTOS: cada una con su propio PublicId, aunque el original y el
        // contra-asiento compartan el mismo SourcePublicId (los dos apuntan al mismo ManualCashMovement).
        Assert.NotEqual(original.PublicId, contraAsiento.PublicId);
        Assert.NotEqual(contraAsiento.PublicId, nuevo.PublicId);
        Assert.Equal(original.SourcePublicId, contraAsiento.SourcePublicId);
    }
}
