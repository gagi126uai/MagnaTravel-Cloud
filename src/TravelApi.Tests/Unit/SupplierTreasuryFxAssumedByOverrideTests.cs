using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-044 T3b Decision 3 / T4 (2026-07-10): la excepcion OPCIONAL por operador de "quién asume el ajuste por
/// el dólar" en sus multas (<see cref="Supplier.TreasuryFxAssumedByOverride"/>). Cubre: round-trip completo en
/// alta/edicion (setear Agency, setear Client, y volver a null = "hereda el default general"), y el rechazo de
/// un valor de enum invalido (el binder JSON no tiene un <c>JsonStringEnumConverter</c> configurado en este
/// proyecto, asi que un INT fuera de rango podria colarse sin la validacion explicita del servicio).
///
/// <para>Mismo patron que <see cref="SupplierDefaultPaymentTermDaysTests"/>: ejercita <c>SupplierService</c>
/// directo contra InMemory, sin pasar por el controller (el mapeo del controller es un pass-through trivial).</para>
/// </summary>
public class SupplierTreasuryFxAssumedByOverrideTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    // ---------------------------------------------------------------------------------------------------
    // Round-trip: null (hereda) es el default de un alta sin tocar el campo.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateWithoutOverride_DefaultsToNull_InheritsAgencyConfig()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador" }, CancellationToken.None);

        var read = await service.GetSupplierAsync(created.Id, CancellationToken.None);
        Assert.Null(read.TreasuryFxAssumedByOverride);
    }

    [Fact]
    public async Task CreateWithOverride_RoundTripsValue()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador", TreasuryFxAssumedByOverride = TreasuryFxAssumedBy.Agency },
            CancellationToken.None);

        var read = await service.GetSupplierAsync(created.Id, CancellationToken.None);
        Assert.Equal(TreasuryFxAssumedBy.Agency, read.TreasuryFxAssumedByOverride);
    }

    // ---------------------------------------------------------------------------------------------------
    // Round-trip en edicion: setear, cambiar, y volver a null (limpia la excepcion, vuelve a heredar).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateSupplierAsync_CanSetChangeAndClearOverride()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);
        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador" }, CancellationToken.None);

        // Setear "lo asume la agencia".
        await service.UpdateSupplierAsync(
            created.Id,
            new Supplier { Name = "Operador", TreasuryFxAssumedByOverride = TreasuryFxAssumedBy.Agency },
            CancellationToken.None);
        Assert.Equal(
            TreasuryFxAssumedBy.Agency,
            (await service.GetSupplierAsync(created.Id, CancellationToken.None)).TreasuryFxAssumedByOverride);

        // Cambiar a "lo asume el cliente" (apartarse en la otra direccion, si el default general fuera Agency).
        await service.UpdateSupplierAsync(
            created.Id,
            new Supplier { Name = "Operador", TreasuryFxAssumedByOverride = TreasuryFxAssumedBy.Client },
            CancellationToken.None);
        Assert.Equal(
            TreasuryFxAssumedBy.Client,
            (await service.GetSupplierAsync(created.Id, CancellationToken.None)).TreasuryFxAssumedByOverride);

        // Volver a null ("Como la configuración general"): a diferencia de DefaultCurrency, mandar null AQUI
        // SI limpia la excepcion (es un valor de negocio valido, no "el front no mando el campo").
        await service.UpdateSupplierAsync(
            created.Id,
            new Supplier { Name = "Operador", TreasuryFxAssumedByOverride = null },
            CancellationToken.None);
        Assert.Null(
            (await service.GetSupplierAsync(created.Id, CancellationToken.None)).TreasuryFxAssumedByOverride);
    }

    // ---------------------------------------------------------------------------------------------------
    // Rechazo de un valor de enum invalido (fuera de Client=0 / Agency=1).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateSupplierAsync_WithInvalidOverrideValue_Throws()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateSupplierAsync(
                new Supplier { Name = "Operador", TreasuryFxAssumedByOverride = (TreasuryFxAssumedBy)99 },
                CancellationToken.None));

        Assert.Contains("ajuste por el dólar", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.Suppliers.CountAsync());
    }

    [Fact]
    public async Task UpdateSupplierAsync_WithInvalidOverrideValue_Throws()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);
        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador" }, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateSupplierAsync(
                created.Id,
                new Supplier { Name = "Operador", TreasuryFxAssumedByOverride = (TreasuryFxAssumedBy)(-1) },
                CancellationToken.None));

        Assert.Contains("ajuste por el dólar", ex.Message, StringComparison.OrdinalIgnoreCase);
        // El intento invalido no debe haber tocado el valor existente (que seguia en null).
        Assert.Null(
            (await service.GetSupplierAsync(created.Id, CancellationToken.None)).TreasuryFxAssumedByOverride);
    }

    // Nunca se mencionan "diferencia de cambio" en ningun mensaje que le llegue al usuario (regla dura de
    // multimoneda, 2026-06-09) — este mismo mensaje de rechazo se verifica en OperationalFinanceSettingsService
    // (config general) via su propio test suite; aca solo se verifica la variante por operador.

    // ---------------------------------------------------------------------------------------------------
    // ADR-044 T4 (fix backend-reviewer): la FILA de la lista (SupplierListItemDto) trae AMBOS campos, para
    // que el front pueda hacer PUT con spread de la fila (toggle de estado) SIN borrarlos a null.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetSuppliersAsync_ListItem_RoundTripsOverrideAndPaymentTerm()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        await service.CreateSupplierAsync(
            new Supplier
            {
                Name = "Operador con excepción",
                DefaultPaymentTermDays = 30,
                TreasuryFxAssumedByOverride = TreasuryFxAssumedBy.Agency,
            },
            CancellationToken.None);

        var page = await service.GetSuppliersAsync(
            new SupplierListQuery { IncludeInactive = true }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(30, item.DefaultPaymentTermDays);
        Assert.Equal(TreasuryFxAssumedBy.Agency, item.TreasuryFxAssumedByOverride);
    }

    [Fact]
    public async Task GetSuppliersAsync_ListItem_NullOverrideAndPaymentTerm_RoundTripAsNull()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        // Operador sin excepción ni plazo: la fila los trae explicitamente como null (no ausentes), para que el
        // front distinga "hereda / sin plazo" y NO rompa el spread.
        await service.CreateSupplierAsync(new Supplier { Name = "Operador simple" }, CancellationToken.None);

        var page = await service.GetSuppliersAsync(
            new SupplierListQuery { IncludeInactive = true }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Null(item.DefaultPaymentTermDays);
        Assert.Null(item.TreasuryFxAssumedByOverride);
    }
}
