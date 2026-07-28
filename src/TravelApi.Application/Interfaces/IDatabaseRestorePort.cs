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
/// Obra "Restaurar TOTAL" hardening (2026-07-28, hallazgo BLOQUEANTE B1 de seguridad): dos desenlaces posibles
/// de <see cref="IDatabaseRestorePort.RestoreTotalAsync"/>, deliberadamente distintos.
/// </summary>
public enum TotalRestoreOutcome
{
    /// <summary>
    /// El proceso <c>pg_restore</c> terminó dentro de su propio timeout y sabemos con CERTEZA el resultado
    /// (ver <see cref="TotalRestoreResult.Success"/>): o el <c>--single-transaction</c> hizo commit (éxito), o
    /// hizo ROLLBACK automático (falla, la base quedó exactamente como estaba). En ambos casos es SEGURO
    /// desactivar el modo mantenimiento.
    /// </summary>
    Completed,

    /// <summary>
    /// Se agotó NUESTRO PROPIO timeout (nunca el del pedido HTTP, ver el comentario de <c>RestoreTotalAsync</c>)
    /// y tuvimos que matar el proceso a la fuerza. NO hay certeza de si Postgres ya terminó de revertir la
    /// transacción abierta por <c>--single-transaction</c> en el instante exacto en que se cortó la conexión.
    /// Mientras el desenlace sea este, el sistema NO puede salir de mantenimiento — hacerlo sería mentirle al
    /// usuario ("ya terminó, es seguro") sin saberlo de verdad.
    /// </summary>
    UnknownMayStillBeRunning,
}

/// <summary>
/// Obra "Restaurar TOTAL" (2026-07-28, firmada) + hardening B1: resultado de reemplazar TODA la base viva por
/// la foto de un backup. Ver <see cref="TotalRestoreOutcome"/> para la diferencia entre "sabemos qué pasó" y
/// "no sabemos con certeza".
/// </summary>
public sealed record TotalRestoreResult(TotalRestoreOutcome Outcome, bool Success, string? ErrorMessage);

/// <summary>
/// Obra "Restaurar TOTAL" hardening (2026-07-28, hallazgo B7 de seguridad, "guard de compatibilidad de
/// esquema"): resultado de comparar la versión de esquema del backup contra la versión actual del sistema
/// (tabla <c>__EFMigrationsHistory</c>, que EF Core mantiene con el historial exacto de migraciones
/// aplicadas). <see cref="Compatible"/>=false es FAIL-CLOSED: ante cualquier duda (no se pudo leer, no
/// coincide, el backup no tiene información de versión), se rechaza la restauración.
/// </summary>
public sealed record SchemaCompatibilityResult(bool Compatible, string? ErrorMessage);

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
    /// Obra "Restaurar TOTAL" (2026-07-28, firmada): reemplaza TODA la base viva por la foto del backup
    /// elegido. A diferencia de <see cref="RestoreTablesIntoLiveDatabaseAsync"/> (data-only, tabla por tabla,
    /// solo tablas vacías), esto es <c>pg_restore --clean --if-exists --single-transaction</c>: DROPEA y
    /// recrea CADA objeto del backup (tablas, índices, constraints — el esquema completo), todo dentro de UNA
    /// sola transacción de Postgres.
    ///
    /// <para><b>Por qué hay que cortar las conexiones activas primero</b>: mientras haya OTRA conexión con una
    /// tabla abierta (aunque sea en una transacción de solo lectura), el <c>DROP TABLE</c> que hace
    /// <c>--clean</c> se queda esperando ese lock para siempre. Por eso este método, ANTES de lanzar
    /// <c>pg_restore</c>, vacía el pool de conexiones de la propia API (<c>NpgsqlConnection.ClearAllPools</c>)
    /// y termina (<c>pg_terminate_backend</c>) cualquier conexión que siga activa contra la base viva — el
    /// caller (<c>SystemDataRestoreService</c>) ya puso al sistema en modo mantenimiento ANTES de llamar acá,
    /// así que no deberían quedar pedidos nuevos generando conexiones nuevas mientras esto corre.</para>
    /// </summary>
    /// <b>El timeout de esta operación es PROPIO, nunca el <paramref name="ct"/> del pedido HTTP</b> (hallazgo
    /// BLOQUEANTE B1 de seguridad, 2026-07-28): si el pedido se cancela (el admin cierra la pestaña, el proxy
    /// corta la conexión), el <c>pg_restore</c> real puede seguir corriendo perfectamente bien en el servidor
    /// — tratar esa cancelación como "ya terminó y falló" apagaría el modo mantenimiento MIENTRAS la base
    /// sigue siendo reemplazada, con el sistema ya abierto a los usuarios. Ver <see cref="TotalRestoreOutcome"/>.
    /// </summary>
    Task<TotalRestoreResult> RestoreTotalAsync(string fileName, CancellationToken ct);

    /// <summary>
    /// Obra "Restaurar TOTAL" hardening (2026-07-28, hallazgo B7 de seguridad): compara la versión de esquema
    /// del backup contra la versión actual del sistema ANTES de tocar la base viva — fail-closed (ver
    /// <see cref="SchemaCompatibilityResult"/>). Restaura únicamente la tabla <c>__EFMigrationsHistory</c> a
    /// una base sombra descartable (mismo mecanismo que <see cref="RestoreToShadowDatabaseAsync"/>, pero solo
    /// esa tabla en vez del backup completo — mucho más liviano) y compara el conjunto de migraciones contra
    /// las ya aplicadas en la base viva.
    /// </summary>
    Task<SchemaCompatibilityResult> CheckSchemaCompatibilityAsync(string fileName, CancellationToken ct);

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
