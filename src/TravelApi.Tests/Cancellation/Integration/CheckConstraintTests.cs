using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1 (ADR-002 §2.3.3, 2026-05-14): valida que las 5 CHECK constraints SQL del
/// modulo de cancelacion efectivamente rechazan datos invalidos, y que el
/// <see cref="Infrastructure.Persistence.BusinessInvariantInterceptor"/> traduce
/// la <c>SqlState='23514'</c> a <see cref="BusinessInvariantViolationException"/>
/// con el codigo de invariante (INV-XXX) correcto.
///
/// Por que importa traducir la excepcion:
///  - El frontend espera HTTP 409 con un mensaje en espanol pensado para el
///    usuario final, no una <c>DbUpdateException</c> opaca que el handler
///    global mapearia a 500.
///  - Cada test verifica que el <c>InvariantCode</c> coincida con el documentado
///    en el catalogo Bucket G — si alguien renombra la CHECK constraint sin
///    actualizar <c>CheckConstraintMessages</c>, este test lo detecta.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CheckConstraintTests : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public CheckConstraintTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task OperatorRefundReceived_AllocatedAmountExceedsReceived_ThrowsBusinessInvariantViolation_INV084()
    {
        // ARRANGE: refund con AllocatedAmount > ReceivedAmount viola
        // chk_OperatorRefundsReceived_allocated_not_exceeds (INV-084).
        await using var ctx = _fixture.CreateDbContext();
        var (_, supId, _, _) = await CancellationTestData.SeedBaseAsync(ctx);

        var refund = new OperatorRefundReceived
        {
            SupplierId = supId,
            ReceivedAt = DateTime.UtcNow,
            ReceivedAmount = 100m,
            AllocatedAmount = 200m, // <-- viola la regla
            Method = "Transfer",
            Currency = "ARS",
            ExchangeRateAtReceipt = 1m,
            ReceivedByUserId = "tester",
            ReceivedByUserName = "Tester",
        };
        ctx.OperatorRefundReceived.Add(refund);

        // ACT + ASSERT.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => ctx.SaveChangesAsync());

        // El codigo INV-084 es el contrato publico que el frontend usa para
        // distinguir "saldo asignado excede recibido" de otras violaciones.
        Assert.Equal("INV-084", ex.InvariantCode);
    }

    [Fact]
    public async Task ClientCreditEntry_RemainingBalanceNegative_ThrowsBusinessInvariantViolation_INV085()
    {
        // ARRANGE: cadena completa BC -> Refund -> Allocation -> Entry, y al
        // ultimo le seteamos un RemainingBalance negativo para disparar
        // chk_ClientCreditEntries_remaining_non_negative (INV-085).
        await using var ctx = _fixture.CreateDbContext();
        var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId);
        ctx.BookingCancellations.Add(bc);

        var refund = new OperatorRefundReceived
        {
            SupplierId = supId,
            ReceivedAt = DateTime.UtcNow,
            ReceivedAmount = 500m,
            AllocatedAmount = 500m,
            Method = "Transfer",
            Currency = "ARS",
            ExchangeRateAtReceipt = 1m,
            ReceivedByUserId = "tester",
            ReceivedByUserName = "Tester",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

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
        ctx.OperatorRefundAllocations.Add(alloc);
        await ctx.SaveChangesAsync();

        // RemainingBalance = -100 viola CHECK (>= 0).
        var entry = new ClientCreditEntry
        {
            CustomerId = custId,
            OperatorRefundAllocationId = alloc.Id,
            BookingCancellationId = bc.Id,
            CreditedAmount = 500m,
            RemainingBalance = -100m, // <-- viola la regla
        };
        ctx.ClientCreditEntries.Add(entry);

        // ACT + ASSERT.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => ctx.SaveChangesAsync());
        Assert.Equal("INV-085", ex.InvariantCode);
    }

    [Fact]
    public async Task OperatorRefundAllocation_NetGreaterThanGross_ThrowsBusinessInvariantViolation_INV112()
    {
        // ARRANGE: GrossAmount < NetAmount viola
        // chk_OperatorRefundAllocations_net_positive (Gross >= Net). El service
        // de produccion calcula Net = Gross - SUM(Deductions); pasar Net > Gross
        // significaria "deducciones negativas" que no tienen sentido.
        await using var ctx = _fixture.CreateDbContext();
        var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId);
        ctx.BookingCancellations.Add(bc);

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
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        var alloc = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id,
            BookingCancellationId = bc.Id,
            GrossAmount = 100m,
            NetAmount = 200m, // <-- viola Gross >= Net
            IsVoided = false,
            CreatedByUserId = "tester",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.OperatorRefundAllocations.Add(alloc);

        // ACT + ASSERT.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => ctx.SaveChangesAsync());
        Assert.Equal("INV-112", ex.InvariantCode);
    }

    // Cubrimos tanto Amount=0 como Amount negativo en un solo [Theory]: si manana
    // alguien afloja el CHECK de "Amount > 0" a "Amount >= 0" por error, el caso
    // InlineData(0) deberia romperse y el bug salta a la vista. Sin el caso 0
    // explicito, ese cambio quedaria silencioso (un negativo seguiria rechazandose
    // pero "deducciones cero" entrarian y desbalancearian la cuenta).
    //
    // Nota: xUnit no acepta literales <c>decimal</c> en [InlineData] (limitacion
    // de atributos C#); por eso recibimos <c>double</c> y casteamos al body.
    [Theory]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    public async Task DeductionLine_AmountZeroOrNegative_ThrowsBusinessInvariantViolation_INV112(double invalidAmount)
    {
        // ARRANGE: cadena hasta tener una Allocation valida, y luego insertar
        // una DeductionLine con Amount invalido (0 o negativo) -> viola
        // chk_DeductionLines_amount_positive.
        await using var ctx = _fixture.CreateDbContext();
        var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId);
        ctx.BookingCancellations.Add(bc);

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
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

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
        ctx.OperatorRefundAllocations.Add(alloc);
        await ctx.SaveChangesAsync();

        var deduction = new DeductionLine
        {
            OperatorRefundAllocationId = alloc.Id,
            Kind = DeductionKind.AdministrativeFee,
            Amount = (decimal)invalidAmount, // <-- viola Amount > 0 (cero o negativo)
        };
        ctx.DeductionLines.Add(deduction);

        // ACT + ASSERT. Mismo codigo INV-112 que la regla de Allocation porque
        // representan la misma invariante de negocio (montos coherentes).
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => ctx.SaveChangesAsync());
        Assert.Equal("INV-112", ex.InvariantCode);
    }

    [Fact]
    public async Task Reserva_InvalidStatusValue_ThrowsBusinessInvariantViolation_INV100()
    {
        // ARRANGE: crear minima reserva y luego setearle un Status que NO esta
        // en la lista del CHECK (Budget / Confirmed / Traveling / Closed /
        // Cancelled / PendingOperatorRefund / Archived).
        await using var ctx = _fixture.CreateDbContext();
        var (custId, _, _, _) = await CancellationTestData.SeedBaseAsync(ctx);

        var reserva = await ctx.Reservas.FirstAsync();

        // Bypass del helper: asignacion directa al string. EstadoReserva NO tiene
        // este valor; el CHECK debe rechazarlo independientemente de que la API
        // permita o no setear "InvalidStatus" desde controllers.
        reserva.Status = "InvalidStatusValue";

        // ACT + ASSERT.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => ctx.SaveChangesAsync());
        Assert.Equal("INV-100", ex.InvariantCode);
    }
}
