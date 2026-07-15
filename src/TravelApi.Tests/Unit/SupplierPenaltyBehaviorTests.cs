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
/// Configuracion de multas de cancelacion (2026-07-14): tests del dato en el proveedor
/// (<see cref="Supplier.PenaltyBehavior"/>). Cubre el default, el round-trip en alta/edicion (mismo patron que
/// <see cref="SupplierTreasuryFxAssumedByOverrideTests"/>, campo espejo del mismo endpoint) y el rechazo de un
/// valor de enum fuera de rango.
/// </summary>
public class SupplierPenaltyBehaviorTests
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
    // Default: un Supplier nuevo, sin tocar el campo, queda Unknown ("no se sabe" — sin pista).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateWithoutPenaltyBehavior_DefaultsToUnknown()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador" }, CancellationToken.None);

        var read = await service.GetSupplierAsync(created.Id, CancellationToken.None);
        Assert.Equal(SupplierPenaltyBehavior.Unknown, read.PenaltyBehavior);
    }

    [Fact]
    public void EntityDefault_NewSupplierInstance_IsUnknown()
    {
        // Sin pasar por el servicio: el default vive en la propiedad de la entidad (C#), no en la BD.
        var supplier = new Supplier { Name = "Operador" };
        Assert.Equal(SupplierPenaltyBehavior.Unknown, supplier.PenaltyBehavior);
    }

    // ---------------------------------------------------------------------------------------------------
    // Round-trip en alta y edicion.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SupplierPenaltyBehavior.RarelyCharges)]
    [InlineData(SupplierPenaltyBehavior.UsuallyCharges)]
    public async Task CreateWithPenaltyBehavior_RoundTripsValue(SupplierPenaltyBehavior value)
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador", PenaltyBehavior = value }, CancellationToken.None);

        var read = await service.GetSupplierAsync(created.Id, CancellationToken.None);
        Assert.Equal(value, read.PenaltyBehavior);
    }

    [Fact]
    public async Task UpdateSupplierAsync_PersistsPenaltyBehaviorChange()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);
        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador" }, CancellationToken.None);

        // Arranca Unknown (default) -> se configura como "casi nunca cobra".
        await service.UpdateSupplierAsync(
            created.Id,
            new Supplier { Name = "Operador", PenaltyBehavior = SupplierPenaltyBehavior.RarelyCharges },
            CancellationToken.None);
        Assert.Equal(
            SupplierPenaltyBehavior.RarelyCharges,
            (await service.GetSupplierAsync(created.Id, CancellationToken.None)).PenaltyBehavior);

        // Cambia de opinion: "casi siempre cobra".
        await service.UpdateSupplierAsync(
            created.Id,
            new Supplier { Name = "Operador", PenaltyBehavior = SupplierPenaltyBehavior.UsuallyCharges },
            CancellationToken.None);
        Assert.Equal(
            SupplierPenaltyBehavior.UsuallyCharges,
            (await service.GetSupplierAsync(created.Id, CancellationToken.None)).PenaltyBehavior);

        // Vuelve a Unknown ("no se sabe" es un valor de negocio valido, no "el front no mando el campo") —
        // la ficha del operador tiene que poder deshacer la configuracion.
        await service.UpdateSupplierAsync(
            created.Id,
            new Supplier { Name = "Operador", PenaltyBehavior = SupplierPenaltyBehavior.Unknown },
            CancellationToken.None);
        Assert.Equal(
            SupplierPenaltyBehavior.Unknown,
            (await service.GetSupplierAsync(created.Id, CancellationToken.None)).PenaltyBehavior);
    }

    // ---------------------------------------------------------------------------------------------------
    // Rechazo de un valor de enum invalido (fuera de Unknown=0 / RarelyCharges=1 / UsuallyCharges=2). El
    // binder JSON no tiene JsonStringEnumConverter configurado en este proyecto, asi que un INT fuera de rango
    // podria colarse sin este chequeo explicito del servicio.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateSupplierAsync_WithInvalidPenaltyBehaviorValue_Throws()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateSupplierAsync(
                new Supplier { Name = "Operador", PenaltyBehavior = (SupplierPenaltyBehavior)99 },
                CancellationToken.None));

        Assert.Contains("cobra multa", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.Suppliers.CountAsync());
    }

    [Fact]
    public async Task UpdateSupplierAsync_WithInvalidPenaltyBehaviorValue_Throws()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);
        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador", PenaltyBehavior = SupplierPenaltyBehavior.RarelyCharges },
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateSupplierAsync(
                created.Id,
                new Supplier { Name = "Operador", PenaltyBehavior = (SupplierPenaltyBehavior)(-1) },
                CancellationToken.None));

        Assert.Contains("cobra multa", ex.Message, StringComparison.OrdinalIgnoreCase);
        // El intento invalido no debe haber tocado el valor existente.
        Assert.Equal(
            SupplierPenaltyBehavior.RarelyCharges,
            (await service.GetSupplierAsync(created.Id, CancellationToken.None)).PenaltyBehavior);
    }

    // ---------------------------------------------------------------------------------------------------
    // La FILA de la lista (SupplierListItemDto) trae el campo, para que un PUT con spread de la fila (ej. el
    // toggle de activo/inactivo) no lo borre a Unknown en silencio (mismo motivo que DefaultPaymentTermDays y
    // TreasuryFxAssumedByOverride, ver el fix ADR-044 T4).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetSuppliersAsync_ListItem_RoundTripsPenaltyBehavior()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        await service.CreateSupplierAsync(
            new Supplier { Name = "Operador configurado", PenaltyBehavior = SupplierPenaltyBehavior.UsuallyCharges },
            CancellationToken.None);

        var page = await service.GetSuppliersAsync(
            new SupplierListQuery { IncludeInactive = true }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(SupplierPenaltyBehavior.UsuallyCharges, item.PenaltyBehavior);
    }
}
