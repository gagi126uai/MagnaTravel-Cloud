using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Decision firmada 2026-08-19 ("descalce devolución-caja", punto 2): un movimiento de caja que ES la
/// devolución de un operador NO se edita ni se borra desde Tesorería (T-10, el guard vive en el servidor).
/// Cubre el camino REAL de escritura (<c>ManualCashMovement</c> con <c>OperatorRefundReceivedId</c> y su
/// <see cref="CashLedgerEntry"/> con <c>ManualCashMovementId</c> — igual que <c>OperatorRefundService</c>
/// asienta un ingreso real), no el atajo de otros tests de este proyecto que setean
/// <c>CashLedgerEntry.OperatorRefundReceivedId</c> directo (columna sin uso en producción, ver
/// <c>CashLedgerRefundLedgerAmountLoader</c>).
/// </summary>
public class TreasuryServiceOperatorRefundLinkGuardTests
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

    /// <summary>Siembra el movimiento de caja de UNA devolución de operador, con la forma REAL de escritura
    /// (ManualCashMovement -&gt; CashLedgerEntry via ManualCashMovementId, nunca via OperatorRefundReceivedId
    /// directo en el asiento). Devuelve el Id del ManualCashMovement a editar/borrar en el test.</summary>
    private static async Task<int> SeedOperatorRefundLinkedMovementAsync(AppDbContext ctx, string numeroReserva)
    {
        var reserva = new Reserva { NumeroReserva = numeroReserva, Name = numeroReserva, Status = EstadoReserva.Cancelled };
        ctx.Reservas.Add(reserva);
        var customer = new Customer { FullName = "Cliente Guard", IsActive = true };
        var supplier = new Supplier { Name = "Operador Guard", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund, Reason = "test guard tesoreria",
            DraftedByUserId = "vendedor-1",
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var refund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id, ReceivedAmount = 1000m, AllocatedAmount = 1000m, Currency = "ARS",
            Method = "Transfer", ReceivedByUserId = "cajero-1", ReceivedByUserName = "Cajero Uno",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        ctx.OperatorRefundAllocations.Add(new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id, BookingCancellationId = bc.Id,
            GrossAmount = 1000m, NetAmount = 1000m, IsVoided = false, CreatedByUserId = "cajero-1",
        });

        var movement = new ManualCashMovement
        {
            Direction = CashMovementDirections.Income, Amount = 1000m, Currency = "ARS",
            OccurredAt = DateTime.UtcNow, Method = "Transfer", Category = "OperatorRefund",
            Description = $"Devolucion del operador {supplier.Name}", CreatedBy = "cajero-1",
            OperatorRefundReceivedId = refund.Id, RelatedReservaId = null, // BuildIncomeForRefund lo deja null a proposito
        };
        ctx.ManualCashMovements.Add(movement);
        await ctx.SaveChangesAsync();

        ctx.CashLedgerEntries.Add(new CashLedgerEntry
        {
            Direction = CashMovementDirections.Income, Amount = 1000m, Currency = "ARS", Method = "Transfer",
            OccurredAt = DateTime.UtcNow, SourceType = CashLedgerSourceTypes.OperatorRefund,
            ManualCashMovementId = movement.Id, // forma REAL: nunca OperatorRefundReceivedId directo.
        });
        await ctx.SaveChangesAsync();

        return movement.Id;
    }

    [Fact]
    public async Task Update_MovimientoLigadoADevolucionDeOperador_RechazaConCodeYNumeroDeReserva()
    {
        await using var ctx = CreateContext();
        var movementId = await SeedOperatorRefundLinkedMovementAsync(ctx, "F-2026-9001");
        var service = CreateService(ctx);

        var ex = await Assert.ThrowsAsync<CashMovementLinkedToOperatorRefundException>(() =>
            service.UpdateManualMovementAsync(
                movementId,
                new UpsertManualCashMovementRequest
                {
                    Direction = CashMovementDirections.Income, Amount = 1200m, OccurredAt = DateTime.UtcNow,
                    Method = "Transfer", Category = "OperatorRefund", Description = "intento de edicion",
                },
                CancellationToken.None));

        // Code es un const estable (T-1), el controller lo lee del TIPO (no de la instancia) — mismo
        // patron que UndoAnnulmentBlockedException. Igual dejamos constancia del valor esperado aca.
        Assert.Equal("CASH_MOVEMENT_LINKED_TO_OPERATOR_REFUND", CashMovementLinkedToOperatorRefundException.Code);
        Assert.Contains("F-2026-9001", ex.Message);
        Assert.Contains("deshacé la devolución desde la ficha del operador", ex.Message);
    }

    [Fact]
    public async Task Delete_MovimientoLigadoADevolucionDeOperador_RechazaConCodeYNumeroDeReserva()
    {
        await using var ctx = CreateContext();
        var movementId = await SeedOperatorRefundLinkedMovementAsync(ctx, "F-2026-9002");
        var service = CreateService(ctx);

        var ex = await Assert.ThrowsAsync<CashMovementLinkedToOperatorRefundException>(() =>
            service.DeleteManualMovementAsync(movementId, CancellationToken.None));

        Assert.Equal("CASH_MOVEMENT_LINKED_TO_OPERATOR_REFUND", CashMovementLinkedToOperatorRefundException.Code);
        Assert.Contains("F-2026-9002", ex.Message);

        // Ademas: el movimiento NO debe haber quedado tocado (el guard corta ANTES de mutar nada).
        var entity = await ctx.ManualCashMovements.SingleAsync(m => m.Id == movementId);
        Assert.False(entity.IsVoided);
    }

    [Fact]
    public async Task Update_MovimientoManualComun_NoLigadoAReembolso_SePermiteNormalmente()
    {
        // Control negativo: un gasto/ajuste comun (sin CashLedgerEntry de SourceType OperatorRefund) sigue
        // pudiendo editarse igual que siempre — el guard no debe frenar movimientos que no tienen nada que
        // ver con una devolucion de operador.
        await using var ctx = CreateContext();
        var service = CreateService(ctx);

        var created = await service.CreateManualMovementAsync(
            new UpsertManualCashMovementRequest
            {
                Direction = CashMovementDirections.Expense, Amount = 300m, OccurredAt = DateTime.UtcNow,
                Method = "Cash", Category = "Otros", Description = "Gasto de imprenta",
            },
            createdBy: "cajero-1", CancellationToken.None);

        var manualEntity = await ctx.ManualCashMovements.SingleAsync(m => m.PublicId == created.PublicId);

        var updated = await service.UpdateManualMovementAsync(
            manualEntity.Id,
            new UpsertManualCashMovementRequest
            {
                Direction = CashMovementDirections.Expense, Amount = 350m, OccurredAt = DateTime.UtcNow,
                Method = "Cash", Category = "Otros", Description = "Gasto de imprenta corregido",
            },
            CancellationToken.None);

        Assert.Equal(350m, updated.Amount);
    }

    [Fact]
    public async Task Delete_MovimientoManualComun_NoLigadoAReembolso_SePermiteNormalmente()
    {
        await using var ctx = CreateContext();
        var service = CreateService(ctx);

        var created = await service.CreateManualMovementAsync(
            new UpsertManualCashMovementRequest
            {
                Direction = CashMovementDirections.Expense, Amount = 300m, OccurredAt = DateTime.UtcNow,
                Method = "Cash", Category = "Otros", Description = "Gasto de imprenta",
            },
            createdBy: "cajero-1", CancellationToken.None);

        var manualEntity = await ctx.ManualCashMovements.SingleAsync(m => m.PublicId == created.PublicId);

        await service.DeleteManualMovementAsync(manualEntity.Id, CancellationToken.None);

        var reloaded = await ctx.ManualCashMovements.SingleAsync(m => m.Id == manualEntity.Id);
        Assert.True(reloaded.IsVoided);
    }
}
