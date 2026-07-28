namespace TravelApi.Application.Interfaces;

/// <summary>Un archivo de backup encontrado en el directorio de backups (ver <c>Wipe:BackupDirectory</c>).</summary>
public sealed record BackupFileInfo(string FileName, DateTime LastWriteTimeUtc, long SizeBytes);

/// <summary>
/// Resultado de validar un backup con <c>pg_restore --list</c> (lee el índice del archivo, NO restaura nada).
/// </summary>
public sealed record RestoreVerifyResult(bool Success, string? ErrorMessage, int TableCount, bool HasKeyTables);

/// <summary>
/// Resultado de restaurar el backup completo a la base SOMBRA (<c>&lt;db&gt;_shadow</c>). Nunca toca la base
/// viva — la base sombra se recrea (drop + create) en cada intento, así que siempre queda "limpia" antes de
/// restaurar.
/// </summary>
public sealed record ShadowRestoreResult(bool Success, string? ErrorMessage, string? ShadowConnectionString);

/// <summary>
/// Resultado de restaurar, tabla por tabla, datos de configuración sobre la base VIVA (modo <c>real</c>). Cada
/// tabla se procesa de forma independiente: <see cref="RestoredTables"/> son las que quedaron con datos,
/// <see cref="SkippedNonEmptyTables"/> las que NO se tocaron porque ya tenían filas (nunca se sobrescribe).
/// </summary>
public sealed record LiveTableRestoreResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<string> RestoredTables,
    IReadOnlyList<string> SkippedNonEmptyTables);

/// <summary>
/// Obra "Restaurar desde la app" (2026-07-27, Parte B firmada): puerto (patrón hexagonal, mismo espíritu que
/// <see cref="IWipeBackupPort"/>) para las operaciones REALES de <c>pg_restore</c>/administración de bases
/// que necesita la restauración. Separado en un puerto para poder testear <c>SystemDataRestoreService</c> sin
/// depender de binarios de Postgres ni de archivos reales en disco (se inyecta un fake en los tests
/// unitarios). El puerto real se prueba por construcción (mismo criterio que
/// <c>PgDumpAndMinioWipeBackupPort</c>): correrlo de verdad en producción/manualmente es la única forma
/// honesta de validar un <c>Process.Start</c> real.
/// </summary>
public interface IDatabaseRestorePort
{
    /// <summary>Lista los archivos de backup disponibles (directorio <c>Wipe:BackupDirectory</c>), más nuevo primero.</summary>
    Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync(CancellationToken ct);

    /// <summary>Valida el archivo (existencia + <c>pg_restore --list</c>) sin restaurar nada.</summary>
    Task<RestoreVerifyResult> VerifyBackupAsync(string fileName, CancellationToken ct);

    /// <summary>Restaura el backup completo a la base sombra (recreándola antes). No toca la base viva.</summary>
    Task<ShadowRestoreResult> RestoreToShadowDatabaseAsync(string fileName, CancellationToken ct);

    /// <summary>
    /// Restaura, tabla por tabla y SOLO si cada una está vacía, los datos de las tablas pedidas directamente
    /// sobre la base viva (data-only, sin tocar el schema). <paramref name="tableNames"/> ya viene validado
    /// por el caller contra la lista blanca de tablas de configuración — este puerto NO decide qué tablas son
    /// seguras, solo ejecuta.
    /// </summary>
    Task<LiveTableRestoreResult> RestoreTablesIntoLiveDatabaseAsync(
        string fileName, IReadOnlyList<string> tableNames, CancellationToken ct);

    /// <summary>
    /// Hallazgo menor de seguridad (revisión 2026-07-27): borra la base sombra (<c>&lt;db&gt;_shadow</c>)
    /// después de haber leído los conteos del modo <c>prueba</c>. Sin esto, quedaría una copia COMPLETA de la
    /// base de producción (con datos de pasajeros/clientes reales) para siempre, sin que nadie la borre nunca
    /// — un riesgo de exposición de datos permanente por una operación que solo necesitaba existir un
    /// instante. Es best-effort/idempotente (si la base sombra no existe, no hace nada); no debe bloquear el
    /// resultado ya calculado si falla.
    /// </summary>
    Task DropShadowDatabaseAsync(CancellationToken ct);
}
