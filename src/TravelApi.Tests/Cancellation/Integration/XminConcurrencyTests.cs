using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1 (ADR-002 §2.5 / B11, 2026-05-14): valida que el concurrency token <c>xmin</c>
/// configurado via <c>UseXminAsConcurrencyToken()</c> efectivamente impide que dos
/// sesiones concurrentes modifiquen el mismo aggregate sin enterarse.
///
/// Por que es critico:
///  - Sin lock optimista, dos cashiers podrian registrar allocations del mismo
///    refund del operador en paralelo y duplicar dinero (INV-114 / INV-085).
///  - El cliente podria retirar mas de su saldo si dos cajas trabajan a la vez.
///  - <c>xmin</c> es el id de transaccion que ultimo modifico la fila — Postgres
///    lo mantiene gratis y EF lo lee como <c>RowVersion</c>.
///
/// Patron determinista (sin <c>Thread.Sleep</c>):
///   1. Context A carga el aggregate.
///   2. Context B carga el MISMO aggregate.
///   3. Context A modifica y persiste -&gt; OK, <c>xmin</c> avanza.
///   4. Context B modifica y persiste -&gt; <see cref="DbUpdateConcurrencyException"/>
///      porque el <c>xmin</c> que cargo ya no coincide con el de la fila.
///
/// Los 2 contexts vienen de la fixture (<c>CreateDbContext</c>) para asegurar
/// instancias independientes — EF Core 8 no permite que una entidad este tracked
/// en dos contexts a la vez.
/// </summary>
[Trait("Category", "Integration")]
public sealed class XminConcurrencyTests : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public XminConcurrencyTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Cleanup pre-test: los 374 tests existentes son independientes; mantenemos
    /// la misma garantia aca. La fixture es compartida por la clase asi que el
    /// reset corre antes de cada metodo, no al inicializar la clase.
    /// </summary>
    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BookingCancellation_TwoConcurrentUpdates_SecondShouldThrowDbUpdateConcurrencyException()
    {
        // ARRANGE: persistir un BookingCancellation con FiscalSnapshot completo.
        // El test no valida transiciones de estado — solo que xmin proteja la fila.
        int bcId;
        await using (var setup = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(setup);
            var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId);
            setup.BookingCancellations.Add(bc);
            await setup.SaveChangesAsync();
            bcId = bc.Id;
        }

        // ACT: dos contexts cargan la misma fila y la modifican.
        // ctxA persiste primero (xmin avanza en la BD), ctxB persiste con el xmin
        // viejo y debe explotar.
        await using var ctxA = _fixture.CreateDbContext();
        await using var ctxB = _fixture.CreateDbContext();

        var bcInA = await ctxA.BookingCancellations.FirstAsync(b => b.Id == bcId);
        var bcInB = await ctxB.BookingCancellations.FirstAsync(b => b.Id == bcId);

        bcInA.EstimatedRefundAmount = 700m;
        await ctxA.SaveChangesAsync();

        bcInB.EstimatedRefundAmount = 600m;

        // ASSERT: el segundo SaveChanges debe disparar el conflicto optimista.
        // Si esta excepcion NO se lanza, significa que UseXminAsConcurrencyToken
        // no esta aplicandose -> dinero perdido bajo concurrencia.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctxB.SaveChangesAsync());
    }

    [Fact]
    public async Task OperatorRefundReceived_TwoConcurrentUpdates_SecondShouldThrowDbUpdateConcurrencyException()
    {
        // ARRANGE: persistir un OperatorRefundReceived con monto recibido > 0
        // para poder simular dos asignaciones concurrentes que modifiquen
        // AllocatedAmount (el escenario real de INV-114).
        int refundId;
        await using (var setup = _fixture.CreateDbContext())
        {
            var (_, supId, _, _) = await CancellationTestData.SeedBaseAsync(setup);

            var refund = new OperatorRefundReceived
            {
                SupplierId = supId,
                ReceivedAt = DateTime.UtcNow,
                ReceivedAmount = 1000m,
                AllocatedAmount = 0m,
                Method = "Transfer",
                Currency = "ARS",
                ExchangeRateAtReceipt = 1m,
                ReceivedByUserId = "tester",
                ReceivedByUserName = "Tester",
            };
            setup.OperatorRefundReceived.Add(refund);
            await setup.SaveChangesAsync();
            refundId = refund.Id;
        }

        // ACT: dos cashiers asignan al mismo tiempo (race condition real).
        await using var ctxA = _fixture.CreateDbContext();
        await using var ctxB = _fixture.CreateDbContext();

        var refundA = await ctxA.OperatorRefundReceived.FirstAsync(r => r.Id == refundId);
        var refundB = await ctxB.OperatorRefundReceived.FirstAsync(r => r.Id == refundId);

        refundA.AllocatedAmount = 400m;
        await ctxA.SaveChangesAsync();

        refundB.AllocatedAmount = 500m;

        // ASSERT: el segundo update se rechaza por xmin desactualizado.
        // En produccion, OperatorRefundService captura esta excepcion y reintenta
        // recargando el aggregate — sin xmin, los $400 de A se perderian.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctxB.SaveChangesAsync());
    }

    [Fact]
    public async Task ClientCreditEntry_TwoConcurrentUpdates_SecondShouldThrowDbUpdateConcurrencyException()
    {
        // ARRANGE: para crear un ClientCreditEntry necesitamos toda la cadena
        // BC -> Refund -> Allocation -> Entry. Sin allocation real la FK falla.
        int entryId;
        await using (var setup = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(setup);

            var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId);
            setup.BookingCancellations.Add(bc);

            var refund = new OperatorRefundReceived
            {
                SupplierId = supId,
                ReceivedAt = DateTime.UtcNow,
                ReceivedAmount = 1000m,
                AllocatedAmount = 500m,
                Method = "Transfer",
                Currency = "ARS",
                ExchangeRateAtReceipt = 1m,
                ReceivedByUserId = "tester",
                ReceivedByUserName = "Tester",
            };
            setup.OperatorRefundReceived.Add(refund);
            await setup.SaveChangesAsync();

            var alloc = new OperatorRefundAllocation
            {
                OperatorRefundReceivedId = refund.Id,
                BookingCancellationId = bc.Id,
                GrossAmount = 500m,
                NetAmount = 500m,
                IsVoided = false,
                CreatedByUserId = "tester",
                CreatedAt = DateTime.UtcNow,
            };
            setup.OperatorRefundAllocations.Add(alloc);
            await setup.SaveChangesAsync();

            var entry = new ClientCreditEntry
            {
                CustomerId = custId,
                OperatorRefundAllocationId = alloc.Id,
                BookingCancellationId = bc.Id,
                CreditedAmount = 500m,
                RemainingBalance = 500m,
                IsFullyConsumed = false,
                CreatedAt = DateTime.UtcNow,
            };
            setup.ClientCreditEntries.Add(entry);
            await setup.SaveChangesAsync();
            entryId = entry.Id;
        }

        // ACT: dos cajas que registran retiros del MISMO cliente al mismo tiempo.
        // Cada caja decrementa el saldo; sin xmin, ambas restarian sobre el mismo
        // valor base y el cliente podria retirar mas que su credito (INV-085).
        await using var ctxA = _fixture.CreateDbContext();
        await using var ctxB = _fixture.CreateDbContext();

        var entryA = await ctxA.ClientCreditEntries.FirstAsync(e => e.Id == entryId);
        var entryB = await ctxB.ClientCreditEntries.FirstAsync(e => e.Id == entryId);

        entryA.RemainingBalance = 300m;
        await ctxA.SaveChangesAsync();

        entryB.RemainingBalance = 200m;

        // ASSERT: segundo SaveChanges -> conflicto. Si paso, el saldo del cliente
        // ya no es consistente y se rompe la garantia INV-085.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctxB.SaveChangesAsync());
    }
}
