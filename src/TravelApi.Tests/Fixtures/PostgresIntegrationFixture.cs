using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Testcontainers.PostgreSql;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Interfaces;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Fixtures;

/// <summary>
/// FC1 (2026-05-14): fixture compartida (via <see cref="IClassFixture{T}"/>) que
/// levanta un Postgres real en Docker para los tests de integracion del modulo
/// de cancelacion/refund.
///
/// Por que TestContainers en vez de InMemory:
///  - El modulo se apoya en CHECK constraints SQL (INV-084/085/100/112/118) y en
///    el concurrency token <c>xmin</c> de Postgres. InMemory ignora las dos cosas,
///    asi que los tests pasarian sin proteger las invariantes reales.
///  - <see cref="BusinessInvariantInterceptor"/> mapea <c>SqlState='23514'</c>
///    (check_violation) -> <see cref="Domain.Exceptions.BusinessInvariantViolationException"/>.
///    Validar esto necesita una BD que efectivamente lance ese error.
///
/// Cross-platform (decision Gaston 2026-05-14):
///  - TestContainers detecta el Docker Engine local (Docker Desktop en Windows /
///    socket nativo en Linux), por eso esta misma clase corre identica en
///    Windows dev y en el VPS Ubuntu. No hay paths hardcoded.
///  - Imagen <c>postgres:16</c> (igual al <c>docker-compose.yml</c> del repo).
///
/// Lifecycle:
///  - <see cref="InitializeAsync"/> arranca el container, espera healthcheck y
///    construye el schema usando <c>db.Database.EnsureCreatedAsync()</c> + un
///    batch SQL con los CHECK constraints SQL crudos del modulo FC1.
///    Por que NO <c>MigrateAsync()</c>: el historial de migraciones del repo
///    arranca creando una tabla <c>Reservas</c> que en produccion fue renombrada
///    manualmente a <c>TravelFiles</c> (documentado en
///    <c>reference_db_naming</c>). Migrar desde cero contra una BD limpia
///    deja la tabla como <c>Reservas</c> y las migraciones posteriores rompen.
///    <c>EnsureCreated</c> usa el modelo en memoria (que tiene
///    <c>ToTable("TravelFiles")</c> via fluent) -> schema correcto.
///    El trade-off: <c>EnsureCreated</c> no replica los CHECK SQL crudos de
///    la migracion FC1; los aplicamos a mano despues. Sin esto los tests no
///    podrian validar lo que vinieron a validar.
///  - <see cref="DisposeAsync"/> detiene + elimina el container — todos los datos
///    se descartan, no queda volumen residual.
///  - <see cref="ResetDatabaseAsync"/> hace <c>TRUNCATE ... RESTART IDENTITY CASCADE</c>
///    de las tablas tocadas por el modulo entre tests para mantenerlos
///    independientes sin pagar el costo de levantar otro container.
/// </summary>
public sealed class PostgresIntegrationFixture : IAsyncLifetime
{
    /// <summary>
    /// Imagen alineada con <c>docker-compose.yml</c> (servicio <c>db</c>). Cambios
    /// de mayor version aqui obligan a verificar que las CHECK constraints SQL
    /// del modulo sigan funcionando — Postgres conserva la semantica entre versiones
    /// 14/15/16 pero conviene saberlo.
    /// </summary>
    private const string PostgresImage = "postgres:16";

    private readonly PostgreSqlContainer _container;

    public PostgresIntegrationFixture()
    {
        // Credenciales locales no sensibles — el container es efimero y solo
        // expone su puerto al host de tests. NO se publican fuera del host.
        _container = new PostgreSqlBuilder()
            .WithImage(PostgresImage)
            .WithDatabase("travel_tests")
            .WithUsername("travel_tests_user")
            .WithPassword("travel_tests_password_local_only")
            // PostgreSqlBuilder ya espera el healthcheck pg_isready internamente
            // antes de devolver el control; no hace falta WaitUntil explicito.
            .Build();
    }

    /// <summary>
    /// Connection string apuntando al container. Cambia entre runs (puerto
    /// random asignado por Docker) — nunca hardcodearla.
    /// </summary>
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var ctx = CreateDbContext();

        // Construir schema desde el modelo. La tabla "TravelFiles" queda con su
        // nombre correcto (HasTable via fluent), las owned entities del
        // FiscalSnapshot quedan como columnas con prefijo, y xmin
        // (rowVersion=true) queda configurado como concurrency token.
        await ctx.Database.EnsureCreatedAsync();

        // Aplicar los CHECK constraints SQL crudos del modulo FC1. Estos NO
        // los crea EnsureCreated porque EF no expone los <c>migrationBuilder.Sql</c>
        // del archivo de migracion al schema-from-model. Sin esto los tests
        // no validan lo que vienen a validar.
        //
        // Mismo SQL que la migracion 20260514030142_FC1_AddCancellationModule
        // (DROP IF EXISTS + ADD) para idempotencia local; cualquier cambio
        // alli debe replicarse aca o el contrato del modulo divergira.
        await ApplyCheckConstraintsAsync(ctx);
    }

    private static async Task ApplyCheckConstraintsAsync(AppDbContext ctx)
    {
        // (a) INV-084: AllocatedAmount <= ReceivedAmount.
        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "OperatorRefundsReceived"
              DROP CONSTRAINT IF EXISTS chk_OperatorRefundsReceived_allocated_not_exceeds;
            ALTER TABLE "OperatorRefundsReceived"
              ADD CONSTRAINT chk_OperatorRefundsReceived_allocated_not_exceeds
              CHECK ("AllocatedAmount" >= 0 AND "AllocatedAmount" <= "ReceivedAmount");
            """);

        // (b) INV-085: saldo cliente no negativo y <= credito inicial.
        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "ClientCreditEntries"
              DROP CONSTRAINT IF EXISTS chk_ClientCreditEntries_remaining_non_negative;
            ALTER TABLE "ClientCreditEntries"
              ADD CONSTRAINT chk_ClientCreditEntries_remaining_non_negative
              CHECK ("RemainingBalance" >= 0 AND "RemainingBalance" <= "CreditedAmount");
            """);

        // (c) INV-112: NetAmount >= 0 y GrossAmount >= NetAmount.
        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "OperatorRefundAllocations"
              DROP CONSTRAINT IF EXISTS chk_OperatorRefundAllocations_net_positive;
            ALTER TABLE "OperatorRefundAllocations"
              ADD CONSTRAINT chk_OperatorRefundAllocations_net_positive
              CHECK ("NetAmount" >= 0 AND "GrossAmount" >= "NetAmount");
            """);

        // (d) INV-112: DeductionLine.Amount > 0.
        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "DeductionLines"
              DROP CONSTRAINT IF EXISTS chk_DeductionLines_amount_positive;
            ALTER TABLE "DeductionLines"
              ADD CONSTRAINT chk_DeductionLines_amount_positive
              CHECK ("Amount" > 0);
            """);

        // (e) INV-100: TravelFiles.Status restringido a la whitelist.
        //     Incluye "Archived" (legacy soft-delete) + "PendingOperatorRefund" (FC1).
        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "TravelFiles"
              DROP CONSTRAINT IF EXISTS chk_TravelFiles_status_valid;
            ALTER TABLE "TravelFiles"
              ADD CONSTRAINT chk_TravelFiles_status_valid
              CHECK ("Status" IN (
                'Budget',
                'Confirmed',
                'Traveling',
                'Closed',
                'Cancelled',
                'PendingOperatorRefund',
                'Archived'
              ));
            """);

        // (f) INV-118: FiscalSnapshot consistente fuera de Drafted/Aborted.
        //     Codigo de status: 0=Drafted, 1=AwaitingFiscalConfirmation, ..., 6=Aborted.
        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "BookingCancellations"
              DROP CONSTRAINT IF EXISTS chk_BookingCancellations_fiscalsnapshot_consistent;
            ALTER TABLE "BookingCancellations"
              ADD CONSTRAINT chk_BookingCancellations_fiscalsnapshot_consistent
              CHECK (
                "Status" IN (0, 6)
                OR (
                  "FiscalSnapshot_Source" <> 0
                  AND "FiscalSnapshot_ExchangeRateAtOriginalInvoice" > 0
                  AND "FiscalSnapshot_CurrencyAtEvent" IS NOT NULL
                )
              );
            """);

        // (g) Unique partial index para allocations activas (no voided) por pareja.
        //     Conservamos por completitud aunque ningun test lo ejercita directamente.
        await ctx.Database.ExecuteSqlRawAsync("""
            DROP INDEX IF EXISTS "ix_OperatorRefundAllocations_active_unique_alloc_per_refund_per_bc";
            CREATE UNIQUE INDEX "ix_OperatorRefundAllocations_active_unique_alloc_per_refund_per_bc"
              ON "OperatorRefundAllocations" ("OperatorRefundReceivedId", "BookingCancellationId")
              WHERE "IsVoided" = false;
            """);

        // (h) FC1.3 Fase 2 (F2.1): CHECK de suma del FiscalLiquidation.
        //     Mismo SQL que la migracion Fase2_M1 (mantener sincronizado). EnsureCreated
        //     no los crea porque son migrationBuilder.Sql, no fluent API.
        //     I1 fix: COALESCE(..., 0) en cada componente para que un NULL parcial cuente
        //     como 0 y el CHECK valide de verdad (ver comentario completo en la migracion).
        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "BookingCancellations"
              DROP CONSTRAINT IF EXISTS chk_BookingCancellations_fiscalliquidation_sum;
            ALTER TABLE "BookingCancellations"
              ADD CONSTRAINT chk_BookingCancellations_fiscalliquidation_sum
              CHECK (
                "FiscalLiquidation_FiscalAmountToCredit" IS NULL
                OR ABS(
                     COALESCE("FiscalLiquidation_FiscalAmountToCredit", 0)
                     + COALESCE("FiscalLiquidation_NonRefundableItemsAmount", 0)
                     + COALESCE("FiscalLiquidation_OperatorPenaltyAmount", 0)
                     - COALESCE("FiscalLiquidation_OriginalInvoiceAmount", 0)
                   ) <= 0.01
              );
            """);

        // (i) FC1.3 Fase 2 (F2.1): CHECK de consistencia de timestamp del FiscalLiquidation.
        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "BookingCancellations"
              DROP CONSTRAINT IF EXISTS chk_BookingCancellations_fiscalliquidation_consistency;
            ALTER TABLE "BookingCancellations"
              ADD CONSTRAINT chk_BookingCancellations_fiscalliquidation_consistency
              CHECK (
                "FiscalLiquidation_ComputedAt" IS NULL
                OR "LiquidationComputedAt" = "FiscalLiquidation_ComputedAt"
              );
            """);

        // (j) FC1.3 Fase 3 (ADR-010): CHECK chk_pcnr_status de la bandeja de
        //     reconciliacion de NC parciales. Mismo SQL que la migracion
        //     20260529081148_Fase3_M1_AddPartialCreditNoteReconciliation. EnsureCreated
        //     NO crea estos CHECK porque son migrationBuilder.Sql, no fluent API —
        //     sin esto, los tests de CHECK de Fase 3 no validarian nada.
        //     Status (persistido como string) solo puede ser 'Pending' o 'Resolved'.
        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "PartialCreditNoteReconciliations"
              DROP CONSTRAINT IF EXISTS chk_pcnr_status;
            ALTER TABLE "PartialCreditNoteReconciliations"
              ADD CONSTRAINT chk_pcnr_status
              CHECK ("Status" IN ('Pending', 'Resolved'));
            """);

        // (k) FC1.3 Fase 3 (ADR-010): CHECK chk_pcnr_resolved_consistency. Un caso
        //     marcado 'Resolved' DEBE tener trazabilidad de cierre (ResolvedAt +
        //     ResolvedByUserId NOT NULL). Mismo SQL que la migracion Fase3_M1.
        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "PartialCreditNoteReconciliations"
              DROP CONSTRAINT IF EXISTS chk_pcnr_resolved_consistency;
            ALTER TABLE "PartialCreditNoteReconciliations"
              ADD CONSTRAINT chk_pcnr_resolved_consistency
              CHECK (
                "Status" <> 'Resolved'
                OR ("ResolvedAt" IS NOT NULL AND "ResolvedByUserId" IS NOT NULL)
              );
            """);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Crea un <see cref="AppDbContext"/> nuevo apuntando al container, con el
    /// <see cref="BusinessInvariantInterceptor"/> registrado.
    ///
    /// Por que cada test crea su propio context:
    ///  - Los tests de concurrencia abren 2 contexts paralelos para simular dos
    ///    sesiones que cargan el mismo aggregate. EF Core NO permite compartir
    ///    una entidad tracked entre dos contexts.
    ///  - Reusar un context entre tests tampoco es seguro por el ChangeTracker
    ///    cacheando estados previos.
    /// </summary>
    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .AddInterceptors(new BusinessInvariantInterceptor())
            .Options;

        // IHttpContextAccessor null -> el ctor lo acepta, los audit logs usaran
        // "System"/"Sistema" como user (suficiente para los tests).
        return new AppDbContext(options);
    }

    /// <summary>
    /// FC1.2.2 v3 §7.3 (BR-V2-04, 2026-05-18): construye un IServiceProvider
    /// con TODAS las dependencias necesarias para los services del modulo de
    /// cancelacion/refund. Cada test paralelo (los 4 de concurrencia xmin
    /// abren scopes separados sobre este provider para tener su propio
    /// AppDbContext sin pelear con el ChangeTracker del otro.
    ///
    /// <para>
    /// <b>Smoke test obligatorio</b>: antes de los tests funcionales, validar
    /// con <c>BuildServiceProvider_ResolvesAllServices</c> que todo resuelve.
    /// Si falla, sabemos inmediatamente que falta registrar X — no hay que
    /// debugear via tests funcionales rotos.
    /// </para>
    ///
    /// <para>
    /// <b>Settings: feature flag prendido</b>. Para todos los tests, levantamos
    /// con <c>EnableNewCancellationFlow=true</c>. Es lo unico que evita que
    /// los services rechacen las operaciones con "modulo no habilitado".
    /// </para>
    /// </summary>
    public IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        // EF + interceptor (scoped). Cada scope obtiene su propio AppDbContext.
        services.AddDbContext<AppDbContext>(o => o
            .UseNpgsql(ConnectionString)
            .AddInterceptors(new BusinessInvariantInterceptor()),
            ServiceLifetime.Scoped);

        // Logging null para no spamear test output. NullLogger es seguro thread-wise.
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(NullLoggerFactory.Instance);

        // Repository<T> generico (lo usa AuditService internamente).
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

        // Settings con feature flag prendido. Lo registramos como singleton-mock
        // para que todos los tests del fixture compartan el mismo retorno.
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                OnePerReservaInvoicePolicy = true,
                OperatorRefundTimeoutDays = 60,
            });
        services.AddSingleton(settingsMock.Object);

        // AuditService real (persiste audit logs en Postgres real).
        services.AddScoped<IAuditService, AuditService>();

        // ApprovalRequestService real (FindActiveApproved + MarkConsumed contra BD).
        services.AddScoped<IApprovalRequestService, ApprovalRequestService>();

        // InvoiceService: NO lo usamos en FC1.2.2 (solo BC.ConfirmAsync lo invoca).
        // Lo dejamos como mock no-op para que el BC service compile y resuelva.
        var invoiceMock = new Mock<IInvoiceService>();
        invoiceMock
            .Setup(s => s.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Returns(Task.CompletedTask);
        services.AddSingleton(invoiceMock.Object);

        // BookingCancellationService implementa AMBAS interfaces (split BR-04).
        // Registramos la clase concreta + ambas interfaces como factory que
        // resuelve la misma instancia dentro del scope.
        //
        // Por que registramos las 2 interfaces apuntando a la misma instancia:
        // BookingCancellationService es a la vez IBookingCancellationService (lo
        // usa el flujo principal) y IInvoiceAnnulmentBcBridge (lo usa
        // InvoiceService para notificar callbacks). Sin el bridge, el smoke DI
        // test rompe con "No service for type 'IInvoiceAnnulmentBcBridge'", y
        // ademas la sincronizacion del callback no funciona porque el bridge
        // resolveria una instancia distinta con otro AppDbContext.
        services.AddScoped<BookingCancellationService>();
        services.AddScoped<IBookingCancellationService>(sp =>
            sp.GetRequiredService<BookingCancellationService>());
        services.AddScoped<IInvoiceAnnulmentBcBridge>(sp =>
            sp.GetRequiredService<BookingCancellationService>());

        // OperatorRefundService + ClientCreditService (los 2 de FC1.2.2).
        services.AddScoped<IOperatorRefundService, OperatorRefundService>();
        services.AddScoped<IClientCreditService, ClientCreditService>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Limpia todas las tablas del modulo de cancelacion + las dependencias
    /// minimas que cada test instancia (Customer, Supplier, TravelFiles, Invoices).
    /// Usa <c>TRUNCATE ... RESTART IDENTITY CASCADE</c> para resetear los
    /// IDENTITY sequences — sino los Id seguirian creciendo entre tests y
    /// haria diff debugging mas dificil.
    ///
    /// El orden no importa por <c>CASCADE</c>, pero listamos primero las tablas
    /// del modulo para documentacion. Las tablas Identity (AspNetUsers, etc.) se
    /// dejan intactas porque ningun test las usa para datos.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        await using var ctx = CreateDbContext();

        // TRUNCATE CASCADE en un solo statement: Postgres lo procesa atomicamente.
        // RESTART IDENTITY resetea las secuencias asociadas a cada tabla.
        //
        // FC1.3.0a (2026-05-21): se agrega "ApprovalRequests" al TRUNCATE. Los
        // tests de concurrencia xmin de FC1.3 (M0) seedean approvals reales y
        // necesitan que la tabla este limpia entre tests para no contaminar
        // los queries por EntityId/RequestType. El CASCADE arrastra tambien
        // los FKs de Invoices.AnnulmentApprovalRequestId si los hubiera.
        //
        // FC1.3 Fase 2 Etapa 0 (2026-05-27): se agrega "ArcaIdempotencyKeys" al
        // TRUNCATE. Los tests de la migracion Fase2_M1b (UNIQUE index anti-doble-POST)
        // seedean keys reales y necesitan la tabla limpia entre tests para no chocar
        // por el indice UNIQUE de un test previo. No tiene FKs hacia el resto del
        // modulo (es operacional), por eso el orden no importa.
        // FC1.3 Fase 3 (ADR-010, 2026-05-29): se agregan las dos tablas de la bandeja
        // de reconciliacion de NC parciales. La hija
        // ("PartialCreditNoteReconciliationReceipts") va primero por prolijidad; el
        // CASCADE igual la arrastraria al truncar el padre o las Invoices. Los tests
        // de Fase 3 (xmin + CHECK) seedean casos reales y necesitan la tabla limpia
        // entre tests para no chocar contra el indice UNIQUE de CreditNoteInvoiceId.
        await ctx.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE
                "PartialCreditNoteReconciliationReceipts",
                "PartialCreditNoteReconciliations",
                "ClientCreditWithdrawals",
                "ClientCreditEntries",
                "DeductionLines",
                "OperatorRefundAllocations",
                "OperatorRefundsReceived",
                "BookingCancellations",
                "ManualCashMovements",
                "PaymentReceipts",
                "Invoices",
                "Payments",
                "TravelFiles",
                "Customers",
                "Suppliers",
                "ApprovalRequests",
                "ArcaIdempotencyKeys",
                "AuditLogs"
            RESTART IDENTITY CASCADE;
            """);
    }
}
