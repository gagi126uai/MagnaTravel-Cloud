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
///
/// <para><b>Obra "anular sin factura" (2026-07-23, migracion AnnulWithoutInvoice_RelaxFiscalSnapshotCheck)</b>:
/// el CHECK se relajo para que un BC SIN ancla fiscal (<c>OriginatingInvoiceId IS NULL</c>) quede exento
/// GLOBALMENTE (cualquier Status), no solo Drafted/Aborted — nunca emite NC, asi que nunca completa su
/// snapshot. Los casos 5-7 de <see cref="UnanchoredBc_AnyStatus_NeverRequiresFiscalSnapshot_ButAnchoredBcStill_INV118"/>
/// cubren exactamente eso, Y confirman (caso 7) que la relajacion NO abre un hueco para las filas CON factura:
/// esas siguen exigiendo el snapshot completo fuera de {Drafted, Aborted}, sin cambios.</para>
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

    /// <summary>
    /// Obra "anular sin factura" (2026-07-23), item 5(iii) del fix: prueba la relajacion del CHECK.
    ///
    /// <list type="number">
    ///   <item>BC SIN ancla (OriginatingInvoiceId null) en AwaitingOperatorRefund (2), snapshot incompleto ->
    ///     PERMITIDO (la exencion es global, no solo Drafted/Aborted).</item>
    ///   <item>BC SIN ancla en ClientCreditApplied (3), snapshot incompleto -> PERMITIDO (mismo motivo; cubre
    ///     el "camina 2 -&gt; 3 -&gt; 4" que describe la obra).</item>
    ///   <item>BC SIN ancla en Closed (4), snapshot incompleto -> PERMITIDO (cierra el circuito completo).</item>
    ///   <item>BC CON ancla (OriginatingInvoiceId no nulo) en AwaitingOperatorRefund (2), snapshot INCOMPLETO ->
    ///     SIGUE RECHAZADO (INV-118). La relajacion NO abre un hueco para las filas con factura: prueba que el
    ///     CHECK relajado distingue por ancla, no por status.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task UnanchoredBc_AnyStatus_NeverRequiresFiscalSnapshot_ButAnchoredBcStill_INV118()
    {
        // ============================================================
        // Caso 5: SIN ancla + AwaitingOperatorRefund + snapshot incompleto -> permitido.
        // ============================================================
        await using (var ctx = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, _) = await CancellationTestData.SeedBaseAsync(ctx);

            var bc = CancellationTestData.NewCancellation(custId, supId, resId, originatingInvoiceId: null,
                status: BookingCancellationStatus.AwaitingOperatorRefund);
            bc.FiscalSnapshot = CancellationTestData.IncompleteSnapshot();

            ctx.BookingCancellations.Add(bc);

            // No debe lanzar: sin ancla, la exencion aplica sin importar el Status.
            await ctx.SaveChangesAsync();
        }

        await _fixture.ResetDatabaseAsync();

        // ============================================================
        // Caso 6: SIN ancla + ClientCreditApplied + snapshot incompleto -> permitido.
        // ============================================================
        await using (var ctx = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, _) = await CancellationTestData.SeedBaseAsync(ctx);

            var bc = CancellationTestData.NewCancellation(custId, supId, resId, originatingInvoiceId: null,
                status: BookingCancellationStatus.ClientCreditApplied);
            bc.FiscalSnapshot = CancellationTestData.IncompleteSnapshot();

            ctx.BookingCancellations.Add(bc);
            await ctx.SaveChangesAsync();
        }

        await _fixture.ResetDatabaseAsync();

        // ============================================================
        // Caso 6-bis: SIN ancla + Closed + snapshot incompleto -> permitido (cierra 2 -> 3 -> 4).
        // ============================================================
        await using (var ctx = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, _) = await CancellationTestData.SeedBaseAsync(ctx);

            var bc = CancellationTestData.NewCancellation(custId, supId, resId, originatingInvoiceId: null,
                status: BookingCancellationStatus.Closed);
            bc.FiscalSnapshot = CancellationTestData.IncompleteSnapshot();

            ctx.BookingCancellations.Add(bc);
            await ctx.SaveChangesAsync();
        }

        await _fixture.ResetDatabaseAsync();

        // ============================================================
        // Caso 7: CON ancla (factura viva) + AwaitingOperatorRefund + snapshot INCOMPLETO -> SIGUE rechazado.
        // Prueba que la relajacion no abre un hueco para las filas que SI tienen factura.
        // ============================================================
        await using (var ctx = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

            var bc = CancellationTestData.NewCancellation(custId, supId, resId, originatingInvoiceId: invId,
                status: BookingCancellationStatus.AwaitingOperatorRefund);
            bc.FiscalSnapshot = CancellationTestData.IncompleteSnapshot();

            ctx.BookingCancellations.Add(bc);

            var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
                () => ctx.SaveChangesAsync());
            Assert.Equal("INV-118", ex.InvariantCode);
        }
    }
}
