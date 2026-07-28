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
