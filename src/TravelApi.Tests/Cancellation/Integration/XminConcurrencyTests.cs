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

    /// <summary>
    /// FC1.3.0a (ADR-009 §2.2 punto 10 / §6.2 / RH-006, 2026-05-21): valida que
    /// la migracion M0 (<c>FC1_3_PRE_AddApprovalRequestConcurrencyToken</c>)
    /// haya activado el concurrency token <c>xmin</c> en <c>ApprovalRequests</c>.
    ///
    /// Escenario real (que FC1.3 va a habilitar):
    ///   - Admin A abre la bandeja y empieza a editar el Metadata de un approval
    ///     pending (recalcula la liquidacion partial NC).
    ///   - Admin B, en paralelo, abre el mismo approval y agrega un comentario
    ///     al Metadata.
    ///   - Sin xmin, el ultimo SaveChanges pisa silenciosamente al primero —
    ///     incidente fiscal: cambios de un admin se pierden y el AuditLog no
    ///     refleja la divergencia.
    ///   - Con xmin, el segundo SaveChanges tira <see cref="DbUpdateConcurrencyException"/>
    ///     y el frontend obliga a recargar la bandeja antes de re-intentar.
    ///
    /// Por que se valida ahora (M0) y no junto con el resto FC1.3:
    /// la edicion admin del Metadata es la unica via para mutar un approval
    /// pending. Subir el flag <c>EnablePartialCreditNotes</c> sin xmin
    /// expondria el race instantaneamente. M0 entra como hotfix separado.
    /// </summary>
    [Fact]
    public async Task ApprovalRequest_TwoConcurrentMetadataUpdates_SecondShouldThrowDbUpdateConcurrencyException()
    {
        // ARRANGE: persistir un ApprovalRequest pending. No hay FKs hacia
        // Customer/Supplier/Reserva, asi que no usamos SeedBaseAsync — la
        // entidad es self-contained (EntityType+EntityId son strings/int sin FK).
        int approvalId;
        await using (var setup = _fixture.CreateDbContext())
        {
            var approval = new ApprovalRequest
            {
                // Tipo existente Fase 1.2 (FC1.3 todavia no agrega PartialCreditNoteApproval=11,
                // eso entra en FC1.3.0). Para el test alcanza con cualquier tipo —
                // xmin protege la fila, no el RequestType.
                RequestType = ApprovalRequestType.InvoiceAnnulment,
                RequestedByUserId = "user-vendedor-A",
                RequestedByUserName = "Vendedor A",
                RequestedAt = DateTime.UtcNow,
                EntityType = "Invoice",
                EntityId = 12345,
                Reason = "Test FC1.3.0a — concurrency token xmin.",
                Status = ApprovalStatus.Pending,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                Metadata = """{"draft":"liquidation original"}""",
            };
            setup.ApprovalRequests.Add(approval);
            await setup.SaveChangesAsync();
            approvalId = approval.Id;
        }

        // ACT: dos admins cargan el MISMO approval, modifican el Metadata
        // (campo donde FC1.3 va a guardar el JSON de la liquidacion + edits[])
        // y persisten.
        await using var ctxA = _fixture.CreateDbContext();
        await using var ctxB = _fixture.CreateDbContext();

        var approvalInA = await ctxA.ApprovalRequests.FirstAsync(a => a.Id == approvalId);
        var approvalInB = await ctxB.ApprovalRequests.FirstAsync(a => a.Id == approvalId);

        // Admin A persiste primero -> xmin de la fila avanza en la BD.
        approvalInA.Metadata = """{"draft":"liquidation editada por admin A"}""";
        await ctxA.SaveChangesAsync();

        // Admin B trabajaba con el xmin viejo. Su SaveChanges va a comparar el
        // xmin que cargo contra el actual de la fila y van a diferir.
        approvalInB.Metadata = """{"draft":"liquidation editada por admin B"}""";

        // ASSERT: el segundo SaveChanges DEBE disparar el conflicto optimista.
        // Si no se lanza, significa que UseXminAsConcurrencyToken no aplico a
        // ApprovalRequests -> race silencioso en edicion admin habilitado.
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
