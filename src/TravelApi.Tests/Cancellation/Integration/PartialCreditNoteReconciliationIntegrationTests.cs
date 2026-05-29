using Microsoft.EntityFrameworkCore;
using Npgsql;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.3 Fase 3 (ADR-010, 2026-05-29): tests de INTEGRACION (Postgres real via
/// TestContainers) para la bandeja de reconciliacion de NC parciales con recibos
/// vivos (<see cref="PartialCreditNoteReconciliation"/>).
///
/// <para><b>Por que integracion y no InMemory</b>: la Fase 3 tiene 14 unit tests
/// con InMemory, pero InMemory NO ejercita ni el concurrency token <c>xmin</c> ni
/// los CHECK constraints SQL crudos. Estos dos puntos solo se pueden validar
/// contra un Postgres de verdad. Esto cubre la deuda de cobertura que marco la
/// review del backend (no hay bug conocido, es cobertura faltante).</para>
///
/// <para><b>Que cubre</b>:</para>
/// <list type="bullet">
///   <item>xmin: dos encargados editando el MISMO caso a la vez -> el segundo
///   recibe <see cref="DbUpdateConcurrencyException"/> (R2 del ADR-010).</item>
///   <item><c>chk_pcnr_status</c>: Status fuera de ('Pending','Resolved') rebota.</item>
///   <item><c>chk_pcnr_resolved_consistency</c>: un caso 'Resolved' sin ResolvedAt
///   o sin ResolvedByUserId rebota.</item>
/// </list>
///
/// <para><b>HALLAZGO IMPORTANTE (ver comentarios en los tests de CHECK)</b>: los
/// constraints <c>chk_pcnr_status</c> y <c>chk_pcnr_resolved_consistency</c> NO
/// estan mapeados en <c>CheckConstraintMessages</c>. El interceptor igual atrapa
/// el SqlState 23514 y lanza <see cref="BusinessInvariantViolationException"/>,
/// pero con el fallback generico (<c>InvariantCode = "INV-UNKNOWN"</c>). NO hay un
/// INV-XXX dedicado para esta bandeja. Si se quiere un 409 con mensaje amigable
/// especifico, hay que registrar las dos constraints en CheckConstraintMessages.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PartialCreditNoteReconciliationIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public PartialCreditNoteReconciliationIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =====================================================================
    // Helper de seed: deja persistido un caso Pending con las dos facturas
    // que las FKs exigen (NC parcial + factura original) y devuelve su Id.
    // =====================================================================

    /// <summary>
    /// Seedea un <see cref="PartialCreditNoteReconciliation"/> en estado Pending
    /// listo para usar. Crea Customer/Supplier/Reserva/Factura-original con el
    /// helper base, suma una segunda Invoice (la NC parcial) y arma el caso.
    /// Receipts queda vacio: ni el test de xmin ni los de CHECK necesitan hijas.
    /// </summary>
    private async Task<int> SeedPendingReconciliationAsync()
    {
        await using var ctx = _fixture.CreateDbContext();

        // Cliente + proveedor + reserva + factura ORIGINAL.
        var (_, _, reservaId, originalInvoiceId) = await CancellationTestData.SeedBaseAsync(ctx);

        // Segunda factura que hace de NOTA DE CREDITO parcial (Id distinto del original).
        var creditNoteInvoiceId = await CancellationTestData.SeedCreditNoteInvoiceAsync(ctx, reservaId);

        var caso = new PartialCreditNoteReconciliation
        {
            CreditNoteInvoiceId = creditNoteInvoiceId,
            OriginalInvoiceId = originalInvoiceId,
            ReservaId = reservaId,
            FiscalAmountCredited = 750m,
            Currency = "ARS",
            Status = PartialCreditNoteReconciliationStatus.Pending,
            OpenedAt = DateTime.UtcNow,
            OpenedByUserId = "system",
            OpenedByUserName = "Sistema",
        };
        ctx.PartialCreditNoteReconciliations.Add(caso);
        await ctx.SaveChangesAsync();
        return caso.Id;
    }

    // =====================================================================
    // 1. xmin (concurrencia)
    // =====================================================================

    /// <summary>
    /// Verifica que el concurrency token <c>xmin</c> protege el caso de la bandeja:
    /// si dos encargados cargan el mismo caso y lo editan en paralelo, el segundo
    /// SaveChanges debe rebotar con <see cref="DbUpdateConcurrencyException"/>.
    ///
    /// <para><b>Por que importa (ejemplo de mostrador)</b>: dos encargados abren el
    /// mismo caso de la bandeja a la vez. Uno escribe la nota de resolucion y guarda.
    /// El otro, que cargo la pantalla un segundo antes, guarda lo suyo. Sin xmin, el
    /// segundo pisa silenciosamente lo que escribio el primero y nadie se entera —
    /// se pierde la justificacion del cierre (R2 del ADR-010). Con xmin, el segundo
    /// recibe un conflicto y el frontend lo obliga a recargar antes de reintentar.</para>
    ///
    /// <para>Mutamos <c>ResolutionNotes</c> a proposito: es un campo LIBRE, no sujeto
    /// a ningun CHECK. Asi el test aisla la proteccion de xmin sin cruzarse con el
    /// CHECK de consistencia (que exige ResolvedAt+ResolvedByUserId si Status=Resolved).</para>
    /// </summary>
    [Fact]
    public async Task PartialCreditNoteReconciliation_TwoConcurrentUpdates_SecondShouldThrowDbUpdateConcurrencyException()
    {
        // ARRANGE: persistir un caso Pending.
        var casoId = await SeedPendingReconciliationAsync();

        // Dos contexts independientes simulan dos sesiones distintas. EF Core 8 no
        // permite que una misma entidad este tracked en dos contexts a la vez, por
        // eso pedimos dos AppDbContext separados a la fixture.
        await using var ctxA = _fixture.CreateDbContext();
        await using var ctxB = _fixture.CreateDbContext();

        var casoEnA = await ctxA.PartialCreditNoteReconciliations.FirstAsync(c => c.Id == casoId);
        var casoEnB = await ctxB.PartialCreditNoteReconciliations.FirstAsync(c => c.Id == casoId);

        // ACT: el encargado A guarda primero -> el xmin de la fila avanza en la BD.
        casoEnA.ResolutionNotes = "Nota escrita por el encargado A.";
        await ctxA.SaveChangesAsync();

        // El encargado B trabajaba con el xmin viejo (el que cargo antes del save de A).
        casoEnB.ResolutionNotes = "Nota escrita por el encargado B.";

        // ASSERT: el segundo SaveChanges debe disparar el conflicto optimista. Si NO
        // se lanza, significa que UseXminAsConcurrencyToken no aplico a esta tabla ->
        // edicion concurrente silenciosa habilitada en la bandeja.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctxB.SaveChangesAsync());

        // ASSERT extra: un test de concurrencia no termina en "hubo conflicto". Tiene que
        // probar el DESENLACE correcto: gano el PRIMER escritor (A). Si solo verificamos
        // que B exploto, no descartamos un bug donde igual termina pisado el valor de A
        // (ej. un retry mal hecho que reaplica a B). Releemos en un context NUEVO (sin
        // cache de tracking) y confirmamos que en la BD quedo lo que escribio A.
        await using var verify = _fixture.CreateDbContext();
        var persisted = await verify.PartialCreditNoteReconciliations.FirstAsync(c => c.Id == casoId);
        Assert.Equal("Nota escrita por el encargado A.", persisted.ResolutionNotes);
    }

    // =====================================================================
    // 2. CHECK constraints SQL crudo
    // =====================================================================

    /// <summary>
    /// Verifica que <c>chk_pcnr_status</c> rechaza cualquier Status fuera de la
    /// whitelist ('Pending','Resolved'). Protege contra un bug que escriba un estado
    /// invalido en la columna (ej. una migracion futura o un INSERT manual).
    ///
    /// <para><b>Por que via SQL crudo y no via EF</b>: la propiedad <c>Status</c> es
    /// un enum con <c>HasConversion&lt;string&gt;()</c>. El enum solo tiene Pending y
    /// Resolved, asi que EF JAMAS puede producir el string "Bogus" — no existe forma
    /// de forzarlo desde el modelo. La unica manera de meter un valor invalido en la
    /// columna (y ejercitar el CHECK) es un INSERT crudo. Como ese INSERT NO pasa por
    /// SaveChangesAsync, el <c>BusinessInvariantInterceptor</c> NO lo intercepta:
    /// el error sale como <see cref="PostgresException"/> con SqlState 23514 directo.</para>
    ///
    /// <para><b>HALLAZGO</b>: aunque pasara por el interceptor, <c>chk_pcnr_status</c>
    /// NO esta mapeado en <c>CheckConstraintMessages</c> -> caeria en el fallback
    /// "INV-UNKNOWN". No inventamos un InvariantCode que no existe; aca asertamos el
    /// error nativo de Postgres, que es lo que realmente ocurre.</para>
    /// </summary>
    [Fact]
    public async Task PartialCreditNoteReconciliation_InvalidStatusValue_ShouldViolateCheckPcnrStatus()
    {
        // ARRANGE: necesitamos dos Invoices reales para satisfacer las FKs Restrict
        // (CreditNoteInvoiceId + OriginalInvoiceId) antes de insertar el caso crudo.
        int creditNoteInvoiceId;
        int originalInvoiceId;
        await using var ctx = _fixture.CreateDbContext();

        var (_, _, reservaId, origInvId) = await CancellationTestData.SeedBaseAsync(ctx);
        originalInvoiceId = origInvId;
        creditNoteInvoiceId = await CancellationTestData.SeedCreditNoteInvoiceAsync(ctx, reservaId);

        // ACT + ASSERT: INSERT crudo con Status='Bogus' (valor fuera de la whitelist).
        // Postgres rechaza por chk_pcnr_status con SqlState 23514 (check_violation).
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            ctx.Database.ExecuteSqlRawAsync("""
                INSERT INTO "PartialCreditNoteReconciliations"
                    ("PublicId", "CreditNoteInvoiceId", "OriginalInvoiceId",
                     "FiscalAmountCredited", "Currency", "Status",
                     "OpenedAt", "OpenedByUserId", "ClosedWithLiveReceipts", "FourEyesBypassApplied")
                VALUES
                    (gen_random_uuid(), {0}, {1},
                     750, 'ARS', 'Bogus',
                     now(), 'system', false, false);
                """,
                creditNoteInvoiceId, originalInvoiceId));

        // 23514 = check_violation. Confirmamos ademas que el CHECK que disparo fue el
        // de status, no otro, leyendo el nombre del constraint que reporta Postgres.
        Assert.Equal("23514", ex.SqlState);
        Assert.Equal("chk_pcnr_status", ex.ConstraintName);
    }

    /// <summary>
    /// Verifica que <c>chk_pcnr_resolved_consistency</c> rechaza un caso marcado
    /// 'Resolved' que NO tiene la trazabilidad de cierre completa (le falta
    /// ResolvedAt y/o ResolvedByUserId). Regla del ADR-010: no se cierra un caso sin
    /// dejar el rastro de cuando y quien lo cerro.
    ///
    /// <para><b>Por que via EF (a diferencia del test de status)</b>: aca el enum SI
    /// puede representar el estado invalido — Resolved es un valor legitimo del enum.
    /// Lo invalido es la COMBINACION (Resolved + ResolvedAt null + ResolvedByUserId
    /// null), que el modelo permite construir. Como pasa por SaveChangesAsync, el
    /// <c>BusinessInvariantInterceptor</c> SI intercepta el SqlState 23514 y lo
    /// traduce a <see cref="BusinessInvariantViolationException"/>.</para>
    ///
    /// <para><b>HALLAZGO</b>: <c>chk_pcnr_resolved_consistency</c> NO esta mapeado en
    /// <c>CheckConstraintMessages</c>. El interceptor cae en el branch <c>_ =&gt;</c>
    /// (fallback) -> <c>InvariantCode = "INV-UNKNOWN"</c> y mensaje generico. NO
    /// inventamos un INV-XXX: asertamos exactamente lo que el codigo produce hoy
    /// (tipo BusinessInvariantViolationException + ConstraintName real + INV-UNKNOWN).
    /// Si se quiere un mensaje amigable propio, registrar la constraint en
    /// CheckConstraintMessages.</para>
    /// </summary>
    [Fact]
    public async Task PartialCreditNoteReconciliation_ResolvedWithoutTraceability_ShouldViolateCheckResolvedConsistency()
    {
        // ARRANGE: seedear un caso Pending valido.
        var casoId = await SeedPendingReconciliationAsync();

        await using var ctx = _fixture.CreateDbContext();
        var caso = await ctx.PartialCreditNoteReconciliations.FirstAsync(c => c.Id == casoId);

        // Marcamos Resolved pero NO seteamos ResolvedAt ni ResolvedByUserId.
        // Esta combinacion es justamente la que el CHECK debe rechazar.
        caso.Status = PartialCreditNoteReconciliationStatus.Resolved;
        caso.ResolvedAt = null;
        caso.ResolvedByUserId = null;

        // ACT + ASSERT: el SaveChanges pasa por el interceptor, que traduce el
        // SqlState 23514 a BusinessInvariantViolationException.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => ctx.SaveChangesAsync());

        // El ConstraintName SI viaja siempre (lo setea el interceptor desde Postgres),
        // este o no mapeado el mensaje. Confirmamos que disparo el CHECK correcto.
        Assert.Equal("chk_pcnr_resolved_consistency", ex.ConstraintName);

        // HALLAZGO documentado: como la constraint NO esta en CheckConstraintMessages,
        // el InvariantCode es el fallback generico. Si manana se mapea, este assert
        // hay que actualizarlo al INV-XXX que se le asigne.
        Assert.Equal("INV-UNKNOWN", ex.InvariantCode);
    }

    // =====================================================================
    // 3. CHECK constraint — CAMINO FELIZ (aceptacion)
    // =====================================================================

    /// <summary>
    /// Verifica el CAMINO FELIZ de <c>chk_pcnr_resolved_consistency</c>: un caso marcado
    /// 'Resolved' CON la trazabilidad completa (ResolvedAt + ResolvedByUserId seteados)
    /// debe persistir SIN excepcion. Es el cierre normal de un caso de la bandeja.
    ///
    /// <para><b>Por que importa</b>: los otros tests de la clase son todos de RECHAZO
    /// (status invalido, resolved sin rastro). Un CHECK demasiado estricto que tambien
    /// rebotara el cierre legitimo seria un bug que esos tests NO atrapan. Este test es
    /// la contraparte positiva: confirma que el constraint deja pasar el caso valido y
    /// no bloquea el flujo real de cierre del encargado (R2/R4 del ADR-010).</para>
    ///
    /// <para><b>Por que via EF y no SQL crudo</b>: queremos ejercitar el flujo NORMAL
    /// (el encargado cierra el caso desde la app, no con un INSERT a mano). Seteamos el
    /// enum <c>Resolved</c> (no el string crudo) para que pase por la conversion
    /// <c>HasConversion&lt;string&gt;()</c> tal cual lo hace produccion, y dejamos que
    /// SaveChangesAsync mande el UPDATE.</para>
    /// </summary>
    [Fact]
    public async Task PartialCreditNoteReconciliation_ResolvedWithFullTraceability_ShouldPersistOk()
    {
        // ARRANGE: seedear un caso Pending valido (mismo helper que los demas tests).
        var casoId = await SeedPendingReconciliationAsync();

        await using var ctx = _fixture.CreateDbContext();
        var caso = await ctx.PartialCreditNoteReconciliations.FirstAsync(c => c.Id == casoId);

        // ACT: cerramos el caso con la trazabilidad COMPLETA. A diferencia del test de
        // rechazo, aca SI seteamos ResolvedAt + ResolvedByUserId -> el CHECK debe dejar pasar.
        caso.Status = PartialCreditNoteReconciliationStatus.Resolved;
        caso.ResolvedAt = DateTime.UtcNow;
        caso.ResolvedByUserId = "encargado-cierra-bandeja";

        // ASSERT (parte 1): el SaveChanges NO debe tirar. Es el camino feliz: no usamos
        // Assert.ThrowsAsync. Si el CHECK fuera demasiado estricto, esto explotaria aca.
        await ctx.SaveChangesAsync();

        // ASSERT (parte 2): releemos en un context NUEVO para confirmar que el cierre
        // realmente quedo persistido en la BD (no solo en el cache de tracking).
        await using var verify = _fixture.CreateDbContext();
        var persisted = await verify.PartialCreditNoteReconciliations.FirstAsync(c => c.Id == casoId);
        Assert.Equal(PartialCreditNoteReconciliationStatus.Resolved, persisted.Status);
    }
}
