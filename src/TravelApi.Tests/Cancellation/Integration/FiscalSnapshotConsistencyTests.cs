using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1 (ADR-002 §2.7 / review BR2, 2026-05-14): valida el CHECK
/// <c>chk_BookingCancellations_fiscalsnapshot_consistent</c> (INV-118).
///
/// Regla de negocio:
///  - En <see cref="BookingCancellationStatus.Drafted"/> y
///    <see cref="BookingCancellationStatus.Aborted"/> el snapshot puede estar
///    incompleto (el cashier todavia esta editando o no se llego a confirmar).
///  - Para cualquier estado &gt;= <see cref="BookingCancellationStatus.AwaitingFiscalConfirmation"/>
///    (T0+), el snapshot DEBE tener:
///      * <c>Source != Unset</c> (fuente de TC declarada).
///      * <c>ExchangeRateAtOriginalInvoice &gt; 0</c> (TC fiscal de la NC).
///      * <c>CurrencyAtEvent != NULL</c>.
///  - Sin esto, AFIP rechazaria la NC (incoherencia con la factura original) y
///    la reconstruccion contable de la diferencia de cambio T0->T2 seria imposible.
///
/// El test cubre los 4 escenarios criticos del CHECK:
///   1. Drafted + snapshot incompleto -> permitido.
///   2. AwaitingFiscalConfirmation + snapshot incompleto -> rechazado (INV-118).
///   3. AwaitingFiscalConfirmation + snapshot completo -> permitido (no es false positive).
///   4. Aborted + snapshot incompleto -> permitido (cierra el OR del CHECK).
/// </summary>
[Trait("Category", "Integration")]
public sealed class FiscalSnapshotConsistencyTests : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public FiscalSnapshotConsistencyTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BookingCancellation_StatusBeyondDrafted_RequiresCompleteFiscalSnapshot_INV118()
    {
        // ============================================================
        // Caso 1: Drafted + snapshot incompleto -> permitido.
        // ============================================================
        await using (var ctx = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

            var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId,
                status: BookingCancellationStatus.Drafted);
            // Override del snapshot para que sea incompleto (default vacio).
            bc.FiscalSnapshot = CancellationTestData.IncompleteSnapshot();

            ctx.BookingCancellations.Add(bc);

            // No debe lanzar: en Drafted el snapshot incompleto es legal.
            await ctx.SaveChangesAsync();
        }

        await _fixture.ResetDatabaseAsync();

        // ============================================================
        // Caso 2: AwaitingFiscalConfirmation + snapshot incompleto -> debe rechazarse.
        // ============================================================
        await using (var ctx = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

            var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId,
                status: BookingCancellationStatus.AwaitingFiscalConfirmation);
            bc.FiscalSnapshot = CancellationTestData.IncompleteSnapshot();

            ctx.BookingCancellations.Add(bc);

            // El CHECK debe disparar -> interceptor lo traduce a INV-118.
            var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
                () => ctx.SaveChangesAsync());

            // El codigo es el contrato con el frontend: si cambia,
            // CheckConstraintMessages tambien debe actualizarse en sincro.
            Assert.Equal("INV-118", ex.InvariantCode);
        }

        await _fixture.ResetDatabaseAsync();

        // ============================================================
        // Caso 3: AwaitingFiscalConfirmation + snapshot COMPLETO -> permitido.
        // Valida que el CHECK no es excesivamente estricto (false positive).
        // ============================================================
        await using (var ctx = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

            var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId,
                status: BookingCancellationStatus.AwaitingFiscalConfirmation);
            // El helper NewCancellation entrega snapshot completo por default.

            ctx.BookingCancellations.Add(bc);

            // Debe persistirse sin error.
            await ctx.SaveChangesAsync();
        }

        await _fixture.ResetDatabaseAsync();

        // ============================================================
        // Caso 4: Aborted + snapshot incompleto -> permitido.
        //
        // El CHECK SQL permite snapshot incompleto SOLO para Drafted (0) y
        // Aborted (6) — los dos estados donde la BC todavia no produjo efectos
        // fiscales con AFIP. Cubrimos los dos casos juntos para que el test se
        // rompa si alguien deja la regla en "Status = 0" sin el "OR Status = 6"
        // (un BC abortado-desde-borrador no tendria por que llenar TC fiscal).
        // ============================================================
        await using (var ctx = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

            var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId,
                status: BookingCancellationStatus.Aborted);
            // Override del snapshot para que sea incompleto: en Aborted es legal
            // porque jamas se llego a confirmar nada con el cliente / AFIP.
            bc.FiscalSnapshot = CancellationTestData.IncompleteSnapshot();

            ctx.BookingCancellations.Add(bc);

            // No debe lanzar: el CHECK acepta Status IN (0, 6) sin snapshot completo.
            await ctx.SaveChangesAsync();
        }
    }
}
