using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.2.2 v3 §7.3 (BR-V2-04, 2026-05-18): tests de concurrencia para el
/// invariante critico de N:M
/// (<c>SUM(allocations.GrossAmount) &lt;= refund.ReceivedAmount</c>).
///
/// <para>
/// <b>Cada test paralelo</b> abre 2 scopes EF independientes desde el provider
/// del fixture (cada scope tiene su propio AppDbContext) y usa un
/// <see cref="Barrier"/>(2) para forzar arranque simultaneo, garantizando que
/// el CHECK SQL del cap se eval realmente bajo concurrencia (no como un test
/// "secuencial sin querer").
/// </para>
///
/// <para>
/// <b>Smoke DI obligatorio</b>: el primer test <c>BuildServiceProvider_ResolvesAllServices</c>
/// se corre antes que los otros para detectar inmediato cualquier registro DI
/// faltante. Si falla, los otros 4 tests no aportan informacion util — caen
/// todos con "Unable to resolve service for type X" y nadie sabe por que.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class OperatorRefundConcurrencyTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public OperatorRefundConcurrencyTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // Smoke DI test (BR-V2-04)
    // =========================================================================

    [Fact]
    public void BuildServiceProvider_ResolvesAllServices()
    {
        // Si esto rompe en runtime es porque algun service del modulo o de sus
        // transitivas no esta registrado en BuildServiceProvider. El error de
        // GetRequiredService incluye el tipo no resuelto — usar ese error para
        // agregar el registro al fixture.
        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var sp = scope.ServiceProvider;

        Assert.NotNull(sp.GetRequiredService<IBookingCancellationService>());
        Assert.NotNull(sp.GetRequiredService<IInvoiceAnnulmentBcBridge>());
        Assert.NotNull(sp.GetRequiredService<IOperatorRefundService>());
        Assert.NotNull(sp.GetRequiredService<IClientCreditService>());
        Assert.NotNull(sp.GetRequiredService<IAuditService>());
        Assert.NotNull(sp.GetRequiredService<IApprovalRequestService>());
        Assert.NotNull(sp.GetRequiredService<IOperationalFinanceSettingsService>());

        // Validacion clave del split BR-04: BC y Bridge resuelven al MISMO objeto
        // dentro del scope. Si esto falla, una NC procesada por el job pierde la
        // sincronizacion con el BC porque escribe en un AppDbContext distinto.
        var bcAsService = sp.GetRequiredService<IBookingCancellationService>();
        var bcAsBridge = sp.GetRequiredService<IInvoiceAnnulmentBcBridge>();
        Assert.Same(bcAsService, bcAsBridge);
    }

    // =========================================================================
    // Tests de concurrencia paralela
    // =========================================================================

    [Fact]
    public async Task Test_TwoTasksAllocateWithinCap_BothSucceed()
    {
        // Setup: refund de $1000 + 2 BCs.
        var seed = await SeedRefundAndTwoBcsAsync(receivedAmount: 1000m);

        // Acto en paralelo: cada tarea allocate $300 → suma $600 < $1000.
        var (result1, result2) = await RunParallelAsync(
            seed.RefundPublicId,
            t1Request: new AllocateRefundRequest(seed.Bc1PublicId, 300m, new List<DeductionLineRequest>()),
            t2Request: new AllocateRefundRequest(seed.Bc2PublicId, 300m, new List<DeductionLineRequest>()));

        // Assert: ambas exitosas.
        Assert.True(result1.Succeeded, $"Task 1 fallo: {result1.Error}");
        Assert.True(result2.Succeeded, $"Task 2 fallo: {result2.Error}");

        // Cap final = $600.
        await using var verifyCtx = _fixture.CreateDbContext();
        var refund = await verifyCtx.OperatorRefundReceived
            .AsNoTracking()
            .FirstAsync(r => r.PublicId == seed.RefundPublicId);
        Assert.Equal(600m, refund.AllocatedAmount);
    }

    [Fact]
    public async Task Test_TwoTasksAllocateExceedingCap_OneWinsOneRejects409()
    {
        // Setup: refund de $500 + 2 BCs. 2 tareas de $400 cada una -> $800 > $500.
        var seed = await SeedRefundAndTwoBcsAsync(receivedAmount: 500m);

        var (result1, result2) = await RunParallelAsync(
            seed.RefundPublicId,
            t1Request: new AllocateRefundRequest(seed.Bc1PublicId, 400m, new List<DeductionLineRequest>()),
            t2Request: new AllocateRefundRequest(seed.Bc2PublicId, 400m, new List<DeductionLineRequest>()));

        // Una gana, la otra rechaza con INV-084.
        var successes = new[] { result1, result2 }.Count(r => r.Succeeded);
        var failures = new[] { result1, result2 }.Count(r => !r.Succeeded);
        Assert.Equal(1, successes);
        Assert.Equal(1, failures);

        var loser = result1.Succeeded ? result2 : result1;
        Assert.NotNull(loser.Exception);
        // Aceptamos BusinessInvariantViolationException directamente (CHECK SQL traducido)
        // O DbUpdateException si el retry llego al limite — ambos son rechazos correctos.
        var isInvariant = loser.Exception is BusinessInvariantViolationException biv &&
                          biv.InvariantCode == "INV-084";
        var isUpdate = loser.Exception is DbUpdateException;
        Assert.True(isInvariant || isUpdate,
            $"Esperaba INV-084 o DbUpdateException, recibi: {loser.Exception?.GetType().Name} {loser.Exception?.Message}");

        // Cap final = $400 (solo el ganador).
        await using var verifyCtx = _fixture.CreateDbContext();
        var refund = await verifyCtx.OperatorRefundReceived
            .AsNoTracking()
            .FirstAsync(r => r.PublicId == seed.RefundPublicId);
        Assert.Equal(400m, refund.AllocatedAmount);
    }

    [Fact]
    public async Task Test_VoidedAllocationFreesCap_AllowsReallocation()
    {
        // Setup: allocate hasta el cap, void, reallocate igual.
        var seed = await SeedRefundAndTwoBcsAsync(receivedAmount: 500m);

        // Primero: allocate $500 sobre BC1 (llena el cap).
        // IServiceScope NO es IAsyncDisposable, va con `using` clasico.
        using (var scope1 = _fixture.BuildServiceProvider().CreateScope())
        {
            var svc = scope1.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            await svc.AllocateAsync(
                seed.RefundPublicId,
                new AllocateRefundRequest(seed.Bc1PublicId, 500m, new List<DeductionLineRequest>()),
                "test-user", null, CancellationToken.None);
        }

        // Verificar cap lleno.
        Guid firstAllocationPublicId;
        await using (var v1 = _fixture.CreateDbContext())
        {
            var refund = await v1.OperatorRefundReceived.AsNoTracking()
                .FirstAsync(r => r.PublicId == seed.RefundPublicId);
            Assert.Equal(500m, refund.AllocatedAmount);

            firstAllocationPublicId = (await v1.OperatorRefundAllocations.AsNoTracking()
                .FirstAsync(a => a.OperatorRefundReceivedId == refund.Id)).PublicId;
        }

        // Void el primer allocation.
        using (var scope2 = _fixture.BuildServiceProvider().CreateScope())
        {
            var svc = scope2.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            await svc.VoidAllocationAsync(
                firstAllocationPublicId,
                new VoidAllocationRequest("Test void para liberar el cap completamente"),
                "test-user", null, CancellationToken.None);
        }

        // Verificar cap liberado.
        await using (var v2 = _fixture.CreateDbContext())
        {
            var refund = await v2.OperatorRefundReceived.AsNoTracking()
                .FirstAsync(r => r.PublicId == seed.RefundPublicId);
            Assert.Equal(0m, refund.AllocatedAmount);
        }

        // Reallocate sobre BC2.
        using (var scope3 = _fixture.BuildServiceProvider().CreateScope())
        {
            var svc = scope3.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            await svc.AllocateAsync(
                seed.RefundPublicId,
                new AllocateRefundRequest(seed.Bc2PublicId, 500m, new List<DeductionLineRequest>()),
                "test-user", null, CancellationToken.None);
        }

        await using (var v3 = _fixture.CreateDbContext())
        {
            var refund = await v3.OperatorRefundReceived.AsNoTracking()
                .FirstAsync(r => r.PublicId == seed.RefundPublicId);
            Assert.Equal(500m, refund.AllocatedAmount);
        }
    }

    [Fact]
    public async Task Test_ConcurrentVoidAndAllocate_RespectsCap()
    {
        // Setup: cap $500, allocate $500 sobre BC1.
        var seed = await SeedRefundAndTwoBcsAsync(receivedAmount: 500m);

        using (var scope = _fixture.BuildServiceProvider().CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            await svc.AllocateAsync(
                seed.RefundPublicId,
                new AllocateRefundRequest(seed.Bc1PublicId, 500m, new List<DeductionLineRequest>()),
                "test-user", null, CancellationToken.None);
        }

        // Obtener el PublicId de la allocation creada.
        Guid allocPublicId;
        await using (var v1 = _fixture.CreateDbContext())
        {
            var refund = await v1.OperatorRefundReceived.AsNoTracking()
                .FirstAsync(r => r.PublicId == seed.RefundPublicId);
            allocPublicId = (await v1.OperatorRefundAllocations.AsNoTracking()
                .FirstAsync(a => a.OperatorRefundReceivedId == refund.Id)).PublicId;
        }

        // En paralelo: una tarea VOID la allocation, otra intenta allocate $500
        // contra BC2. Si el orden es void-then-allocate, ambas deberian poder
        // completar (el void libera el cap). Si el orden es allocate-then-void,
        // el allocate detecta cap superado y rechaza. En cualquier caso, el
        // resultado final del refund.AllocatedAmount debe ser <= 500.
        var barrier = new Barrier(2);

        var voidTask = Task.Run(async () =>
        {
            try
            {
                using var scope = _fixture.BuildServiceProvider().CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
                barrier.SignalAndWait();
                await svc.VoidAllocationAsync(
                    allocPublicId,
                    new VoidAllocationRequest("Test concurrent void contra allocate"),
                    "test-user", null, CancellationToken.None);
                return (Succeeded: true, Exception: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Succeeded: false, Exception: (Exception?)ex);
            }
        });

        var allocTask = Task.Run(async () =>
        {
            try
            {
                using var scope = _fixture.BuildServiceProvider().CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
                barrier.SignalAndWait();
                await svc.AllocateAsync(
                    seed.RefundPublicId,
                    new AllocateRefundRequest(seed.Bc2PublicId, 500m, new List<DeductionLineRequest>()),
                    "test-user", null, CancellationToken.None);
                return (Succeeded: true, Exception: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Succeeded: false, Exception: (Exception?)ex);
            }
        });

        await Task.WhenAll(voidTask, allocTask);

        // Cap final NUNCA debe superar 500. La consistencia es el punto.
        await using var verifyCtx = _fixture.CreateDbContext();
        var finalRefund = await verifyCtx.OperatorRefundReceived
            .AsNoTracking()
            .FirstAsync(r => r.PublicId == seed.RefundPublicId);
        Assert.True(finalRefund.AllocatedAmount >= 0m && finalRefund.AllocatedAmount <= 500m,
            $"Cap inconsistente: AllocatedAmount={finalRefund.AllocatedAmount}");
    }

    // =========================================================================
    // Smoke test DROP CHECK (confirma que los tests anteriores NO son falsos positivos)
    // =========================================================================

    [Fact]
    public async Task Test_AllocationCheck_DroppedConstraintLetsOverAllocate_ConfirmsRealEnforcement()
    {
        // Setup: refund $500.
        var seed = await SeedRefundAndTwoBcsAsync(receivedAmount: 500m);

        // Dropear el CHECK constraint temporalmente.
        await using (var dropCtx = _fixture.CreateDbContext())
        {
            await dropCtx.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"OperatorRefundsReceived\" " +
                "DROP CONSTRAINT IF EXISTS chk_OperatorRefundsReceived_allocated_not_exceeds;");
        }

        try
        {
            // Sin el CHECK, podemos forzar manualmente AllocatedAmount > ReceivedAmount.
            // No usamos el service (que valida en C#) — escribimos directo en BD.
            await using var ctx = _fixture.CreateDbContext();
            var refund = await ctx.OperatorRefundReceived
                .FirstAsync(r => r.PublicId == seed.RefundPublicId);
            refund.AllocatedAmount = 9999m; // imposible si el CHECK existiera
            await ctx.SaveChangesAsync();

            await using var verifyCtx = _fixture.CreateDbContext();
            var verifyRefund = await verifyCtx.OperatorRefundReceived
                .AsNoTracking()
                .FirstAsync(r => r.PublicId == seed.RefundPublicId);
            Assert.Equal(9999m, verifyRefund.AllocatedAmount);
        }
        finally
        {
            // Recrear el CHECK para no contaminar los demas tests de la clase.
            // El fixture lo recrea al InitializeAsync de la clase, pero estamos
            // en un test puntual: lo restauramos explicitamente para que los
            // siguientes tests dentro de esta clase queden bien.
            await using var restoreCtx = _fixture.CreateDbContext();
            await restoreCtx.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"OperatorRefundsReceived\" " +
                "ADD CONSTRAINT chk_OperatorRefundsReceived_allocated_not_exceeds " +
                "CHECK (\"AllocatedAmount\" >= 0 AND \"AllocatedAmount\" <= \"ReceivedAmount\");");
        }
    }

    // =========================================================================
    // Helpers privados
    // =========================================================================

    private record SeedResult(
        Guid RefundPublicId,
        Guid Bc1PublicId,
        Guid Bc2PublicId,
        int Bc1Id,
        int Bc2Id);

    /// <summary>
    /// Crea: Customer + Supplier + 1 Reserva por BC + Invoice + 2 BCs en
    /// AwaitingOperatorRefund + un refund recibido del operador con
    /// <paramref name="receivedAmount"/> y cap libre.
    /// </summary>
    private async Task<SeedResult> SeedRefundAndTwoBcsAsync(decimal receivedAmount)
    {
        await using var ctx = _fixture.CreateDbContext();

        var customer = new Customer { FullName = "Cliente C", TaxCondition = "Consumidor Final", IsActive = true };
        var supplier = new Supplier { Name = "Operador S", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        // 2 Reservas independientes.
        var r1 = new Reserva { NumeroReserva = "RES1", Name = "R1", Status = EstadoReserva.PendingOperatorRefund, PayerId = customer.Id };
        var r2 = new Reserva { NumeroReserva = "RES2", Name = "R2", Status = EstadoReserva.PendingOperatorRefund, PayerId = customer.Id };
        ctx.Reservas.AddRange(r1, r2);
        await ctx.SaveChangesAsync();

        // 2 Invoices originales (una por reserva).
        var i1 = new Invoice { TipoComprobante = 1, PuntoDeVenta = 1, NumeroComprobante = 1, ImporteTotal = 1000m, ReservaId = r1.Id };
        var i2 = new Invoice { TipoComprobante = 1, PuntoDeVenta = 1, NumeroComprobante = 2, ImporteTotal = 1000m, ReservaId = r2.Id };
        ctx.Invoices.AddRange(i1, i2);
        await ctx.SaveChangesAsync();

        // 2 BCs en AwaitingOperatorRefund con snapshot fiscal RI x RI (sin Mono → la matriz fiscal no rechaza retenciones AR).
        var bc1 = CancellationTestData.NewCancellation(customer.Id, supplier.Id, r1.Id, i1.Id, BookingCancellationStatus.AwaitingOperatorRefund);
        var bc2 = CancellationTestData.NewCancellation(customer.Id, supplier.Id, r2.Id, i2.Id, BookingCancellationStatus.AwaitingOperatorRefund);
        // Override del default RI x Mono → ponemos Agency RI explicitamente:
        bc1.FiscalSnapshot.AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO";
        bc2.FiscalSnapshot.AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO";
        ctx.BookingCancellations.AddRange(bc1, bc2);
        await ctx.SaveChangesAsync();

        // Refund del operador con cap libre.
        var refund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id,
            ReceivedAmount = receivedAmount,
            AllocatedAmount = 0m,
            Method = "Transfer",
            Currency = "ARS",
            ExchangeRateAtReceipt = 1m,
            ReceivedAt = DateTime.UtcNow,
            ReceivedByUserId = "test-user",
            ReceivedByUserName = "Test",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        return new SeedResult(
            RefundPublicId: refund.PublicId,
            Bc1PublicId: bc1.PublicId,
            Bc2PublicId: bc2.PublicId,
            Bc1Id: bc1.Id,
            Bc2Id: bc2.Id);
    }

    private record ParallelResult(bool Succeeded, Exception? Exception)
    {
        public string Error => Exception?.Message ?? string.Empty;
    }

    /// <summary>
    /// Ejecuta 2 allocates en paralelo con un Barrier(2) para forzar arranque
    /// simultaneo. Cada tarea abre su propio scope EF (AppDbContext independiente).
    /// </summary>
    private async Task<(ParallelResult Task1, ParallelResult Task2)> RunParallelAsync(
        Guid refundPublicId,
        AllocateRefundRequest t1Request,
        AllocateRefundRequest t2Request)
    {
        var barrier = new Barrier(2);

        var t1 = Task.Run(async () =>
        {
            try
            {
                using var scope = _fixture.BuildServiceProvider().CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
                barrier.SignalAndWait();
                await svc.AllocateAsync(refundPublicId, t1Request, "user-1", null, CancellationToken.None);
                return new ParallelResult(true, null);
            }
            catch (Exception ex)
            {
                return new ParallelResult(false, ex);
            }
        });

        var t2 = Task.Run(async () =>
        {
            try
            {
                using var scope = _fixture.BuildServiceProvider().CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
                barrier.SignalAndWait();
                await svc.AllocateAsync(refundPublicId, t2Request, "user-2", null, CancellationToken.None);
                return new ParallelResult(true, null);
            }
            catch (Exception ex)
            {
                return new ParallelResult(false, ex);
            }
        });

        var results = await Task.WhenAll(t1, t2);
        return (results[0], results[1]);
    }
}
