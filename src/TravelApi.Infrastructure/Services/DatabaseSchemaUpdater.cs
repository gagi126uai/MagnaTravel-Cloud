using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-052 (D3, cierra el bloqueante B4): la secuencia de "poner el esquema al día", extraída de
/// <c>Program.cs</c> a una clase compartida para que la usen los DOS caminos que la necesitan: el ARRANQUE de la
/// app (o el contenedor <c>migrate</c>, con <c>--migrate-only</c>) y la RESTAURACIÓN de un resguardo de una
/// versión anterior.
///
/// <para><b>Qué hace, en orden</b> (exactamente el mismo orden que un deploy limpio, por eso cualquier resguardo
/// cuyo historial sea "subconjunto final" es soportable sin importar la antigüedad):
/// <list type="number">
///   <item>Los 3 bootstrappers de SQL crudo (finanzas operativas, refresh tokens, cotizaciones BNA).</item>
///   <item><c>MigrateAsync()</c>: aplica las migraciones de EF que falten.</item>
///   <item>Los 3 backfills idempotentes: ADR-021 (saldos por moneda), ADR-022 (libro de caja), ADR-025 (líneas
///   de cancelación). Cada uno arranca con un <c>NeedsBackfillAsync</c> barato, así que en el caso normal son
///   tres consultas y listo.</item>
/// </list></para>
///
/// <para><b>Las dos políticas</b> (ver <see cref="SchemaUpdatePolicy"/>): arranque = 5 intentos y los backfills
/// que fallan se loguean y siguen (comportamiento histórico, intacto); restore = 1 intento y NINGÚN fallo de
/// backfill se traga (hace fallar el paso → el restore vuelve atrás).</para>
///
/// <para><b>Seams para tests</b>: los tres pasos son <c>protected virtual</c>. La política de reintentos y de
/// tolerancia es lo único nuevo y peligroso de esta clase, y es lo que se puede probar sin depender de un
/// historial de migraciones real (que en este repo no se puede aplicar desde una base vacía, ver
/// <c>PostgresIntegrationFixture</c>).</para>
/// </summary>
public class DatabaseSchemaUpdater : ISchemaUpdatePort
{
    /// <summary>
    /// Defaults expuestos <c>internal</c> para que el test guardián de timeouts
    /// (<c>RestoreTotalTimeoutConfigurationTests</c>) los derive de ACÁ y no los duplique a mano.
    /// </summary>
    internal const int DefaultMigrateTimeoutMinutes = 10;

    /// <summary>
    /// <c>CommandTimeout</c> por sentencia durante la actualización. Sin esto rige el default de Npgsql (30 s) y
    /// hay migraciones con SQL crudo largo (backfills adentro de la propia migración) que lo superan.
    /// </summary>
    internal const int DefaultMigrateCommandTimeoutMinutes = 5;

    /// <summary>Intentos y espera del camino de ARRANQUE (comportamiento histórico de <c>Program.cs</c>).</summary>
    internal const int StartupAttempts = 5;

    /// <summary>Intentos del camino de RESTORE: uno solo. Si falla, se vuelve atrás (hay a dónde volver).</summary>
    internal const int RestoreAttempts = 1;

    /// <summary>Espera entre reintentos del arranque. Configurable para que los tests no tengan que esperarla de verdad.</summary>
    internal const int DefaultRetryDelaySeconds = 5;

    private readonly AppDbContext _db;
    private readonly ISupplierService _supplierService;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DatabaseSchemaUpdater> _logger;

    public DatabaseSchemaUpdater(
        AppDbContext db,
        ISupplierService supplierService,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        ILogger<DatabaseSchemaUpdater> logger)
    {
        _db = db;
        _supplierService = supplierService;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Contrato: NUNCA tira. Todo fallo sale como <see cref="SchemaUpdateResult"/> con <c>Success=false</c>, porque
    /// los dos callers necesitan DECIDIR con eso (el restore vuelve atrás; el arranque aborta con su log crítico).
    /// El <c>try</c> exterior es lo que hace que ese contrato valga incluso para lo que no previmos.
    /// </summary>
    public async Task<SchemaUpdateResult> UpdateAsync(SchemaUpdatePolicy policy, CancellationToken ct)
    {
        try
        {
            return await UpdateCoreAsync(policy, ct);
        }
        catch (Exception ex)
        {
            // BLOQUEANTE 3(c) de la re-review: antes, una excepción escapada acá (por ejemplo la del Task.Delay del
            // reintento si el token se cancelaba) rompía el contrato y, en el arranque, se perdía el log CRITICAL
            // histórico que avisa "no se pudieron aplicar las migraciones".
            _logger.LogError(ex, "Actualización de esquema: excepción inesperada (política {Policy}).", policy);
            return new SchemaUpdateResult(false, 0, ex.Message);
        }
    }

    private async Task<SchemaUpdateResult> UpdateCoreAsync(SchemaUpdatePolicy policy, CancellationToken ct)
    {
        var isStartup = policy == SchemaUpdatePolicy.Startup;
        var attemptsAllowed = isStartup ? StartupAttempts : RestoreAttempts;
        var toleratesBackfillFailure = isStartup;
        var retryDelaySeconds = _configuration.GetValue<int?>("Wipe:MigrateRetryDelaySeconds") ?? DefaultRetryDelaySeconds;

        // BLOQUEANTE 3(a) y 3(b) de la re-review: el ARRANQUE conserva EXACTAMENTE el comportamiento histórico — sin
        // tope de tiempo total y con el CommandTimeout de siempre. Un deploy con una migración larga (backfill
        // adentro de la propia migración) no puede empezar a fallar por un tope que esta obra trajo para OTRO
        // camino. El tope y el CommandTimeout largo son SOLO del restore, donde el presupuesto de mantenimiento
        // obliga a acotar el peor caso.
        // En el ARRANQUE no se crea NADA: los pasos reciben el mismo token del caller, tal cual lo recibían antes de
        // esta obra. En el RESTORE se cuelga un tope de tiempo propio.
        using var timeoutCts = isStartup ? null : CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutCts is not null)
        {
            var totalTimeoutMinutes = _configuration.GetValue<int?>("Wipe:MigrateTimeoutMinutes") ?? DefaultMigrateTimeoutMinutes;
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(totalTimeoutMinutes));

            var commandTimeoutMinutes = _configuration.GetValue<int?>("Wipe:MigrateCommandTimeoutMinutes") ?? DefaultMigrateCommandTimeoutMinutes;

            // SetCommandTimeout es una extensión RELACIONAL: contra el proveedor InMemory (tests unitarios) tira
            // InvalidOperationException. Por eso se pregunta primero — sin esto, la clase sería intesteable sin Postgres.
            if (_db.Database.IsRelational())
            {
                _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(commandTimeoutMinutes));
            }
        }

        var stepCt = timeoutCts?.Token ?? ct;

        await RunBootstrappersAsync(stepCt);

        var attemptsLeft = attemptsAllowed;
        while (true)
        {
            attemptsLeft--;
            try
            {
                var applied = await ApplyMigrationsAsync(stepCt);
                var backfillsOk = await RunBackfillsAsync(toleratesBackfillFailure, stepCt);
                if (!backfillsOk)
                {
                    // Solo puede llegar acá con la política de restore (la de arranque devuelve true igual).
                    return new SchemaUpdateResult(false, applied, "Falló un backfill de datos derivados durante la actualización de esquema.");
                }

                return new SchemaUpdateResult(true, applied, null);
            }
            catch (Exception ex)
            {
                if (attemptsLeft <= 0)
                {
                    _logger.LogError(ex,
                        "Actualización de esquema FALLIDA tras {Attempts} intento(s) (política {Policy}).",
                        attemptsAllowed, policy);
                    return new SchemaUpdateResult(false, 0, ex.Message);
                }

                _logger.LogWarning(
                    "Actualización de esquema falló, reintentando en {Delay}s (quedan {AttemptsLeft} intentos). Motivo interno: {Message}",
                    retryDelaySeconds, attemptsLeft, ex.Message);

                // La espera del reintento va en su PROPIO try: si el token se cancelara justo acá, un Task.Delay que
                // tira convertiría "no pude migrar" (con su motivo real) en una excepción cruda, y el arranque
                // perdería el log crítico que explica qué pasó.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), stepCt);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "Actualización de esquema FALLIDA y además se agotó el tiempo antes de poder reintentar (política {Policy}).",
                        policy);
                    return new SchemaUpdateResult(false, 0, ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Los 3 bootstrappers de SQL crudo que corren ANTES de <c>MigrateAsync</c>. Son parches idempotentes
    /// (<c>ADD COLUMN IF NOT EXISTS</c> / <c>CREATE TABLE IF NOT EXISTS</c>) y su fallo se TOLERA en las dos
    /// políticas, igual que antes de esta obra.
    ///
    /// <para><b>Por qué se tolera incluso en restore</b> (desvío declarado respecto de "en restore no se traga
    /// nada"): estos pasos no aportan datos derivados, solo preparan el terreno para que las migraciones puedan
    /// aplicarse. Si un bootstrapper falla de verdad, el problema aparece INMEDIATAMENTE en el paso siguiente
    /// (<c>MigrateAsync</c>), que sí es fatal en las dos políticas y dispara la vuelta atrás. Hacerlos fatales
    /// acá agregaría rechazos de restauraciones legítimas sin ganar ninguna garantía.</para>
    /// </summary>
    protected virtual async Task RunBootstrappersAsync(CancellationToken ct)
    {
        await TryBootstrapAsync("finanzas operativas", async () =>
        {
            await OperationalFinanceSchemaBootstrapper.EnsureAsync(_db, ct);
            await OperationalFinanceSchemaBootstrapper.MarkOperationalFinanceMigrationAsAppliedAsync(_db, ct);
        });

        await TryBootstrapAsync("refresh tokens", async () =>
        {
            await RefreshTokenSchemaBootstrapper.EnsureAsync(_db, ct);
            await RefreshTokenSchemaBootstrapper.MarkRefreshTokenMigrationAsAppliedAsync(_db, ct);
        });

        await TryBootstrapAsync("cotizaciones BNA", () => BnaExchangeRateSchemaBootstrapper.EnsureAsync(_db, ct));
    }

    private async Task TryBootstrapAsync(string nombreDelPaso, Func<Task> paso)
    {
        try
        {
            await paso();
            _logger.LogInformation("Bootstrap de esquema ({Paso}) terminado.", nombreDelPaso);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Bootstrap de esquema ({Paso}) salteado o fallido: {Message}", nombreDelPaso, ex.Message);
        }
    }

    /// <summary>
    /// Aplica las migraciones que falten y devuelve CUÁNTAS se aplicaron. El conteo se toma ANTES de migrar
    /// (las pendientes de ese momento) porque después de un <c>MigrateAsync</c> exitoso ya no queda ninguna.
    /// </summary>
    protected virtual async Task<int> ApplyMigrationsAsync(CancellationToken ct)
    {
        var pending = (await _db.Database.GetPendingMigrationsAsync(ct)).Count();
        if (pending == 0)
        {
            _logger.LogInformation("Actualización de esquema: no había migraciones pendientes.");
            return 0;
        }

        _logger.LogInformation("Actualización de esquema: aplicando {Pending} migración(es) pendiente(s)...", pending);
        await _db.Database.MigrateAsync(ct);
        _logger.LogInformation("Actualización de esquema: {Pending} migración(es) aplicada(s).", pending);
        return pending;
    }

    /// <summary>
    /// Los 3 backfills idempotentes de datos DERIVADOS. Devuelve <c>false</c> solo cuando la política NO tolera
    /// fallos (restore) y alguno falló — ahí el caller vuelve atrás.
    /// </summary>
    protected virtual async Task<bool> RunBackfillsAsync(bool toleratesFailure, CancellationToken ct)
    {
        var multiCurrencyOk = await TryRunBackfillAsync(
            "ADR-021 saldos por moneda", toleratesFailure, () => RunMultiCurrencyBackfillAsync(ct));

        var cashLedgerOk = await TryRunBackfillAsync(
            "ADR-022 libro de caja", toleratesFailure, () => RunCashLedgerBackfillAsync(ct));

        var cancellationLinesOk = await TryRunBackfillAsync(
            "ADR-025 líneas de cancelación", toleratesFailure, () => RunCancellationLinesBackfillAsync(ct));

        return multiCurrencyOk && cashLedgerOk && cancellationLinesOk;
    }

    /// <summary>
    /// ADR-021: saldos por moneda. Cada backfill es un método <c>protected virtual</c> propio para que los tests
    /// puedan hacer fallar UNO y ejercitar la lógica REAL de tolerancia de <see cref="RunBackfillsAsync"/> (antes, un
    /// test que sobreescribía <c>RunBackfillsAsync</c> terminaba probando su propia reimplementación).
    /// </summary>
    protected virtual async Task RunMultiCurrencyBackfillAsync(CancellationToken ct)
    {
        var backfill = new MultiCurrencyBackfillService(
            _db, _supplierService, _loggerFactory.CreateLogger<MultiCurrencyBackfillService>());
        if (!await backfill.NeedsBackfillAsync(ct))
        {
            return;
        }

        var (reservasDone, suppliersDone) = await backfill.RunAsync(ct);
        _logger.LogInformation(
            "ADR-021 backfill terminado. Reservas={Reservas}, Proveedores={Suppliers}.", reservasDone, suppliersDone);
    }

    /// <summary>ADR-022: libro de caja. Ver <see cref="RunMultiCurrencyBackfillAsync"/> sobre por qué es virtual.</summary>
    protected virtual async Task RunCashLedgerBackfillAsync(CancellationToken ct)
    {
        var backfill = new CashLedgerBackfillService(_db, _loggerFactory.CreateLogger<CashLedgerBackfillService>());
        if (!await backfill.NeedsBackfillAsync(ct))
        {
            return;
        }

        var (payments, supplierPayments, manuals) = await backfill.RunAsync(ct);
        _logger.LogInformation(
            "ADR-022 backfill terminado. Cobros={Payments}, PagosProveedor={SupplierPayments}, Manuales={Manuals}.",
            payments, supplierPayments, manuals);
    }

    /// <summary>ADR-025: líneas de cancelación. Ver <see cref="RunMultiCurrencyBackfillAsync"/> sobre por qué es virtual.</summary>
    protected virtual async Task RunCancellationLinesBackfillAsync(CancellationToken ct)
    {
        var backfill = new BookingCancellationLineBackfillService(
            _db, _loggerFactory.CreateLogger<BookingCancellationLineBackfillService>());
        if (!await backfill.NeedsBackfillAsync(ct))
        {
            return;
        }

        var lines = await backfill.RunAsync(ct);
        _logger.LogInformation("ADR-025 backfill terminado. Lineas={Lines}.", lines);
    }

    private async Task<bool> TryRunBackfillAsync(string nombreDelBackfill, bool toleratesFailure, Func<Task> backfill)
    {
        try
        {
            await backfill();
            return true;
        }
        catch (Exception ex)
        {
            if (toleratesFailure)
            {
                _logger.LogError(ex,
                    "Backfill {Backfill} falló. El arranque continúa: los datos derivados se completan en el próximo recálculo o deploy.",
                    nombreDelBackfill);
                return true;
            }

            _logger.LogCritical(ex,
                "Backfill {Backfill} falló DURANTE UNA RESTAURACIÓN. No se tolera: dejar los datos derivados de plata en cero " +
                "sería un dato silencioso falso, así que la restauración vuelve atrás.",
                nombreDelBackfill);
            return false;
        }
    }
}
