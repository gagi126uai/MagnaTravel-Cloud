using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using Npgsql;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Obra "Empezar de cero" (2026-07-27): implementación REAL del backup obligatorio previo al borrado masivo.
/// Hace DOS cosas, en orden, y aborta (sin tocar nada) si cualquiera de las dos falla:
///
/// <list type="number">
///   <item><b>Postgres</b>: corre <c>pg_dump -Fc</c> (mismo formato que el backup automático diario del
///   sidecar <c>postgres-backup</c> de <c>docker-compose.yml</c>) contra <c>Host=db</c>, escribiendo el
///   archivo en el volumen montado <c>/backups/wipe</c> (ver Dockerfile: requiere <c>postgresql-client-16</c>
///   instalado en la imagen de la API). Valida el resultado en DOS pasos: tamaño &gt; 0 Y <c>pg_restore --list</c>
///   (lee el TOC del archivo sin necesitar conexión — detecta un dump truncado/corrupto que por casualidad
///   pesa más de 0 bytes).</item>
///   <item><b>MinIO</b>: fix bloqueante #1 (revisión 2026-07-27) — SOLO COPIA (nunca mueve/borra) todos los
///   objetos del bucket a un prefijo <c>wipe-backup-&lt;timestamp&gt;/</c> del MISMO bucket, verificando cada
///   copia con <c>StatObject</c> antes de darla por buena. Los ORIGINALES se borran recién DESPUÉS de que la
///   transacción de Postgres hizo commit (ver <see cref="RemoveOriginalObjectsAsync"/>, llamado desde
///   <c>SystemDataWipeService</c>) — si la transacción falla, los objetos originales de MinIO quedan
///   INTACTOS, así que "no se borró nada" sigue siendo literalmente cierto.</item>
/// </list>
///
/// <para><b>Por que un puerto aparte (no vive dentro de <c>SystemDataWipeService</c>)</b>: permite testear la
/// orquestación del wipe (frase/contraseña/candado fiscal/transacción) inyectando un fake que no necesita
/// Postgres/MinIO reales corriendo. Este puerto real se prueba por construcción (correrlo de verdad en
/// integración/producción es la única forma honesta de validar un <c>Process.Start</c> + una llamada de red a
/// MinIO).</para>
/// </summary>
public class PgDumpAndMinioWipeBackupPort : IWipeBackupPort
{
    /// <summary>
    /// Prefijos que identifican objetos que YA son backup de una operación anterior — no se re-copian (evita
    /// anidar backups-de-backups en operaciones sucesivas). <c>wipe-backup-</c> es de "Empezar de cero";
    /// <c>pre-restore-backup-</c> (2026-07-28, hallazgo menor de la revisión funcional: "el resguardo previo
    /// de un restore total era indistinguible de un wipe en la lista") es el resguardo automático que
    /// <c>SystemDataRestoreService</c> genera del estado ACTUAL antes de una restauración TOTAL.
    /// </summary>
    private static readonly string[] KnownBackupPrefixMarkers = { "wipe-backup-", "pre-restore-backup-" };

    /// <summary>
    /// Defaults expuestos <c>internal</c> (con <c>InternalsVisibleTo("TravelApi.Tests")</c> ya configurado)
    /// para que el test guardián de la invariante de timeouts (<c>RestoreTotalTimeoutConfigurationTests</c>,
    /// hallazgo B-N2(d)) los derive de ACÁ en vez de mantener números duplicados que se puedan desincronizar
    /// en silencio si algún día se cambia el default acá sin actualizar el test.
    /// </summary>
    internal const int DefaultPgDumpTimeoutMinutes = 10;

    /// <summary>Ver <see cref="DefaultPgDumpTimeoutMinutes"/>. Hallazgo B-N2(b): la copia de MinIO no tenía NINGÚN timeout propio antes de esta obra.</summary>
    internal const int DefaultMinioCopyTimeoutMinutes = 5;

    private readonly IConfiguration _configuration;
    private readonly IMinioClient _minioClient;
    private readonly ILogger<PgDumpAndMinioWipeBackupPort> _logger;

    public PgDumpAndMinioWipeBackupPort(
        IConfiguration configuration,
        IMinioClient minioClient,
        ILogger<PgDumpAndMinioWipeBackupPort> logger)
    {
        _configuration = configuration;
        _minioClient = minioClient;
        _logger = logger;
    }

    public async Task<WipeBackupResult> CreateBackupAsync(string backupFileName, string minioPrefix, CancellationToken ct)
    {
        var pgDumpResult = await RunPgDumpAsync(backupFileName, ct);
        if (!pgDumpResult.Success)
        {
            return pgDumpResult;
        }

        var minioResult = await CopyMinioObjectsAsync(minioPrefix, ct);
        if (!minioResult.Success)
        {
            return minioResult;
        }

        return new WipeBackupResult(true, pgDumpResult.BackupFileName, minioPrefix, ErrorMessage: null, minioResult.CopiedObjectKeys);
    }

    public async Task RemoveOriginalObjectsAsync(WipeBackupResult backupResult, CancellationToken ct)
    {
        if (backupResult.CopiedObjectKeys is null || backupResult.CopiedObjectKeys.Count == 0)
        {
            return;
        }

        var bucket = _configuration["Minio:BucketName"] ?? _configuration["MINIO_BUCKET_NAME"] ?? "reservations";

        foreach (var key in backupResult.CopiedObjectKeys)
        {
            try
            {
                var removeArgs = new RemoveObjectArgs().WithBucket(bucket).WithObject(key);
                await _minioClient.RemoveObjectAsync(removeArgs, ct);
            }
            catch (Exception ex)
            {
                // Best-effort A PROPOSITO: el wipe YA fue exitoso en este punto (Postgres + backup de MinIO
                // existen). Un objeto que no se pudo borrar es basura inofensiva (queda vivo en el bucket,
                // ademas de su copia de backup) - NUNCA una perdida de dato. Se loguea y se sigue con el resto.
                _logger.LogWarning(ex,
                    "Empezar de cero: no se pudo borrar el objeto original {Key} de MinIO tras el commit (su copia de backup en {Prefix} ya existe de todos modos).",
                    key, backupResult.MinioPrefix);
            }
        }
    }

    private async Task<WipeBackupResult> RunPgDumpAsync(string backupFileName, CancellationToken ct)
    {
        var backupDirectory = _configuration["Wipe:BackupDirectory"] ?? "/backups/wipe";
        var fullPath = Path.Combine(backupDirectory, backupFileName);

        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new WipeBackupResult(false, null, null, "No hay connection string configurada (DefaultConnection).");
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Empezar de cero: no se pudo parsear la connection string para el backup de Postgres.");
            return new WipeBackupResult(false, null, null, "Connection string invalida.");
        }

        try
        {
            Directory.CreateDirectory(backupDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Empezar de cero: no se pudo crear/acceder al directorio de backup {Directory}.", backupDirectory);
            return new WipeBackupResult(false, null, null, $"No se pudo acceder al directorio de backup: {ex.Message}");
        }

        var timeoutMinutes = _configuration.GetValue<int?>("Wipe:PgDumpTimeoutMinutes") ?? DefaultPgDumpTimeoutMinutes;

        var startInfo = new ProcessStartInfo
        {
            FileName = "pg_dump",
            // -Fc: formato custom de pg_dump (comprimido, restaurable con pg_restore --clean --if-exists,
            // mismo formato que usa el sidecar postgres-backup del docker-compose).
            Arguments = $"-Fc -h {builder.Host} -p {builder.Port} -U {builder.Username} -d {builder.Database} -f \"{fullPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        // PGPASSWORD via variable de entorno del proceso (NUNCA en el argumento de linea de comandos): un
        // argumento queda visible en `ps`/logs del sistema operativo, una variable de entorno del proceso hijo no.
        startInfo.EnvironmentVariables["PGPASSWORD"] = builder.Password;

        using (var process = new Process { StartInfo = startInfo })
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

            try
            {
                process.Start();
                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(timeoutCts.Token);
                var stderr = await stderrTask;

                if (process.ExitCode != 0)
                {
                    _logger.LogError(
                        "Empezar de cero: pg_dump termino con codigo {ExitCode}. Stderr: {Stderr}",
                        process.ExitCode, stderr);
                    return new WipeBackupResult(false, null, null, $"pg_dump exit code {process.ExitCode}: {stderr}");
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                TryKillProcess(process);
                _logger.LogError("Empezar de cero: pg_dump excedio el timeout de {Timeout} minutos.", timeoutMinutes);
                return new WipeBackupResult(false, null, null, $"pg_dump excedio el timeout de {timeoutMinutes} minutos.");
            }
            catch (Exception ex)
            {
                TryKillProcess(process);
                _logger.LogError(ex, "Empezar de cero: fallo al ejecutar pg_dump.");
                return new WipeBackupResult(false, null, null, $"No se pudo ejecutar pg_dump: {ex.Message}");
            }
        }

        // Verificacion 1: el archivo tiene que existir y no estar vacio. Un pg_dump que "termino OK" pero
        // escribio un archivo de 0 bytes (disco lleno, permisos) NO es un backup valido.
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            _logger.LogError(
                "Empezar de cero: pg_dump termino sin error pero el archivo de backup no existe o esta vacio ({Path}).",
                fullPath);
            return new WipeBackupResult(false, null, null, "El archivo de backup quedo vacio o no se genero.");
        }

        // Verificacion 2 (fix menor #6, revision 2026-07-27): "pg_restore --list" lee el indice (TOC) del
        // dump sin necesitar conexion a ningun servidor. Un archivo truncado/corrupto puede pesar > 0 bytes
        // igual (ej. el proceso murio a mitad de escritura) - esto lo detecta ANTES de confiar en el backup.
        var listResult = await RunPgRestoreListAsync(fullPath, ct);
        if (!listResult.Success)
        {
            return listResult;
        }

        return new WipeBackupResult(true, backupFileName, null, null);
    }

    /// <summary>
    /// Fix menor #6: valida el dump generado corriendo <c>pg_restore --list</c> sobre el archivo. Si el
    /// archivo esta corrupto o incompleto, este comando falla (exit code distinto de 0 o TOC vacio) — no
    /// requiere conexion a Postgres, solo lee el archivo local.
    /// </summary>
    private async Task<WipeBackupResult> RunPgRestoreListAsync(string fullPath, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pg_restore",
            Arguments = $"--list \"{fullPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                _logger.LogError(
                    "Empezar de cero: pg_restore --list rechazo el dump generado (posible corrupcion/truncamiento). ExitCode={ExitCode} Stderr={Stderr}",
                    process.ExitCode, stderr);
                return new WipeBackupResult(false, null, null, $"pg_restore --list fallo (exit {process.ExitCode}): {stderr}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Empezar de cero: fallo ejecutando pg_restore --list sobre el dump generado.");
            return new WipeBackupResult(false, null, null, $"No se pudo validar el dump con pg_restore --list: {ex.Message}");
        }

        return new WipeBackupResult(true, null, null, null);
    }

    /// <summary>
    /// Obra "Restaurar TOTAL" hardening (2026-07-28, hallazgo B5 de seguridad, "los archivos no vuelven"):
    /// repone en el bucket VIVO los objetos que estén bajo <paramref name="minioPrefix"/>, best-effort por
    /// objeto (ver el comentario XML de <see cref="IWipeBackupPort.RestoreObjectsFromBackupPrefixAsync"/>).
    /// </summary>
    public async Task<int> RestoreObjectsFromBackupPrefixAsync(string minioPrefix, CancellationToken ct)
    {
        var bucket = _configuration["Minio:BucketName"] ?? _configuration["MINIO_BUCKET_NAME"] ?? "reservations";

        List<string> keysToRestore;
        try
        {
            var listArgs = new ListObjectsArgs().WithBucket(bucket).WithPrefix(minioPrefix).WithRecursive(true);
            keysToRestore = new List<string>();
            await foreach (var item in _minioClient.ListObjectsEnumAsync(listArgs, ct))
            {
                if (!item.IsDir)
                {
                    keysToRestore.Add(item.Key);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restaurar TOTAL: fallo listando los objetos del backup {Prefix} para reponerlos.", minioPrefix);
            return 0;
        }

        var restoredCount = 0;
        foreach (var backupKey in keysToRestore)
        {
            if (backupKey.Length <= minioPrefix.Length)
            {
                continue;
            }

            var originalKey = backupKey[minioPrefix.Length..];

            try
            {
                var copySource = new CopySourceObjectArgs().WithBucket(bucket).WithObject(backupKey);
                var copyArgs = new CopyObjectArgs().WithBucket(bucket).WithObject(originalKey).WithCopyObjectSource(copySource);
                await _minioClient.CopyObjectAsync(copyArgs, ct);

                var statArgs = new StatObjectArgs().WithBucket(bucket).WithObject(originalKey);
                await _minioClient.StatObjectAsync(statArgs, ct);

                restoredCount++;
            }
            catch (Exception ex)
            {
                // Best-effort A PROPOSITO: un archivo que no se pudo reponer es una perdida ACOTADA a ese
                // adjunto puntual, nunca motivo para abortar el resto de la reposicion ni para revertir la
                // restauracion de la base, que ya fue exitosa en este punto.
                _logger.LogWarning(ex,
                    "Restaurar TOTAL: no se pudo reponer el archivo {OriginalKey} desde el backup {BackupKey}.",
                    originalKey, backupKey);
            }
        }

        return restoredCount;
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
            // Best-effort: si no se puede matar el proceso, igual ya reportamos el fallo del backup arriba.
        }
    }

    /// <summary>
    /// Fix bloqueante #1 (revisión 2026-07-27): COPIA (nunca mueve/borra) todos los objetos del bucket al
    /// prefijo de backup, verificando cada copia con <c>StatObject</c> antes de darla por buena. Devuelve las
    /// claves ORIGINALES copiadas con éxito en <see cref="WipeBackupResult.CopiedObjectKeys"/> — el caller
    /// (<c>SystemDataWipeService</c>) recién las borra DESPUÉS de que la transacción de Postgres commiteo.
    ///
    /// <para>Si un objeto individual falla la copia o la verificación, el backup se reporta como fallido. Los
    /// objetos YA copiados con éxito antes del fallo quedan como copias sueltas en el prefijo (basura
    /// inofensiva: ningún original se tocó, así que no hay pérdida de dato ni inconsistencia).</para>
    ///
    /// <para><b>Timeout propio (hallazgo B-N2(b), 2026-07-28)</b>: antes, esta copia (que puede recorrer TODO
    /// el bucket, un bucket grande con miles de vouchers/adjuntos) no tenía ningún límite de tiempo propio —
    /// solo dependía del <c>ct</c> del caller. Mientras el modo total sostiene el candado de mantenimiento
    /// DESDE ANTES de esta llamada (ver <c>SystemDataRestoreService.ExecuteTotalRestoreAsync</c>), un bucket
    /// enorme o una red lenta hacia MinIO podía colgar el sistema entero indefinidamente, sosteniendo el
    /// candado sin que ningún timeout lo cortara. <c>Wipe:MinioCopyTimeoutMinutes</c> (default
    /// <see cref="DefaultMinioCopyTimeoutMinutes"/>) acota esto — igual que el patrón de <c>RunPgDumpAsync</c>,
    /// el timeout está LINKEADO al <paramref name="ct"/> del caller (a diferencia del <c>pg_restore</c> total,
    /// acá SÍ es seguro heredar la cancelación del caller: esta copia solo toca objetos NUEVOS en el mismo
    /// bucket, nunca datos en uso — cancelarla a mitad de camino dejaría, como mucho, copias sueltas
    /// incompletas en el prefijo de backup, basura inofensiva, nunca una base a medio reemplazar).</para>
    /// </summary>
    private async Task<WipeBackupResult> CopyMinioObjectsAsync(string minioPrefix, CancellationToken ct)
    {
        var bucket = _configuration["Minio:BucketName"] ?? _configuration["MINIO_BUCKET_NAME"] ?? "reservations";
        var timeoutMinutes = _configuration.GetValue<int?>("Wipe:MinioCopyTimeoutMinutes") ?? DefaultMinioCopyTimeoutMinutes;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        var timeoutToken = timeoutCts.Token;

        try
        {
            var listArgs = new ListObjectsArgs()
                .WithBucket(bucket)
                .WithRecursive(true);

            var keysToCopy = new List<string>();
            await foreach (var item in _minioClient.ListObjectsEnumAsync(listArgs, timeoutToken))
            {
                if (item.IsDir)
                {
                    continue;
                }

                // Ya es backup de una operacion anterior (wipe o restore total): no lo volvemos a copiar
                // (evita anidar prefijos en operaciones sucesivas, ej. "wipe-backup-A/wipe-backup-B/archivo").
                if (KnownBackupPrefixMarkers.Any(marker => item.Key.StartsWith(marker, StringComparison.Ordinal)))
                {
                    continue;
                }

                keysToCopy.Add(item.Key);
            }

            var copiedKeys = new List<string>(keysToCopy.Count);
            foreach (var key in keysToCopy)
            {
                var destinationKey = minioPrefix + key;

                var copySource = new CopySourceObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(key);
                var copyArgs = new CopyObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(destinationKey)
                    .WithCopyObjectSource(copySource);
                await _minioClient.CopyObjectAsync(copyArgs, timeoutToken);

                // Verificacion OBLIGATORIA (fix bloqueante #1): confirmamos que la copia existe DE VERDAD en
                // el destino antes de confiar en ella. Si esto tira, el catch de abajo aborta todo el backup
                // (los originales, incluido este, siguen intactos - nunca se llamo a RemoveObject).
                var statArgs = new StatObjectArgs().WithBucket(bucket).WithObject(destinationKey);
                await _minioClient.StatObjectAsync(statArgs, timeoutToken);

                copiedKeys.Add(key);
            }

            return new WipeBackupResult(true, null, minioPrefix, null, copiedKeys);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogError(
                "Empezar de cero: la copia de MinIO excedio el timeout de {Timeout} minutos. Los originales NO se tocaron.",
                timeoutMinutes);
            return new WipeBackupResult(false, null, null, $"La copia de archivos a MinIO excedio el timeout de {timeoutMinutes} minutos.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Empezar de cero: fallo copiando/verificando objetos de MinIO al backup (bucket {Bucket}). Los originales NO se tocaron.", bucket);
            return new WipeBackupResult(false, null, null, $"No se pudieron copiar los archivos de MinIO: {ex.Message}");
        }
    }
}
