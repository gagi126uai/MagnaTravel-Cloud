using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Obra "Restaurar desde la app" (2026-07-27, Parte B firmada): implementación REAL de
/// <see cref="IDatabaseRestorePort"/>. Corre <c>pg_restore</c> como proceso externo (mismo binario
/// <c>postgresql-client-16</c> que ya instala el Dockerfile para el backup, ver
/// <c>PgDumpAndMinioWipeBackupPort</c>) y administra la base sombra con un <see cref="NpgsqlConnection"/>
/// directo (no via EF: crear/borrar una base de datos completa no es algo que <c>AppDbContext</c> sepa hacer,
/// y además <c>CREATE DATABASE</c>/<c>DROP DATABASE</c> no pueden correr dentro de una transacción).
///
/// <para><b>ADR-052 (2026-07-29)</b>: la restauración total dejó de hacerse sobre la base VIVA. Ahora se
/// restaura en una base NUEVA al costado y se INTERCAMBIAN LOS NOMBRES (<see cref="RestoreIntoNewDatabaseAsync"/>
/// + <see cref="SwapRestoredDatabaseIntoLiveAsync"/>), con vuelta atrás por otro intercambio
/// (<see cref="RollbackSwapAsync"/>). Todo lo que toca nombres de bases reconcilia POR ESTADO consultando
/// <c>pg_database</c>, nunca ejecutando una secuencia ciega de pasos.</para>
/// </summary>
public class PgDatabaseRestorePort : IDatabaseRestorePort
{
    /// <summary>
    /// Defaults expuestos <c>internal</c> (con <c>InternalsVisibleTo("TravelApi.Tests")</c> ya configurado)
    /// para que el test guardián de la invariante de timeouts (<c>RestoreTotalTimeoutConfigurationTests</c>,
    /// hallazgo B-N2(d)) los derive de ACÁ en vez de mantener números duplicados que se puedan desincronizar
    /// en silencio.
    /// </summary>
    internal const int DefaultPgRestoreTotalTimeoutMinutes = 15;

    /// <summary>Ver <see cref="DefaultPgRestoreTotalTimeoutMinutes"/>. Hallazgo B-N2(b): el chequeo de esquema no tenía timeout propio antes de esta obra.</summary>
    internal const int DefaultSchemaCheckTimeoutMinutes = 3;

    /// <summary>
    /// ADR-052 (D1.4 y D8): intentos del intercambio de nombres y espera entre intentos. El único motivo
    /// esperable de fallo es una conexión que entró justo en la ventana, así que reintentar poco y rápido es lo
    /// correcto (el peor caso entra de sobra en el presupuesto de mantenimiento).
    /// </summary>
    internal const int DefaultSwapRetries = 5;

    /// <summary>Ver <see cref="DefaultSwapRetries"/>.</summary>
    internal const int DefaultSwapRetryDelaySeconds = 2;

    /// <summary>ADR-052 (D4/M2): intentos de la VUELTA ATRÁS antes de declarar doble fallo (mantenimiento sostenido).</summary>
    internal const int DefaultRollbackSwapRetries = 3;

    /// <summary>Ver <see cref="DefaultRollbackSwapRetries"/>.</summary>
    internal const int DefaultRollbackSwapRetryDelaySeconds = 2;

    /// <summary>
    /// ADR-052 (D5): timeout de la lectura BARATA del historial de un resguardo para la lista. Corta y por
    /// archivo: si un archivo tarda más que esto, su marca queda "desconocida" y la lista sigue andando.
    /// </summary>
    private const int CheapHistoryReadTimeoutSeconds = 30;

    /// <summary>
    /// ADR-052 (D5): cuánto se recuerda un intento FALLIDO de leer el historial. Los dumps son inmutables, así
    /// que un éxito se puede cachear para siempre (la clave lleva tamaño+fecha y se auto-invalida); un fallo, en
    /// cambio, puede ser transitorio (el binario ocupado, un timeout), y cachearlo para siempre dejaría el
    /// archivo marcado "desconocida" hasta que alguien reinicie la app.
    /// </summary>
    private static readonly TimeSpan FailedHistoryReadCacheDuration = TimeSpan.FromMinutes(2);

    private readonly IConfiguration _configuration;
    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PgDatabaseRestorePort> _logger;

    public PgDatabaseRestorePort(
        IConfiguration configuration,
        AppDbContext context,
        IMemoryCache cache,
        ILogger<PgDatabaseRestorePort> logger)
    {
        _configuration = configuration;
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    private string BackupDirectory => _configuration["Wipe:BackupDirectory"] ?? "/backups/wipe";

    public async Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync(CancellationToken ct)
    {
        var directory = BackupDirectory;
        if (!Directory.Exists(directory))
        {
            return Array.Empty<BackupFileInfo>();
        }

        var files = Directory.GetFiles(directory, "*.dump")
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToList();

        // ADR-052 (D5): la lista de migraciones del sistema se lee UNA vez para todos los archivos (viene del
        // ensamblado, no de la base: es una lista compilada, gratis de leer). El veredicto se recalcula SIEMPRE
        // contra ella; lo único que se cachea es el historial del archivo. Si se cacheara el veredicto, el
        // primer deploy posterior dejaría toda la lista mintiendo.
        List<string> assemblyMigrations;
        try
        {
            assemblyMigrations = _context.Database.GetMigrations().ToList();
        }
        catch (Exception ex)
        {
            // GetMigrations es una extensión RELACIONAL. En producción el proveedor siempre es Npgsql, pero si algún
            // día no lo fuera, la lista de resguardos tiene que seguir funcionando: sin referencia contra la cual
            // comparar, TODOS quedan "desconocida" (nunca "actual").
            _logger.LogWarning(ex, "Marca de versión de resguardos: no se pudo leer la lista de migraciones del sistema.");
            assemblyMigrations = new List<string>();
        }

        var result = new List<BackupFileInfo>(files.Count);
        foreach (var info in files)
        {
            var dumpMigrations = await TryReadDumpMigrationIdsCachedAsync(info, ct);
            var versionState = dumpMigrations is null
                ? BackupVersionStates.Desconocida
                : RestoreSchemaVerdictRules.ToVersionState(
                    RestoreSchemaVerdictRules.Evaluate(assemblyMigrations, dumpMigrations, liveHasPendingMigrations: false));

            result.Add(new BackupFileInfo(info.Name, info.LastWriteTimeUtc, info.Length, versionState));
        }

        return result;
    }

    /// <summary>
    /// ADR-052 (D5), camino INFORMATIVO: lee el historial de migraciones de un dump SIN base de datos
    /// (<c>pg_restore --data-only --table=__EFMigrationsHistory -f -</c> imprime el bloque <c>COPY</c> por salida
    /// estándar) y lo cachea por archivo.
    ///
    /// <para><b>Clave de caché</b>: nombre + tamaño + fecha de modificación. Los dumps son inmutables una vez
    /// escritos, así que la clave se auto-invalida sola y no hace falta TTL para el caso exitoso.</para>
    ///
    /// <para><b>Riesgo asumido, declarado en el ADR</b>: parsear la salida de <c>pg_restore</c> ya nos falló una
    /// vez (el índice lista los nombres SIN comillas). Acá, si algo no cierra, devuelve <c>null</c> y el archivo
    /// queda marcado "no se pudo determinar" — JAMÁS "compatible". Este camino no habilita ninguna restauración.</para>
    /// </summary>
    private async Task<ISet<string>?> TryReadDumpMigrationIdsCachedAsync(FileInfo info, CancellationToken ct)
    {
        var cacheKey = $"adr052-dump-migrations::{info.Name}::{info.Length}::{info.LastWriteTimeUtc.Ticks}";
        if (_cache.TryGetValue(cacheKey, out ISet<string>? cached))
        {
            return cached;
        }

        var ids = await TryReadDumpMigrationIdsAsync(info.FullName, ct);
        if (ids is null)
        {
            _cache.Set(cacheKey, (ISet<string>?)null, FailedHistoryReadCacheDuration);
            return null;
        }

        _cache.Set(cacheKey, ids);
        return ids;
    }

    /// <summary>
    /// La lectura en sí (un <c>pg_restore</c> por archivo). Es <c>protected virtual</c> para que el test de la CACHÉ
    /// pueda contar cuántas veces se lee de verdad cada archivo sin depender de los binarios de Postgres.
    /// </summary>
    protected virtual async Task<ISet<string>?> TryReadDumpMigrationIdsAsync(string fullPath, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(CheapHistoryReadTimeoutSeconds));

        var (success, stdout, errorMessage) = await RunProcessAsync(
            "pg_restore",
            $"--data-only --no-owner --no-acl --table=__EFMigrationsHistory -f - \"{fullPath}\"",
            timeoutCts.Token);

        if (!success || string.IsNullOrWhiteSpace(stdout))
        {
            _logger.LogDebug(
                "Marca de versión de resguardo: no se pudo leer el historial de {Archivo}. Motivo interno: {Error}",
                Path.GetFileName(fullPath), errorMessage);
            return null;
        }

        var ids = ParseMigrationIdsFromDumpText(stdout);
        return ids.Count == 0 ? null : ids;
    }

    /// <summary>
    /// Saca los ids de migración del texto SQL que imprime <c>pg_restore --data-only ... -f -</c>. El formato
    /// normal es un bloque <c>COPY</c>:
    /// <code>
    /// COPY public."__EFMigrationsHistory" ("MigrationId", "ProductVersion") FROM stdin;
    /// 20260322010000_AddOperationalFinanceAndTreasury	8.0.13
    /// \.
    /// </code>
    /// Se soporta también la variante <c>INSERT INTO</c> (algunos dumps se generan con <c>--inserts</c>) por si
    /// aparece: es texto ajeno, así que conviene ser tolerante y NUNCA tirar excepción — un formato inesperado
    /// devuelve lista vacía y el archivo queda "desconocida".
    /// </summary>
    internal static ISet<string> ParseMigrationIdsFromDumpText(string dumpText)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var insideCopyBlock = false;

        foreach (var rawLine in dumpText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (insideCopyBlock)
            {
                if (line == "\\.")
                {
                    insideCopyBlock = false;
                    continue;
                }

                // Dentro de un COPY las columnas van separadas por TAB y la primera es "MigrationId".
                var migrationId = line.Split('\t')[0].Trim();
                if (migrationId.Length > 0)
                {
                    ids.Add(migrationId);
                }

                continue;
            }

            if (line.StartsWith("COPY ", StringComparison.Ordinal)
                && line.Contains("__EFMigrationsHistory", StringComparison.Ordinal)
                && line.EndsWith("FROM stdin;", StringComparison.Ordinal))
            {
                insideCopyBlock = true;
                continue;
            }

            if (line.StartsWith("INSERT INTO ", StringComparison.Ordinal)
                && line.Contains("__EFMigrationsHistory", StringComparison.Ordinal))
            {
                var valuesIndex = line.IndexOf("VALUES", StringComparison.Ordinal);
                if (valuesIndex < 0)
                {
                    continue;
                }

                var firstQuote = line.IndexOf('\'', valuesIndex);
                if (firstQuote < 0)
                {
                    continue;
                }

                var secondQuote = line.IndexOf('\'', firstQuote + 1);
                if (secondQuote > firstQuote + 1)
                {
                    ids.Add(line[(firstQuote + 1)..secondQuote]);
                }
            }
        }

        return ids;
    }

    public async Task<RestoreVerifyResult> VerifyBackupAsync(string fileName, CancellationToken ct)
    {
        var fullPath = ResolveSafeBackupPath(fileName);
        if (fullPath is null)
        {
            return new RestoreVerifyResult(false, "Nombre de archivo inválido.", 0, false);
        }

        if (!File.Exists(fullPath))
        {
            return new RestoreVerifyResult(false, "El archivo de backup no existe.", 0, false);
        }

        var (success, stdout, errorMessage) = await RunProcessAsync(
            "pg_restore", $"--list \"{fullPath}\"", ct);
        if (!success)
        {
            return new RestoreVerifyResult(false, errorMessage, 0, false);
        }

        // El indice (TOC) de pg_restore --list lista una linea por objeto restaurable, con el tipo de objeto
        // en mayusculas (TABLE, INDEX, CONSTRAINT, etc). Contamos solo las lineas de tabla para "cuantas
        // tablas trae este backup", y buscamos algunas tablas clave para dar una señal rapida de "esto es
        // un backup de este sistema".
        var lines = stdout!.Split('\n');
        var tableCount = lines.Count(line => line.Contains(" TABLE ", StringComparison.Ordinal));
        var tableNames = ParseTableNamesFromToc(stdout);
        var keyTables = new[] { "TravelFiles", "Customers", "Invoices", "AgencySettings" };
        var hasKeyTables = keyTables.Any(key => tableNames.Contains(key, StringComparer.Ordinal));

        return new RestoreVerifyResult(true, null, tableCount, hasKeyTables);
    }

    /// <summary>
    /// Saca los nombres de tabla del indice (TOC) que imprime <c>pg_restore --list</c>. Cada linea de tabla
    /// tiene la forma <c>"215; 1259 16456 TABLE public TravelFiles traveluser"</c>: el nombre va DOS lugares
    /// despues del token TABLE (primero el schema). OJO: el TOC los imprime SIN comillas aunque sean
    /// PascalCase — buscarlos entrecomillados daba siempre "no encontrado" y hacia que un resguardo sano se
    /// reportara como sospechoso ("podria faltarle alguna parte clave").
    /// </summary>
    internal static IReadOnlyList<string> ParseTableNamesFromToc(string toc)
    {
        var nombres = new List<string>();
        foreach (var line in toc.Split('\n'))
        {
            var partes = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var idx = Array.IndexOf(partes, "TABLE");
            if (idx >= 0 && idx + 2 < partes.Length)
            {
                nombres.Add(partes[idx + 2]);
            }
        }

        return nombres;
    }

    public async Task<ShadowRestoreResult> RestoreToShadowDatabaseAsync(string fileName, CancellationToken ct)
    {
        var fullPath = ResolveSafeBackupPath(fileName);
        if (fullPath is null || !File.Exists(fullPath))
        {
            return new ShadowRestoreResult(false, "El archivo de backup no existe.", null);
        }

        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new ShadowRestoreResult(false, "No hay connection string configurada.", null);
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var shadowDatabaseName = $"{builder.Database}_shadow";

        try
        {
            await RecreateEmptyDatabaseAsync(builder, shadowDatabaseName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restaurar (modo prueba): no se pudo recrear la base sombra {ShadowDb}.", shadowDatabaseName);
            return new ShadowRestoreResult(false, $"No se pudo preparar la base de prueba: {ex.Message}", null);
        }

        var restoreArgs = $"--no-owner --no-acl --if-exists --clean -h {builder.Host} -p {builder.Port} " +
                           $"-U {builder.Username} -d {shadowDatabaseName} \"{fullPath}\"";
        var (success, _, errorMessage) = await RunProcessAsync(
            "pg_restore", restoreArgs, ct, password: builder.Password);
        if (!success)
        {
            return new ShadowRestoreResult(false, errorMessage, null);
        }

        var shadowConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = shadowDatabaseName,
        }.ConnectionString;

        return new ShadowRestoreResult(true, null, shadowConnectionString);
    }

    public async Task<LiveTableRestoreResult> RestoreTablesIntoLiveDatabaseAsync(
        string fileName, IReadOnlyList<string> tableNames, CancellationToken ct)
    {
        var fullPath = ResolveSafeBackupPath(fileName);
        if (fullPath is null || !File.Exists(fullPath))
        {
            return new LiveTableRestoreResult(false, "El archivo de backup no existe.", Array.Empty<string>(), Array.Empty<string>());
        }

        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new LiveTableRestoreResult(false, "No hay connection string configurada.", Array.Empty<string>(), Array.Empty<string>());
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var restored = new List<string>();
        var skippedNonEmpty = new List<string>();

        // Tabla por tabla (en vez de un unico pg_restore con varios --table): si una falla o esta ocupada,
        // las anteriores ya quedaron restauradas y el resultado es exacto sobre cuales se tocaron.
        foreach (var table in tableNames)
        {
            ct.ThrowIfCancellationRequested();

            // Defensa final (belt-and-suspenders): SystemDataRestoreService ya valido esto antes de llamar
            // al puerto, pero re-chequeamos aca mismo, lo mas cerca posible del pg_restore real, para acortar
            // la ventana TOCTOU.
            var isEmpty = await IsTableEmptyAsync(connectionString, table, ct);
            if (!isEmpty)
            {
                skippedNonEmpty.Add(table);
                continue;
            }

            // --single-transaction (hallazgo menor de la ronda de revisión, punto 2): envuelve TODO el
            // restore de esta tabla en una única transacción de Postgres. Sin esto, si pg_restore fallaba a
            // mitad de camino (ej. se cortó la conexión, un error en una fila del medio), la tabla podía
            // quedar con ALGUNAS filas insertadas — ni vacía (para reintentar limpio) ni completa. Con
            // --single-transaction, una falla a mitad de camino hace ROLLBACK automático: la tabla queda
            // exactamente como estaba (vacía), y el chequeo "IsTableEmptyAsync" del próximo intento vuelve a
            // dar OK en vez de saltearla creyendo que "ya tenía datos".
            var restoreArgs = $"--data-only --no-owner --no-acl --single-transaction -h {builder.Host} -p {builder.Port} " +
                               $"-U {builder.Username} -d {builder.Database} --table={table} \"{fullPath}\"";
            var (success, _, errorMessage) = await RunProcessAsync(
                "pg_restore", restoreArgs, ct, password: builder.Password);

            if (!success)
            {
                _logger.LogError("Restaurar (modo real): fallo restaurando la tabla {Table}. Motivo interno: {Error}", table, errorMessage);
                return new LiveTableRestoreResult(false, errorMessage, restored, skippedNonEmpty);
            }

            restored.Add(table);
        }

        return new LiveTableRestoreResult(true, null, restored, skippedNonEmpty);
    }

    public async Task<NewDatabaseRestoreResult> RestoreIntoNewDatabaseAsync(string fileName, CancellationToken ct)
    {
        var fullPath = ResolveSafeBackupPath(fileName);
        if (fullPath is null || !File.Exists(fullPath))
        {
            return new NewDatabaseRestoreResult(TotalRestoreOutcome.Completed, false, null, "El archivo de backup no existe.");
        }

        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new NewDatabaseRestoreResult(TotalRestoreOutcome.Completed, false, null, "No hay connection string configurada.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var newDatabaseName = BuildTimestampedDatabaseName(builder.Database!, "restore");

        try
        {
            await CreateEmptyDatabaseAsync(builder, newDatabaseName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Restaurar TOTAL: no se pudo crear la base nueva {NuevaBase} para restaurar el resguardo.", newDatabaseName);
            return new NewDatabaseRestoreResult(TotalRestoreOutcome.Completed, false, null, $"No se pudo crear la base nueva: {ex.Message}");
        }

        // ADR-052 (D1.2): el dump COMPLETO en una base VACÍA. No hace falta --clean ni --if-exists (no hay nada
        // previo que dropear) y por eso NO puede quedar esquema híbrido — el defecto de fondo del diseño anterior.
        // --no-owner/--no-acl: un dump tomado con OTRO rol de Postgres trae órdenes "ALTER ... OWNER TO <rol>";
        // si ese rol no existe acá, dentro de --single-transaction UN solo fallo aborta la restauración COMPLETA.
        var restoreArgs = $"--no-owner --no-acl --single-transaction -h {builder.Host} -p {builder.Port} " +
                           $"-U {builder.Username} -d {newDatabaseName} \"{fullPath}\"";
        var result = await RunPgRestoreTotalProcessAsync(restoreArgs, builder.Password, ct);

        if (result.Success)
        {
            return new NewDatabaseRestoreResult(result.Outcome, true, newDatabaseName, null);
        }

        // La base viva NUNCA se tocó: lo único que queda es una base a medio poblar, que es basura. Se intenta
        // dropear ya (best-effort); si no se puede —por ejemplo porque el pg_restore que matamos por timeout
        // todavía la tiene tomada— la limpieza del próximo intento la levanta (D1.6).
        await DropDatabaseAsync(newDatabaseName, CancellationToken.None);
        return new NewDatabaseRestoreResult(result.Outcome, false, null, result.ErrorMessage);
    }

    public async Task<DatabaseSwapResult> SwapRestoredDatabaseIntoLiveAsync(string newDatabaseName, CancellationToken ct)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new DatabaseSwapResult(false, string.Empty, "No hay connection string configurada.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var liveDatabaseName = builder.Database!;
        var previousDatabaseName = BuildTimestampedDatabaseName(liveDatabaseName, "old");

        var retries = _configuration.GetValue<int?>("Wipe:SwapRetries") ?? DefaultSwapRetries;
        var delaySeconds = _configuration.GetValue<int?>("Wipe:SwapRetryDelaySeconds") ?? DefaultSwapRetryDelaySeconds;

        string? lastError = null;
        try
        {
            // Sin esto, el worker de Hangfire (que vive en la MISMA base y reconecta solo) se mete entre el
            // pg_terminate_backend y el RENAME, y el rename falla con "database is being accessed by other users".
            //
            // Recomendación N1 de backend (re-review): con su propio try/catch. Este paso es el PRIMERO y todavía no
            // renombró nada; si falla (Postgres no responde, privilegios), lo correcto es devolver un resultado
            // "no pude" —que el caller rechaza limpio y auditado— en vez de dejar salir una excepción cruda.
            try
            {
                await SetAllowConnectionsAsync(builder, liveDatabaseName, allow: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Restaurar TOTAL: no se pudo cerrar la puerta de entrada de la base viva antes del intercambio. No se renombró nada.");
                return new DatabaseSwapResult(false, previousDatabaseName,
                    $"No se pudo preparar el intercambio de nombres: {ex.Message}");
            }

            for (var attempt = 1; attempt <= retries; attempt++)
            {
                try
                {
                    var done = await ReconcileSwapStepAsync(builder, liveDatabaseName, newDatabaseName, previousDatabaseName, ct);
                    if (done)
                    {
                        _logger.LogWarning(
                            "Restaurar TOTAL: intercambio de nombres COMPLETO en el intento {Attempt}. La base anterior quedó como {BaseAnterior}.",
                            attempt, previousDatabaseName);
                        return new DatabaseSwapResult(true, previousDatabaseName, null);
                    }

                    lastError = "El intercambio de nombres no terminó de aplicarse.";
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    _logger.LogWarning(ex,
                        "Restaurar TOTAL: falló el intento {Attempt}/{Retries} del intercambio de nombres.", attempt, retries);
                }

                if (attempt < retries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), CancellationToken.None);
                }
            }

            // Los reintentos se agotaron. Si quedamos a mitad de camino (la base original ya está estacionada
            // bajo el nombre "old" y el nombre vivo no existe), lo DESHACEMOS acá mismo: el nombre vivo tiene que
            // volver a apuntar a la base original antes de devolver el fallo.
            await TryUndoHalfDoneSwapAsync(builder, liveDatabaseName, previousDatabaseName);
            _logger.LogError(
                "Restaurar TOTAL: el intercambio de nombres FALLÓ tras {Retries} intentos. Motivo interno del último intento: {Error}",
                retries, lastError);
            return new DatabaseSwapResult(false, previousDatabaseName, lastError);
        }
        catch (Exception ex)
        {
            // Red del PUERTO (bloqueante 1 de la re-review, atacado también en el origen): este método NUNCA tira.
            // Devolver un resultado —y no una excepción— es lo que garantiza que el caller siempre reciba el nombre
            // bajo el que quedó (o iba a quedar) la base anterior, que es lo único que necesita para reconciliar.
            _logger.LogCritical(ex, "Restaurar TOTAL: excepción inesperada durante el intercambio de nombres.");
            await TryUndoHalfDoneSwapAsync(builder, liveDatabaseName, previousDatabaseName);
            return new DatabaseSwapResult(false, previousDatabaseName, ex.Message);
        }
        finally
        {
            // INVARIANTE CRÍTICA (riesgo nuevo más serio de esta obra): la base que tenga el NOMBRE VIVO queda
            // SIEMPRE con ALLOW_CONNECTIONS true, pase lo que pase. Si esto se olvidara, el sistema queda muerto
            // para todos aunque los datos estén perfectos. Salida de emergencia en docs/db-operations.md.
            await TryAllowConnectionsToLiveNameAsync(builder, liveDatabaseName);
        }
    }

    public async Task<DatabaseSwapRollbackResult> RollbackSwapAsync(string previousDatabaseName, CancellationToken ct)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new DatabaseSwapRollbackResult(false, "No hay connection string configurada.");
        }

        if (string.IsNullOrWhiteSpace(previousDatabaseName))
        {
            return new DatabaseSwapRollbackResult(false, "No se sabe con qué nombre quedó la base anterior.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var liveDatabaseName = builder.Database!;
        var failedDatabaseName = BuildTimestampedDatabaseName(liveDatabaseName, "fallido");

        // Guard ESPEJO del de DropDatabaseAsync (recomendación N4 de seguridad): si un caller futuro pasara el
        // nombre de la base VIVA como "la base anterior", la reconciliación la renombraría a "_fallido_" y después
        // buscaría una base anterior que no existe → sistema sin base viva. Fail-closed: no se toca nada y se
        // devuelve "no pude" (el caller lo trata como doble fallo, que deja el sistema frenado y avisando).
        if (string.Equals(previousDatabaseName, liveDatabaseName, StringComparison.Ordinal))
        {
            _logger.LogCritical(
                "Restaurar TOTAL: se pidió volver atrás usando el nombre de la base VIVA como base anterior. Se ignora el pedido.");
            return new DatabaseSwapRollbackResult(false, "El nombre de la base anterior no puede ser el de la base viva.");
        }

        var retries = _configuration.GetValue<int?>("Wipe:RollbackSwapRetries") ?? DefaultRollbackSwapRetries;
        var delaySeconds = _configuration.GetValue<int?>("Wipe:RollbackSwapRetryDelaySeconds") ?? DefaultRollbackSwapRetryDelaySeconds;

        string? lastError = null;
        try
        {
            await using (var probe = new NpgsqlConnection(BuildMaintenanceConnectionString(builder)))
            {
                await probe.OpenAsync(ct);

                // RECONCILIACIÓN POR ESTADO (condición C1): si la base original NO está estacionada bajo el
                // nombre "old", entonces YA tiene el nombre vivo (el intercambio nunca llegó a hacerse, o esta
                // vuelta atrás ya corrió). No hay nada que deshacer y es SEGURO llamar a este método igual, sin
                // averiguar antes "¿hasta dónde llegué?". Sin este chequeo, una vuelta atrás disparada por las
                // dudas renombraría la base BUENA a "fallido" y dejaría el sistema sin base viva.
                if (!await DatabaseExistsAsync(probe, previousDatabaseName, ct))
                {
                    _logger.LogWarning(
                        "Restaurar TOTAL: no hace falta volver atrás — la base original ya tiene el nombre vivo (no existe {BaseAnterior}).",
                        previousDatabaseName);
                    return new DatabaseSwapRollbackResult(true, null);
                }
            }

            await SetAllowConnectionsAsync(builder, liveDatabaseName, allow: false, ct);

            for (var attempt = 1; attempt <= retries; attempt++)
            {
                try
                {
                    var done = await ReconcileRollbackStepAsync(builder, liveDatabaseName, previousDatabaseName, failedDatabaseName, ct);
                    if (done)
                    {
                        _logger.LogWarning(
                            "Restaurar TOTAL: VUELTA ATRÁS completa en el intento {Attempt}. La base del intento fallido quedó como {BaseFallida} para diagnóstico.",
                            attempt, failedDatabaseName);
                        return new DatabaseSwapRollbackResult(true, null);
                    }

                    lastError = "La vuelta atrás no terminó de aplicarse.";
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    _logger.LogWarning(ex,
                        "Restaurar TOTAL: falló el intento {Attempt}/{Retries} de la vuelta atrás.", attempt, retries);
                }

                if (attempt < retries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), CancellationToken.None);
                }
            }

            _logger.LogCritical(
                "Restaurar TOTAL: DOBLE FALLO — la vuelta atrás no se pudo completar tras {Retries} intentos. Motivo interno del último intento: {Error}",
                retries, lastError);
            return new DatabaseSwapRollbackResult(false, lastError);
        }
        catch (Exception ex)
        {
            // Red del PUERTO (bloqueante 1 de la re-review, atacado en el origen): la vuelta atrás NUNCA tira. Los
            // tramos que están fuera del try por intento (abrir la conexión de sondeo, cerrar la puerta de entrada)
            // son justamente los que podían tirar y hacer que el doble fallo no se declarara nunca.
            _logger.LogCritical(ex, "Restaurar TOTAL: excepción inesperada durante la vuelta atrás. Se declara doble fallo.");
            return new DatabaseSwapRollbackResult(false, ex.Message);
        }
        finally
        {
            await TryAllowConnectionsToLiveNameAsync(builder, liveDatabaseName);
        }
    }

    /// <summary>
    /// UN paso del intercambio, decidido por el ESTADO real de <c>pg_database</c> (nunca "el paso 3 de 5"):
    /// libera el nombre vivo estacionando la base original, y después le pone el nombre vivo a la base nueva.
    /// Devuelve true cuando el estado final ya está alcanzado, así un intento a medias lo termina el siguiente.
    /// </summary>
    private async Task<bool> ReconcileSwapStepAsync(
        NpgsqlConnectionStringBuilder builder,
        string liveDatabaseName,
        string newDatabaseName,
        string previousDatabaseName,
        CancellationToken ct)
    {
        // Las conexiones ociosas que el propio pool de Npgsql guardó para reusar bloquean el RENAME igual que
        // cualquier otra: hay que vaciarlo DESDE ADENTRO del proceso, en cada intento.
        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString(builder));
        await connection.OpenAsync(ct);

        await TerminateConnectionsToAsync(connection, liveDatabaseName, ct);
        await TerminateConnectionsToAsync(connection, newDatabaseName, ct);

        var liveExists = await DatabaseExistsAsync(connection, liveDatabaseName, ct);
        var previousExists = await DatabaseExistsAsync(connection, previousDatabaseName, ct);

        if (liveExists && !previousExists)
        {
            await RenameDatabaseAsync(connection, liveDatabaseName, previousDatabaseName, ct);
            liveExists = false;
        }

        if (!liveExists && await DatabaseExistsAsync(connection, newDatabaseName, ct))
        {
            await RenameDatabaseAsync(connection, newDatabaseName, liveDatabaseName, ct);
        }

        return await DatabaseExistsAsync(connection, liveDatabaseName, ct)
               && !await DatabaseExistsAsync(connection, newDatabaseName, ct);
    }

    /// <summary>
    /// UN paso de la vuelta atrás, también por estado: estaciona la base del intento fallido (se CONSERVA para
    /// diagnóstico) y devuelve el nombre vivo a la base original.
    /// </summary>
    private async Task<bool> ReconcileRollbackStepAsync(
        NpgsqlConnectionStringBuilder builder,
        string liveDatabaseName,
        string previousDatabaseName,
        string failedDatabaseName,
        CancellationToken ct)
    {
        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString(builder));
        await connection.OpenAsync(ct);

        await TerminateConnectionsToAsync(connection, liveDatabaseName, ct);
        await TerminateConnectionsToAsync(connection, previousDatabaseName, ct);

        if (await DatabaseExistsAsync(connection, liveDatabaseName, ct)
            && !await DatabaseExistsAsync(connection, failedDatabaseName, ct))
        {
            await RenameDatabaseAsync(connection, liveDatabaseName, failedDatabaseName, ct);
        }

        if (!await DatabaseExistsAsync(connection, liveDatabaseName, ct)
            && await DatabaseExistsAsync(connection, previousDatabaseName, ct))
        {
            await RenameDatabaseAsync(connection, previousDatabaseName, liveDatabaseName, ct);
        }

        return await DatabaseExistsAsync(connection, liveDatabaseName, ct)
               && !await DatabaseExistsAsync(connection, previousDatabaseName, ct);
    }

    /// <summary>
    /// Si el intercambio quedó a mitad de camino (la original estacionada y el nombre vivo libre), le devuelve el
    /// nombre vivo a la original. Best-effort: si esto tampoco sale, el caller ya va a pedir la vuelta atrás
    /// formal, que reconcilia el mismo estado con reintentos.
    /// </summary>
    private async Task TryUndoHalfDoneSwapAsync(
        NpgsqlConnectionStringBuilder builder, string liveDatabaseName, string previousDatabaseName)
    {
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString(builder));
            await connection.OpenAsync(CancellationToken.None);

            if (!await DatabaseExistsAsync(connection, liveDatabaseName, CancellationToken.None)
                && await DatabaseExistsAsync(connection, previousDatabaseName, CancellationToken.None))
            {
                await TerminateConnectionsToAsync(connection, previousDatabaseName, CancellationToken.None);
                await RenameDatabaseAsync(connection, previousDatabaseName, liveDatabaseName, CancellationToken.None);
                _logger.LogWarning(
                    "Restaurar TOTAL: el intercambio había quedado a mitad de camino y se deshizo — el nombre vivo volvió a la base original.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Restaurar TOTAL: no se pudo deshacer un intercambio a medias. La vuelta atrás formal lo reintenta.");
        }
    }

    public async Task<DatabasePrivilegeCheckResult> CheckDatabaseManagementPrivilegesAsync(CancellationToken ct)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new DatabasePrivilegeCheckResult(false, "No hay connection string configurada.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        try
        {
            await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString(builder));
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            // Postgres exige, para renombrar una base, ser DUEÑO de ella y además tener CREATEDB (o ser
            // superusuario). Por eso el chequeo mira las tres cosas — con solo rolcreatedb, el RENAME fallaría
            // recién DESPUÉS de haber pagado el pg_restore completo y el resguardo previo (condición C1).
            command.CommandText = """
                SELECT
                    current_setting('is_superuser') = 'on' AS is_superuser,
                    COALESCE((SELECT rolcreatedb FROM pg_roles WHERE rolname = current_user), false) AS can_create_db,
                    COALESCE((SELECT pg_get_userbyid(datdba) = current_user FROM pg_database WHERE datname = @db), false) AS is_owner;
                """;
            command.Parameters.AddWithValue("db", builder.Database!);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return new DatabasePrivilegeCheckResult(false, "No se pudo leer los privilegios del usuario de base de datos.");
            }

            var isSuperuser = reader.GetBoolean(0);
            var canCreateDatabase = reader.GetBoolean(1);
            var isOwnerOfLiveDatabase = reader.GetBoolean(2);

            if (isSuperuser || (canCreateDatabase && isOwnerOfLiveDatabase))
            {
                return new DatabasePrivilegeCheckResult(true, null);
            }

            return new DatabasePrivilegeCheckResult(false,
                $"Privilegios insuficientes: superusuario={isSuperuser}, puedeCrearBases={canCreateDatabase}, esDueño={isOwnerOfLiveDatabase}.");
        }
        catch (Exception ex)
        {
            // Fail-closed: si no se puede CONFIRMAR que alcanza, no se intenta.
            _logger.LogError(ex, "Restaurar TOTAL: no se pudieron verificar los privilegios de administración de bases.");
            return new DatabasePrivilegeCheckResult(false, ex.Message);
        }
    }

    public async Task CleanupLeftoverRestoreDatabasesAsync(CancellationToken ct)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var liveDatabaseName = builder.Database!;

        try
        {
            await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString(builder));
            await connection.OpenAsync(ct);

            var leftovers = new List<string>();
            await using (var query = connection.CreateCommand())
            {
                // Solo las sobras de ESTA obra, y nunca la base viva ni la sombra del modo prueba: los tres
                // prefijos se arman con el nombre de la base viva + un sufijo fijo con timestamp.
                //
                // ESCAPE (recomendación N3 de seguridad, re-review): en un LIKE de SQL, el guion bajo es un
                // COMODÍN de "cualquier carácter". Sin escaparlo, "travel_old_%" también matchearía "travelXoldY..."
                // — improbable pero real, y en una función que DROPEA bases no se juega con improbables.
                var live = EscapeLikeLiteral(liveDatabaseName);
                query.CommandText = """
                    SELECT datname FROM pg_database
                    WHERE datname LIKE @restorePattern ESCAPE '\'
                       OR datname LIKE @oldPattern ESCAPE '\'
                       OR datname LIKE @failedPattern ESCAPE '\';
                    """;
                query.Parameters.AddWithValue("restorePattern", $@"{live}\_restore\_%");
                query.Parameters.AddWithValue("oldPattern", $@"{live}\_old\_%");
                query.Parameters.AddWithValue("failedPattern", $@"{live}\_fallido\_%");

                await using var reader = await query.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    leftovers.Add(reader.GetString(0));
                }
            }

            foreach (var leftover in leftovers)
            {
                try
                {
                    await TerminateConnectionsToAsync(connection, leftover, ct);
                    await DropDatabaseIfExistsAsync(connection, leftover, ct);
                    _logger.LogWarning("Restaurar TOTAL: se dropeó una base sobrante de un intento anterior ({Base}).", leftover);
                }
                catch (Exception ex)
                {
                    // Best-effort POR BASE: una que no se puede dropear es basura en disco, nunca motivo para
                    // abortar la restauración que recién arranca.
                    _logger.LogWarning(ex, "Restaurar TOTAL: no se pudo dropear la base sobrante {Base}.", leftover);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restaurar TOTAL: no se pudo limpiar las bases sobrantes de intentos anteriores.");
        }
    }

    public async Task DropDatabaseAsync(string databaseName, CancellationToken ct)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(databaseName))
        {
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.Equals(databaseName, builder.Database, StringComparison.Ordinal))
        {
            // Candado de seguridad: este método NUNCA puede dropear la base viva, ni por un bug del caller.
            _logger.LogError("Restaurar TOTAL: se intentó dropear la base VIVA. Se ignora el pedido.");
            return;
        }

        try
        {
            await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString(builder));
            await connection.OpenAsync(ct);
            await TerminateConnectionsToAsync(connection, databaseName, ct);
            await DropDatabaseIfExistsAsync(connection, databaseName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restaurar TOTAL: no se pudo dropear la base {Base} (queda basura en disco, no hay pérdida de datos).", databaseName);
        }
    }

    /// <summary>
    /// Obra "Restaurar TOTAL" hardening (2026-07-28, hallazgo BLOQUEANTE B1 de seguridad): corre el
    /// <c>pg_restore</c> total con un timeout PROPIO, completamente independiente del <paramref name="ct"/>
    /// del pedido HTTP.
    ///
    /// <para><b>Por qué no alcanza con el patrón de <see cref="RunProcessAsync"/> (usado por el resto de este
    /// puerto)</b>: ese helper espera con el <c>ct</c> del CALLER. Si el pedido HTTP se cancela (el admin
    /// cierra la pestaña, el proxy corta la conexión — <c>nginx.conf</c> del repo define
    /// <c>proxy_read_timeout</c>/<c>proxy_send_timeout</c> propios en <c>location /api/admin/danger/</c>, pero
    /// el nginx del HOST en producción es un servicio APARTE, fuera de este repo, y necesita el mismo ajuste a
    /// mano — ver el runbook en <c>docs/db-operations.md</c>), el código dejaría de esperar y trataría la
    /// cancelación como "terminó y falló" — PERO el proceso <c>pg_restore</c> real sigue vivo en el servidor,
    /// reemplazando la base EN VIVO, mientras <c>SystemDataRestoreService</c> ya desactivó el modo
    /// mantenimiento creyendo que todo terminó. Acá el único límite de tiempo es
    /// <c>Wipe:PgRestoreTotalTimeoutMinutes</c> (propio, nunca heredado del pedido).</para>
    ///
    /// <para><b>Qué pasa si se agota el timeout</b>: se mata el proceso (<see cref="TryKillProcess"/>), pero el
    /// resultado es <see cref="TotalRestoreOutcome.UnknownMayStillBeRunning"/> — NO "falló" — porque no hay
    /// certeza absoluta de que Postgres ya completó el ROLLBACK automático de <c>--single-transaction</c> en
    /// el instante exacto en que se cortó la conexión del cliente. El caller (<c>SystemDataRestoreService</c>)
    /// tiene que dejar el sistema en mantenimiento ante este resultado.</para>
    ///
    /// <para><b>Minor hardening (punto 13, revisión funcional)</b>: <c>PGOPTIONS</c> fija <c>lock_timeout</c> y
    /// <c>statement_timeout</c> DEL LADO DE POSTGRES — si un <c>DROP TABLE</c> del <c>--clean</c> se queda
    /// esperando un lock que nunca se libera, Postgres aborta ESA sentencia con un error claro en vez de
    /// colgarse hasta que se agote nuestro timeout completo (igual acotado por ese timeout como red de
    /// seguridad final, pero esto da un fallo más rápido y más claro en el caso común).</para>
    /// </summary>
    /// <summary>
    /// Desenlace del proceso <c>pg_restore</c> largo. Es un tipo INTERNO del puerto: el contrato que ve la capa
    /// de aplicación es <see cref="NewDatabaseRestoreResult"/> (que además lleva el nombre de la base nueva).
    /// </summary>
    private sealed record PgRestoreProcessResult(TotalRestoreOutcome Outcome, bool Success, string? ErrorMessage);

    private async Task<PgRestoreProcessResult> RunPgRestoreTotalProcessAsync(string arguments, string? password, CancellationToken ct)
    {
        var timeoutMinutes = _configuration.GetValue<int?>("Wipe:PgRestoreTotalTimeoutMinutes") ?? DefaultPgRestoreTotalTimeoutMinutes;
        var lockTimeoutSeconds = _configuration.GetValue<int?>("Wipe:PgRestoreTotalLockTimeoutSeconds") ?? 30;

        var startInfo = new ProcessStartInfo
        {
            FileName = "pg_restore",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        if (password is not null)
        {
            startInfo.EnvironmentVariables["PGPASSWORD"] = password;
        }

        // PGOPTIONS: parametros de sesion de Postgres que pg_restore no expone como flag de linea de comandos
        // propio. lock_timeout acota cuanto espera CADA sentencia (ej. un DROP TABLE) por un lock antes de
        // abortar con un error claro; statement_timeout es la red de seguridad del lado de Postgres (acotada
        // un poco por debajo de nuestro propio timeout de proceso, para que Postgres aborte solo ANTES de que
        // tengamos que matar el proceso a la fuerza).
        var statementTimeoutMs = Math.Max(1, timeoutMinutes - 1) * 60_000;
        startInfo.EnvironmentVariables["PGOPTIONS"] =
            $"-c lock_timeout={lockTimeoutSeconds * 1000} -c statement_timeout={statementTimeoutMs}";

        using var process = new Process { StartInfo = startInfo };

        // Timeout COMPLETAMENTE INDEPENDIENTE del "ct" del pedido HTTP (ver el comentario XML de este metodo).
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));

        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(timeoutCts.Token);

            // Hallazgo menor #16 (revision de infra/seguridad, 2026-07-28, "el catch generico atrapa fallas de
            // stderr DESPUES de un restore exitoso y las reporta como falla"): a partir de esta linea, el
            // proceso YA TERMINO (WaitForExitAsync no tiro) - el desenlace es CIERTO (Completed) sin importar
            // lo que pase leyendo stdout/stderr de aca en mas. Antes, un fallo leyendo esos streams (poco
            // probable pero posible, ej. el proceso cerro el pipe de forma abrupta) caia en el catch generico
            // de mas abajo y se reportaba como "no se pudo ejecutar pg_restore" - enganioso, porque el proceso
            // SI corrio y termino (quizas hasta con exit code 0, restauracion exitosa de verdad). Por eso la
            // lectura de los streams va en SU PROPIO try/catch, separado del que decide el desenlace.
            var exitCode = process.ExitCode;
            string? stderr = null;
            try
            {
                stderr = await stderrTask;
                _ = await stdoutTask;
            }
            catch (Exception readEx)
            {
                _logger.LogWarning(readEx,
                    "Restaurar TOTAL: pg_restore termino (ExitCode={ExitCode}) pero fallo leyendo su stdout/stderr.",
                    exitCode);
            }

            if (exitCode != 0)
            {
                _logger.LogError(
                    "Restaurar TOTAL: pg_restore termino con codigo {ExitCode}. Stderr: {Stderr}",
                    exitCode, stderr);
                return new PgRestoreProcessResult(TotalRestoreOutcome.Completed, false, $"pg_restore exit code {exitCode}: {stderr}");
            }

            return new PgRestoreProcessResult(TotalRestoreOutcome.Completed, true, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKillProcess(process);
            _logger.LogCritical(
                "Restaurar TOTAL: pg_restore excedio el timeout propio de {Timeout} minutos. Se mato el proceso, " +
                "pero NO hay certeza de que la base haya terminado de revertir la transaccion en curso.",
                timeoutMinutes);
            return new PgRestoreProcessResult(
                TotalRestoreOutcome.UnknownMayStillBeRunning, false,
                $"pg_restore excedio el timeout de {timeoutMinutes} minutos y tuvo que ser terminado a la fuerza.");
        }
        catch (Exception ex)
        {
            // Cualquier otro fallo (el binario no existe, error de I/O al lanzar el proceso) pasa ANTES o
            // durante el arranque - si nunca llego a correr pg_restore de verdad, es seguro tratarlo como un
            // fallo CONOCIDO (nada toco la base).
            TryKillProcess(process);
            _logger.LogError(ex, "Restaurar TOTAL: fallo al ejecutar pg_restore.");
            return new PgRestoreProcessResult(TotalRestoreOutcome.Completed, false, $"No se pudo ejecutar pg_restore: {ex.Message}");
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort: si no se puede matar el proceso, el resultado ya reportado (UnknownMayStillBeRunning)
            // es igual de conservador — el caller NUNCA sale de mantenimiento por este camino.
        }
    }

    /// <summary>
    /// ADR-052 (D2), reescrito sobre el guard de compatibilidad de la obra anterior (hallazgo B7): lee el
    /// historial de migraciones del resguardo restaurando SOLO la tabla <c>__EFMigrationsHistory</c> (liviana: un
    /// par de columnas, decenas de filas) a la base sombra descartable, y lo compara contra la lista del
    /// ENSAMBLADO más el estado de la base viva.
    ///
    /// <para><b>Qué cambió respecto de la versión anterior</b>: antes exigía igualdad EXACTA contra el historial
    /// de la BASE VIVA, así que cada deploy con una migración dejaba inservibles todos los resguardos anteriores.
    /// Ahora la referencia es <c>Database.GetMigrations()</c> (el ensamblado, que es quien realmente aplica las
    /// migraciones) y un resguardo "subconjunto final" se acepta para restaurar + actualizar solo.</para>
    /// </summary>
    public async Task<SchemaCompatibilityResult> CheckSchemaCompatibilityAsync(string fileName, CancellationToken ct)
    {
        var fullPath = ResolveSafeBackupPath(fileName);
        if (fullPath is null || !File.Exists(fullPath))
        {
            return new SchemaCompatibilityResult(RestoreSchemaVerdict.CouldNotDetermine, "El archivo de backup no existe.");
        }

        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new SchemaCompatibilityResult(RestoreSchemaVerdict.CouldNotDetermine, "No hay connection string configurada.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var shadowDatabaseName = $"{builder.Database}_shadow";

        try
        {
            await RecreateEmptyDatabaseAsync(builder, shadowDatabaseName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restaurar TOTAL: no se pudo preparar la base sombra para el chequeo de compatibilidad de esquema.");
            return new SchemaCompatibilityResult(RestoreSchemaVerdict.CouldNotDetermine, $"No se pudo preparar la verificación de compatibilidad: {ex.Message}");
        }

        try
        {
            var restoreArgs = $"--no-owner --no-acl --table=__EFMigrationsHistory -h {builder.Host} -p {builder.Port} " +
                               $"-U {builder.Username} -d {shadowDatabaseName} \"{fullPath}\"";
            // Timeout propio (hallazgo B-N2(b), 2026-07-28): antes este chequeo no tenia ningun limite de
            // tiempo propio - solo dependia del "ct" del caller. Sostiene el candado de mantenimiento
            // (ExecuteTotalRestoreAsync ya activo el modo mantenimiento ANTES de llamar aca), asi que un
            // Postgres lento/colgado podia bloquear el sistema entero indefinidamente. A diferencia del
            // pg_restore TOTAL (que corre contra la base VIVA), este SOLO toca la base sombra descartable -
            // es seguro heredar la cancelacion del caller (linkeado, no independiente): si se cancela a mitad
            // de camino, como mucho queda una base sombra a medio poblar, que se borra igual en el finally.
            var schemaCheckTimeoutMinutes = _configuration.GetValue<int?>("Wipe:SchemaCheckTimeoutMinutes") ?? DefaultSchemaCheckTimeoutMinutes;
            using var schemaCheckTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            schemaCheckTimeoutCts.CancelAfter(TimeSpan.FromMinutes(schemaCheckTimeoutMinutes));
            var (success, _, errorMessage) = await RunProcessAsync("pg_restore", restoreArgs, schemaCheckTimeoutCts.Token, password: builder.Password);
            if (!success)
            {
                _logger.LogError(
                    "Restaurar TOTAL: no se pudo leer __EFMigrationsHistory del resguardo para el chequeo de compatibilidad. Motivo interno: {Error}",
                    errorMessage);
                return new SchemaCompatibilityResult(RestoreSchemaVerdict.CouldNotDetermine, "No se pudo verificar la versión del resguardo.");
            }

            var shadowConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = shadowDatabaseName }.ConnectionString;

            HashSet<string> dumpMigrations;
            bool liveHasPendingMigrations;
            try
            {
                dumpMigrations = await ReadMigrationIdsAsync(shadowConnectionString, ct);
                // La base VIVA se consulta SOLO para saber si está al día (no para comparar versiones): el
                // veredicto se calcula contra el ensamblado, ver el comentario de RestoreSchemaVerdictRules.
                liveHasPendingMigrations = (await _context.Database.GetPendingMigrationsAsync(ct)).Any();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Restaurar TOTAL: no se pudo leer el historial de migraciones para calcular el veredicto de versión.");
                return new SchemaCompatibilityResult(RestoreSchemaVerdict.CouldNotDetermine, "No se pudo verificar la versión del resguardo.");
            }

            var assemblyMigrations = _context.Database.GetMigrations().ToList();
            var verdict = RestoreSchemaVerdictRules.Evaluate(
                assemblyMigrations, dumpMigrations, liveHasPendingMigrations, out var toleratedOrphans);
            var missing = RestoreSchemaVerdictRules.CountMissingMigrations(assemblyMigrations, dumpMigrations);

            _logger.LogWarning(
                "Restaurar TOTAL: veredicto de versión = {Verdict}. Migraciones del resguardo={DumpCount}, del sistema={AssemblyCount}, faltantes={Missing}, base viva con pendientes={LivePending}.",
                verdict, dumpMigrations.Count, assemblyMigrations.Count, missing, liveHasPendingMigrations);

            if (toleratedOrphans.Count > 0)
            {
                // Log INTERNO (T-5): acá sí van los nombres, porque son la pista para limpiar el historial de la
                // base algún día. Al usuario nunca se le muestra ninguno de estos ids.
                _logger.LogWarning(
                    "Restaurar TOTAL: el resguardo trae {OrphanCount} fila(s) de historial que el sistema no conoce pero son anteriores a su última migración; se toleran y no bloquean. Ids internos: {OrphanIds}.",
                    toleratedOrphans.Count, string.Join(", ", toleratedOrphans));
            }

            // Argumentos nombrados a propósito: los dos últimos son int y uno al lado del otro; sin nombres, un
            // intercambio futuro compilaría igual y ensuciaría la auditoría en silencio.
            return new SchemaCompatibilityResult(
                verdict,
                null,
                MissingMigrationsCount: missing,
                ToleratedOrphanMigrationsCount: toleratedOrphans.Count);
        }
        finally
        {
            await DropShadowDatabaseAsync(ct);
        }
    }

    private static async Task<HashSet<string>> ReadMigrationIdsAsync(string connectionString, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "MigrationId" FROM "__EFMigrationsHistory";""";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public async Task DropShadowDatabaseAsync(CancellationToken ct)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var shadowDatabaseName = $"{builder.Database}_shadow";

        try
        {
            await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString(builder));
            await connection.OpenAsync(ct);
            await TerminateConnectionsToAsync(connection, shadowDatabaseName, ct);
            await DropDatabaseIfExistsAsync(connection, shadowDatabaseName, ct);
        }
        catch (Exception ex)
        {
            // Best-effort a proposito (ver el comentario XML de la interfaz): si esto falla, la base sombra
            // queda viva un rato mas (basura, no perdida de datos reales) - nunca debe tapar el resultado ya
            // calculado del modo prueba.
            _logger.LogWarning(ex,
                "Restaurar (modo prueba): no se pudo borrar la base sombra {ShadowDb} despues de calcular los conteos.",
                shadowDatabaseName);
        }
    }

    /// <summary>
    /// Termina conexiones activas, borra (si existe) y vuelve a crear la base sombra. Estos comandos NO
    /// pueden correr dentro de la base que estan tocando (hay que estar conectado a otra, ac usamos "postgres",
    /// la base de mantenimiento que siempre existe) ni dentro de una transaccion — por eso se usa un
    /// <see cref="NpgsqlConnection"/> directo, fuera de EF.
    /// </summary>
    private static async Task RecreateEmptyDatabaseAsync(NpgsqlConnectionStringBuilder primaryBuilder, string shadowDatabaseName, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString(primaryBuilder));
        await connection.OpenAsync(ct);

        await TerminateConnectionsToAsync(connection, shadowDatabaseName, ct);
        await DropDatabaseIfExistsAsync(connection, shadowDatabaseName, ct);

        await using var create = connection.CreateCommand();
        // El nombre de la base sombra lo armamos nosotros ("{db}_shadow", nunca viene del usuario), asi que
        // interpolarlo en el DDL es seguro — CREATE DATABASE no admite parametros bindeados.
        create.CommandText = $"CREATE DATABASE \"{shadowDatabaseName}\";";
        await create.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// ADR-052 (D1.1): crea la base NUEVA donde se va a restaurar el resguardo. A diferencia de
    /// <see cref="RecreateEmptyDatabaseAsync"/> (que dropea y recrea la sombra, que es SIEMPRE la misma), acá el
    /// nombre lleva timestamp y no debería existir: si existiera, es una sobra que la limpieza no alcanzó a
    /// borrar, y se dropea antes para no restaurar sobre datos ajenos.
    /// </summary>
    private static async Task CreateEmptyDatabaseAsync(
        NpgsqlConnectionStringBuilder primaryBuilder, string databaseName, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString(primaryBuilder));
        await connection.OpenAsync(ct);

        if (await DatabaseExistsAsync(connection, databaseName, ct))
        {
            await TerminateConnectionsToAsync(connection, databaseName, ct);
            await DropDatabaseIfExistsAsync(connection, databaseName, ct);
        }

        await using var create = connection.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{EnsureSafeDatabaseIdentifier(databaseName)}\";";
        await create.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Nombres de las bases que administra esta obra: <c>&lt;base viva&gt;_&lt;rol&gt;_&lt;yyyyMMddHHmmss&gt;</c>.
    /// El timestamp hace que dos intentos nunca choquen y que la limpieza pueda reconocerlas por prefijo.
    /// </summary>
    private static string BuildTimestampedDatabaseName(string liveDatabaseName, string role) =>
        $"{liveDatabaseName}_{role}_{DateTime.UtcNow:yyyyMMddHHmmss}";

    private static async Task<bool> DatabaseExistsAsync(NpgsqlConnection connection, string databaseName, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @db);";
        command.Parameters.AddWithValue("db", databaseName);
        var result = await command.ExecuteScalarAsync(ct);
        return result is bool exists && exists;
    }

    private static async Task RenameDatabaseAsync(
        NpgsqlConnection connection, string fromName, string toName, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER DATABASE \"{EnsureSafeDatabaseIdentifier(fromName)}\" RENAME TO \"{EnsureSafeDatabaseIdentifier(toName)}\";";
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Prende o apaga la puerta de entrada de una base. Apagarla es lo que hace posible el <c>RENAME</c> con un
    /// worker de Hangfire reconectando solo; prenderla de nuevo es la INVARIANTE crítica de esta obra.
    /// </summary>
    private static async Task SetAllowConnectionsAsync(
        NpgsqlConnectionStringBuilder primaryBuilder, string databaseName, bool allow, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString(primaryBuilder));
        await connection.OpenAsync(ct);

        if (!await DatabaseExistsAsync(connection, databaseName, ct))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER DATABASE \"{EnsureSafeDatabaseIdentifier(databaseName)}\" WITH ALLOW_CONNECTIONS {(allow ? "true" : "false")};";
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// El <c>finally</c> obligatorio del ADR (D1.4): deja SIEMPRE con <c>ALLOW_CONNECTIONS true</c> a la base que
    /// tenga el NOMBRE VIVO, sin importar cómo salió el intercambio. Ojo con la trampa: el flag es una propiedad
    /// de la BASE, no del nombre — viaja con ella en cada <c>RENAME</c>, así que hay que prenderlo sobre el
    /// nombre vivo DESPUÉS de los renombres, no antes.
    /// </summary>
    private async Task TryAllowConnectionsToLiveNameAsync(NpgsqlConnectionStringBuilder builder, string liveDatabaseName)
    {
        try
        {
            await SetAllowConnectionsAsync(builder, liveDatabaseName, allow: true, CancellationToken.None);
            NpgsqlConnection.ClearAllPools();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "Restaurar TOTAL: NO se pudo dejar la base viva aceptando conexiones. El sistema puede quedar inaccesible " +
                "con los datos intactos: hay que ejecutar a mano el primer comando de rescate del runbook " +
                "(docs/db-operations.md, sección de salida de emergencia).");
        }
    }

    /// <summary>
    /// Defensa en profundidad para los identificadores que se interpolan en DDL (<c>CREATE</c>/<c>DROP</c>/
    /// <c>ALTER DATABASE</c> no aceptan parámetros bindeados). Todos estos nombres los arma ESTE archivo, nunca
    /// vienen del usuario; igual se valida el juego de caracteres para que un bug futuro no se convierta en una
    /// inyección de SQL.
    /// </summary>
    private static string EnsureSafeDatabaseIdentifier(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName)
            || databaseName.Length > 63
            || !databaseName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
        {
            throw new InvalidOperationException("Nombre de base de datos no admitido para una operación administrativa.");
        }

        return databaseName;
    }

    /// <summary>
    /// Escapa los comodines de un LIKE (<c>_</c> y <c>%</c>) para que el texto se compare LITERAL. Va de la mano
    /// del <c>ESCAPE '\'</c> de la consulta (ver <see cref="CleanupLeftoverRestoreDatabasesAsync"/>).
    /// </summary>
    private static string EscapeLikeLiteral(string value) =>
        value.Replace(@"\", @"\\").Replace("_", @"\_").Replace("%", @"\%");

    private static string BuildMaintenanceConnectionString(NpgsqlConnectionStringBuilder primaryBuilder) =>
        new NpgsqlConnectionStringBuilder(primaryBuilder.ConnectionString) { Database = "postgres" }.ConnectionString;

    private static async Task TerminateConnectionsToAsync(NpgsqlConnection connection, string databaseName, CancellationToken ct)
    {
        await using var terminate = connection.CreateCommand();
        terminate.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db;";
        terminate.Parameters.AddWithValue("db", databaseName);
        await terminate.ExecuteNonQueryAsync(ct);
    }

    private static async Task DropDatabaseIfExistsAsync(NpgsqlConnection connection, string databaseName, CancellationToken ct)
    {
        await using var drop = connection.CreateCommand();
        // DROP DATABASE no admite parametros bindeados, asi que el nombre va interpolado. Recomendacion N2 de
        // seguridad (re-review): pasa por la MISMA validacion de identificador que el resto del DDL de esta clase —
        // aca tambien llegan nombres LEIDOS de pg_database (la limpieza de sobras), no solo los que armamos nosotros.
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{EnsureSafeDatabaseIdentifier(databaseName)}\";";
        await drop.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> IsTableEmptyAsync(string connectionString, string tableName, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // tableName viene de la lista blanca validada en SystemDataRestoreService (WipeGroups.ConfiguracionTables),
        // nunca directo del usuario - interpolarlo en el identificador es seguro.
        command.CommandText = $"SELECT EXISTS (SELECT 1 FROM \"{tableName}\" LIMIT 1);";
        var result = await command.ExecuteScalarAsync(ct);
        var hasRows = result is bool b && b;
        return !hasRows;
    }

    /// <summary>
    /// Path traversal: el nombre de archivo NUNCA puede contener separadores de carpeta ni ".." — se valida
    /// que <see cref="Path.GetFileName"/> devuelva el mismo string (ninguna parte de carpeta), que termine en
    /// ".dump" (mismo filtro que <see cref="ListBackupsAsync"/> usa para listar — consistencia entre listar,
    /// verificar y restaurar, hallazgo menor de seguridad), y que la ruta final resuelta siga estando DENTRO
    /// de <see cref="BackupDirectory"/>.
    ///
    /// <para><b>Separador final en la comparación de directorio</b> (hallazgo menor de seguridad): comparar
    /// solo con <c>StartsWith(directory)</c> sin el separador final dejaría pasar, por ejemplo, un
    /// <c>directory</c> "/backups/wipe" contra una ruta resuelta "/backups/wipe-otra-cosa/archivo.dump" (el
    /// prefijo de texto matchea, pero NO es la misma carpeta). Se agrega el separador a la base de comparación
    /// para evitar ese falso positivo.</para>
    /// </summary>
    private string? ResolveSafeBackupPath(string fileName)
    {
        // Recomendación N1 de seguridad (re-review): LISTA BLANCA compartida con el servicio
        // (<see cref="SafeBackupFileNameRules"/>). Antes alcanzaba con "sin carpetas y termina en .dump", así que un
        // nombre con una comilla doble adentro podía cerrar el entrecomillado del argumento de pg_restore y meter
        // flags propios en el comando.
        if (!SafeBackupFileNameRules.IsSafe(fileName))
        {
            return null;
        }

        var directory = Path.GetFullPath(BackupDirectory);
        var directoryWithSeparator = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));

        if (!fullPath.StartsWith(directoryWithSeparator, StringComparison.Ordinal))
        {
            return null;
        }

        return fullPath;
    }

    private async Task<(bool Success, string? Stdout, string? ErrorMessage)> RunProcessAsync(
        string fileName, string arguments, CancellationToken ct, string? password = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        if (password is not null)
        {
            // PGPASSWORD via variable de entorno del proceso (NUNCA en el argumento de linea de comandos):
            // un argumento queda visible en `ps`/logs del sistema operativo, una variable de entorno del
            // proceso hijo no. Mismo patron que PgDumpAndMinioWipeBackupPort.
            startInfo.EnvironmentVariables["PGPASSWORD"] = password;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogError(
                    "Restaurar: {FileName} termino con codigo {ExitCode}. Stderr: {Stderr}",
                    fileName, process.ExitCode, stderr);
                return (false, stdout, $"{fileName} exit code {process.ExitCode}: {stderr}");
            }

            return (true, stdout, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restaurar: fallo al ejecutar {FileName}.", fileName);
            return (false, null, $"No se pudo ejecutar {fileName}: {ex.Message}");
        }
    }
}
