using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Obra "Restaurar desde la app" (2026-07-27, Parte B firmada): implementación REAL de
/// <see cref="IDatabaseRestorePort"/>. Corre <c>pg_restore</c> como proceso externo (mismo binario
/// <c>postgresql-client-16</c> que ya instala el Dockerfile para el backup, ver
/// <c>PgDumpAndMinioWipeBackupPort</c>) y administra la base sombra con un <see cref="NpgsqlConnection"/>
/// directo (no via EF: crear/borrar una base de datos completa no es algo que <c>AppDbContext</c> sepa hacer,
/// y además <c>CREATE DATABASE</c>/<c>DROP DATABASE</c> no pueden correr dentro de una transacción).
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

    private readonly IConfiguration _configuration;
    private readonly ILogger<PgDatabaseRestorePort> _logger;

    public PgDatabaseRestorePort(IConfiguration configuration, ILogger<PgDatabaseRestorePort> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string BackupDirectory => _configuration["Wipe:BackupDirectory"] ?? "/backups/wipe";

    public Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync(CancellationToken ct)
    {
        var directory = BackupDirectory;
        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<BackupFileInfo>>(Array.Empty<BackupFileInfo>());
        }

        var files = Directory.GetFiles(directory, "*.dump")
            .Select(path => new FileInfo(path))
            .Select(info => new BackupFileInfo(info.Name, info.LastWriteTimeUtc, info.Length))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToList();

        return Task.FromResult<IReadOnlyList<BackupFileInfo>>(files);
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

    public async Task<TotalRestoreResult> RestoreTotalAsync(string fileName, CancellationToken ct)
    {
        var fullPath = ResolveSafeBackupPath(fileName);
        if (fullPath is null || !File.Exists(fullPath))
        {
            return new TotalRestoreResult(TotalRestoreOutcome.Completed, false, "El archivo de backup no existe.");
        }

        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new TotalRestoreResult(TotalRestoreOutcome.Completed, false, "No hay connection string configurada.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        // Paso "c" del plan firmado: cortar TODO lo que pueda tener un lock sobre la base viva ANTES de
        // restaurar. ClearAllPools vacia, DESDE ADENTRO del proceso de la API, las conexiones ociosas que el
        // propio pool de Npgsql tenia guardadas para reusar. pg_terminate_backend, DESDE AFUERA (via SQL
        // contra la base de mantenimiento "postgres", nunca contra la base viva misma), mata cualquier
        // conexion que en ese instante siguiera activa (un pedido que ya estaba en vuelo cuando se activo el
        // modo mantenimiento, por ejemplo). Sin esto, "pg_restore --clean" se queda esperando para siempre el
        // lock de un DROP TABLE contra una tabla que otra conexion todavia tiene abierta. Esta parte SI usa el
        // "ct" del pedido (son operaciones rapidas de metadata, no el pg_restore largo de abajo) — si se
        // cancela antes de llegar a lanzar pg_restore, nada riesgoso paso todavia.
        NpgsqlConnection.ClearAllPools();

        try
        {
            await using var maintenanceConnection = new NpgsqlConnection(BuildMaintenanceConnectionString(builder));
            await maintenanceConnection.OpenAsync(ct);
            await TerminateConnectionsToAsync(maintenanceConnection, builder.Database!, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restaurar (modo total): no se pudieron cortar las conexiones activas antes de restaurar.");
            return new TotalRestoreResult(TotalRestoreOutcome.Completed, false, $"No se pudieron cortar las conexiones activas: {ex.Message}");
        }

        // Paso "d": pg_restore reemplaza TODO el contenido de la base viva por el del dump, dentro de UNA sola
        // transaccion de Postgres (--single-transaction). --clean --if-exists dropea cada objeto del dump
        // antes de recrearlo (si no existiera en la base viva, "--if-exists" evita que eso sea un error). Si
        // algo falla a mitad de camino (una fila invalida, se corta la conexion, lo que sea), Postgres hace
        // ROLLBACK automatico de TODO: la base queda EXACTAMENTE como estaba antes de este intento, como si
        // nunca se hubiera llamado a este metodo.
        //
        // --no-owner (hallazgo menor #4, revision de infra 2026-07-28): un dump generado con OTRO rol de
        // Postgres (ej. un backup viejo tomado con un usuario admin distinto al que usa la app) trae ordenes
        // "ALTER ... OWNER TO <ese-rol-viejo>" - si ese rol no existe en la base viva, esas ordenes fallan, y
        // dentro de --single-transaction UN SOLO fallo aborta la transaccion COMPLETA (toda la restauracion,
        // no solo esa orden). --no-owner le dice a pg_restore "no restaures el dueño de los objetos, dejalos
        // con el usuario de la conexion actual" - evita ese fallo entero. El modo prueba (RestoreToShadowDatabaseAsync)
        // ya lo usaba; a este metodo se lo agrega recien ahora.
        var restoreArgs = $"--no-owner --clean --if-exists --single-transaction -h {builder.Host} -p {builder.Port} " +
                           $"-U {builder.Username} -d {builder.Database} \"{fullPath}\"";
        return await RunPgRestoreTotalProcessAsync(restoreArgs, builder.Password, ct);
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
    private async Task<TotalRestoreResult> RunPgRestoreTotalProcessAsync(string arguments, string? password, CancellationToken ct)
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
                return new TotalRestoreResult(TotalRestoreOutcome.Completed, false, $"pg_restore exit code {exitCode}: {stderr}");
            }

            return new TotalRestoreResult(TotalRestoreOutcome.Completed, true, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKillProcess(process);
            _logger.LogCritical(
                "Restaurar TOTAL: pg_restore excedio el timeout propio de {Timeout} minutos. Se mato el proceso, " +
                "pero NO hay certeza de que la base haya terminado de revertir la transaccion en curso.",
                timeoutMinutes);
            return new TotalRestoreResult(
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
            return new TotalRestoreResult(TotalRestoreOutcome.Completed, false, $"No se pudo ejecutar pg_restore: {ex.Message}");
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
    /// Obra "Restaurar TOTAL" hardening (2026-07-28, hallazgo B7 de seguridad, "guard de compatibilidad de
    /// esquema"): compara el conjunto de migraciones EF ya aplicadas en el backup contra las de la base viva.
    /// Restaura SOLO la tabla <c>__EFMigrationsHistory</c> (liviana: un par de columnas, decenas de filas) a
    /// la base sombra descartable — mucho más barato que restaurar el backup completo solo para leer una
    /// tabla chica.
    /// </summary>
    public async Task<SchemaCompatibilityResult> CheckSchemaCompatibilityAsync(string fileName, CancellationToken ct)
    {
        var fullPath = ResolveSafeBackupPath(fileName);
        if (fullPath is null || !File.Exists(fullPath))
        {
            return new SchemaCompatibilityResult(false, "El archivo de backup no existe.");
        }

        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new SchemaCompatibilityResult(false, "No hay connection string configurada.");
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
            return new SchemaCompatibilityResult(false, $"No se pudo preparar la verificación de compatibilidad: {ex.Message}");
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
                return new SchemaCompatibilityResult(false, "No se pudo verificar la versión del resguardo.");
            }

            var shadowConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = shadowDatabaseName }.ConnectionString;

            HashSet<string> dumpMigrations;
            HashSet<string> liveMigrations;
            try
            {
                dumpMigrations = await ReadMigrationIdsAsync(shadowConnectionString, ct);
                liveMigrations = await ReadMigrationIdsAsync(connectionString, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Restaurar TOTAL: no se pudo leer __EFMigrationsHistory para comparar versiones de esquema.");
                return new SchemaCompatibilityResult(false, "No se pudo verificar la versión del resguardo.");
            }

            if (dumpMigrations.Count == 0)
            {
                _logger.LogError(
                    "Restaurar TOTAL: el resguardo no tiene registros en __EFMigrationsHistory (posible version muy anterior o dump incompleto).");
                return new SchemaCompatibilityResult(false, "El resguardo no tiene información de versión de esquema.");
            }

            if (!dumpMigrations.SetEquals(liveMigrations))
            {
                _logger.LogError(
                    "Restaurar TOTAL: version de esquema incompatible. Migraciones del dump={DumpCount}, migraciones actuales={LiveCount}.",
                    dumpMigrations.Count, liveMigrations.Count);
                return new SchemaCompatibilityResult(false, "La versión de esquema del resguardo no coincide con la versión actual.");
            }

            return new SchemaCompatibilityResult(true, null);
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
        // El nombre de la base sombra lo armamos nosotros ("{db}_shadow", nunca viene del usuario), asi que
        // interpolarlo en el DDL es seguro — DROP DATABASE no admite parametros bindeados.
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\";";
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
        if (string.IsNullOrWhiteSpace(fileName)
            || Path.GetFileName(fileName) != fileName
            || !fileName.EndsWith(".dump", StringComparison.Ordinal))
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
