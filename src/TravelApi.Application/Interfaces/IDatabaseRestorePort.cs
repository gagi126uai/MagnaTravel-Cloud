using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// Un archivo de backup encontrado en el directorio de backups (ver <c>Wipe:BackupDirectory</c>).
///
/// <para><b>ADR-052 (D5)</b>: <paramref name="VersionState"/> es la marca INFORMATIVA de "¿de qué versión del
/// sistema es este resguardo?" (uno de <see cref="BackupVersionStates"/>). Se calcula con una lectura barata y
/// cacheada del historial de migraciones del archivo; si no se puede determinar queda
/// <see cref="BackupVersionStates.Desconocida"/> — el default del record es ese A PROPÓSITO (fail-safe: un
/// camino que se olvide de calcularlo NUNCA puede terminar diciendo "actual"). Esta marca JAMÁS habilita ni
/// bloquea una restauración: el único veredicto que frena algo es el gate autoritativo del puerto
/// (<see cref="IDatabaseRestorePort.CheckSchemaCompatibilityAsync"/>).</para>
///
/// <para><b>Rediseño de la pantalla de resguardos (2026-07-30, firmado, §7 punto 1)</b>:
/// <paramref name="OriginLabel"/> es el POR QUÉ se guardó esta copia, YA traducido a criollo por el motor (ver
/// <c>BackupOriginRules</c>). Viaja como frase lista para mostrar; el prefijo del archivo del que se deriva se
/// queda del lado del servidor (T-5). El default es "guardada a mano" a propósito: un origen que no consta
/// nunca se adivina.</para>
/// </summary>
public sealed record BackupFileInfo(
    string FileName,
    DateTime LastWriteTimeUtc,
    long SizeBytes,
    string VersionState = BackupVersionStates.Desconocida,
    string OriginLabel = BackupOriginLabels.Manual);

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
/// del proceso <c>pg_restore</c> largo, deliberadamente distintos. Con ADR-052 este desenlace corresponde a
/// <see cref="IDatabaseRestorePort.RestoreIntoNewDatabaseAsync"/> (antes, al <c>pg_restore</c> sobre la base viva).
/// </summary>
public enum TotalRestoreOutcome
{
    /// <summary>
    /// El proceso <c>pg_restore</c> terminó dentro de su propio timeout y sabemos con CERTEZA el resultado: o el
    /// <c>--single-transaction</c> hizo commit (éxito), o hizo ROLLBACK automático (falla, la base destino quedó
    /// exactamente como estaba). En ambos casos es SEGURO desactivar el modo mantenimiento.
    /// </summary>
    Completed,

    /// <summary>
    /// Se agotó NUESTRO PROPIO timeout (nunca el del pedido HTTP) y tuvimos que matar el proceso a la fuerza. NO
    /// hay certeza de si Postgres ya terminó de revertir la transacción abierta por <c>--single-transaction</c>
    /// en el instante exacto en que se cortó la conexión.
    ///
    /// <para><b>ADR-052 cambió la gravedad de este desenlace</b>: antes el <c>pg_restore</c> corría sobre la base
    /// VIVA, así que "no sé si terminó" obligaba a dejar el sistema en mantenimiento. Ahora corre sobre una base
    /// NUEVA descartable: la incertidumbre es sobre basura, y el sistema puede reabrirse tranquilo (la limpieza
    /// del próximo intento dropea esa base). Se conserva la distinción porque el log tiene que decir la verdad.</para>
    /// </summary>
    UnknownMayStillBeRunning,
}

/// <summary>
/// ADR-052 (D2): los CINCO veredictos posibles del gate de esquema, más el "no se pudo determinar". Reemplaza
/// al "compatible sí/no" de la obra anterior, que exigía igualdad EXACTA de migraciones y por eso dejaba
/// inservibles todos los resguardos anteriores a cada deploy.
///
/// <para><b>El veredicto se calcula contra el ENSAMBLADO</b> (la lista que EF Core trae compilada,
/// <c>Database.GetMigrations()</c>), NO contra el historial de la base viva: el que aplica las migraciones es
/// EF con esa lista, así que comparar contra la base viva sería comparar contra la referencia equivocada
/// cuando la base quedó atrás (deploy a medias).</para>
/// </summary>
public enum RestoreSchemaVerdict
{
    /// <summary>
    /// Fail-closed por defecto: no se pudo leer el historial del resguardo (archivo dañado, no se pudo
    /// preparar la verificación, etc.). Se rechaza. Es el valor 0 A PROPÓSITO: un resultado sin inicializar
    /// nunca puede pasar por "se puede restaurar".
    /// </summary>
    CouldNotDetermine = 0,

    /// <summary>El resguardo tiene EXACTAMENTE las mismas migraciones que el sistema: camino de siempre, sin paso de actualización.</summary>
    Identical,

    /// <summary>
    /// El resguardo es de una versión ANTERIOR: sus migraciones son un subconjunto y lo que falta es el final
    /// de la fila. Se restaura y después el sistema se actualiza solo (decisión firmada del dueño).
    /// </summary>
    SubsetNeedsUpdate,

    /// <summary>El resguardo trae migraciones que este sistema no conoce: es de una versión MÁS NUEVA. Se rechaza.</summary>
    NewerThanSystem,

    /// <summary>
    /// Al resguardo le falta una migración del MEDIO de la fila (no el final): no se puede "completar" con las
    /// que siguen. Se rechaza con un texto DISTINTO al de "versión más nueva" — el de más nueva mentiría.
    /// </summary>
    HistoryGap,

    /// <summary>El resguardo no tiene ninguna fila de historial de migraciones (dump incompleto o versión muy anterior). Se rechaza.</summary>
    DumpHistoryEmpty,

    /// <summary>
    /// La BASE VIVA tiene migraciones pendientes (el sistema quedó a mitad de una actualización). No se
    /// restaura desde la app: el veredicto se calcularía sobre una base que ni ella misma está al día.
    /// </summary>
    LiveHasPendingMigrations,
}

/// <summary>
/// ADR-052 (D2): resultado del gate de esquema. <see cref="ErrorMessage"/> es SIEMPRE detalle INTERNO (para el
/// log, nunca para el usuario — T-5): el texto en criollo lo arma el caller según el
/// <see cref="RestoreSchemaVerdict"/>. <see cref="MissingMigrationsCount"/> es cuántas migraciones habría que
/// aplicar después de restaurar (0 si el resguardo está al día); se usa para el log y la auditoría como
/// NÚMERO, jamás como lista de ids.
/// </summary>
/// <param name="ToleratedOrphanMigrationsCount">
/// Cuántas filas del historial del resguardo el sistema no conoce pero se toleraron por ser ANTERIORES a su
/// última migración (ver la regla en <c>RestoreSchemaVerdictRules</c>). Va a la auditoría como NÚMERO para que
/// quede constancia de que el gate aflojó y cuánto; los ids se quedan en el log interno (T-5).
/// </param>
public sealed record SchemaCompatibilityResult(
    RestoreSchemaVerdict Verdict,
    string? ErrorMessage,
    int MissingMigrationsCount = 0,
    int ToleratedOrphanMigrationsCount = 0);

/// <summary>
/// ADR-052 (D1): resultado de restaurar el dump COMPLETO en una base NUEVA al costado. Si esto falla, la base
/// viva NUNCA se tocó — un resguardo corrupto pasa de "riesgo de dejar la base a medias" a CERO daño.
/// <see cref="NewDatabaseName"/> es un nombre interno (jamás va a una respuesta de API, T-5) que el caller
/// necesita para pedir el intercambio de nombres después.
/// </summary>
public sealed record NewDatabaseRestoreResult(
    TotalRestoreOutcome Outcome,
    bool Success,
    string? NewDatabaseName,
    string? ErrorMessage);

/// <summary>
/// ADR-052 (D1.4): resultado del intercambio de nombres. <see cref="PreviousDatabaseName"/> viene SIEMPRE
/// (haya salido bien o mal): es el nombre bajo el cual quedó (o iba a quedar) estacionada la base ORIGINAL, y
/// es lo único que necesita la vuelta atrás para reconciliar por estado.
/// </summary>
public sealed record DatabaseSwapResult(bool Success, string PreviousDatabaseName, string? ErrorMessage);

/// <summary>
/// ADR-052 (D4): resultado de la vuelta atrás. <see cref="Success"/>=false es el DOBLE FALLO: el sistema queda
/// en mantenimiento sostenido y solo sale a mano por el runbook (<c>docs/db-operations.md</c>).
/// </summary>
public sealed record DatabaseSwapRollbackResult(bool Success, string? ErrorMessage);

/// <summary>
/// ADR-052 (D1.5 + condición C1 de la re-review): resultado del assert de privilegios que corre ANTES de pagar
/// el <c>pg_restore</c> y el resguardo previo. No alcanza con "puede crear bases": para RENOMBRAR una base hay
/// que ser DUEÑO de ella (o superusuario), así que el chequeo incluye la propiedad
/// (<c>datdba = current_user</c>). <see cref="ErrorMessage"/> es detalle interno para el log.
/// </summary>
public sealed record DatabasePrivilegeCheckResult(bool CanManage, string? ErrorMessage);

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
    /// <summary>
    /// Lista los archivos de backup disponibles (directorio <c>Wipe:BackupDirectory</c>), más nuevo primero,
    /// cada uno con su marca informativa de versión (ADR-052 D5, ver <see cref="BackupFileInfo.VersionState"/>).
    /// </summary>
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
    /// ADR-052 (D1.1-3), REEMPLAZA al viejo <c>RestoreTotalAsync</c> (que hacía <c>pg_restore --clean</c> sobre
    /// la base VIVA): restaura el dump COMPLETO en una base NUEVA al costado (<c>&lt;db&gt;_restore_&lt;ts&gt;</c>).
    ///
    /// <para><b>Por qué es mejor y no solo distinto</b>: <c>--clean</c> solo dropea lo que está en el índice del
    /// dump, así que cualquier objeto que existiera en la base viva y NO en el dump sobrevivía (esquema híbrido)
    /// o abortaba la transacción entera. Y sobre todo: tocaba la base viva ANTES de saber si el dump servía. Acá
    /// la base destino está VACÍA (no hace falta <c>--clean</c>) y si el dump está corrupto el daño es CERO.</para>
    ///
    /// <para><b>El timeout es PROPIO, nunca el <paramref name="ct"/> del pedido HTTP</b> (hallazgo B1 de la obra
    /// anterior, sigue vigente). Un timeout acá ya NO deja un desenlace peligroso: lo único incierto es el
    /// estado de una base descartable, que la limpieza del próximo intento dropea (D1.6).</para>
    /// </summary>
    Task<NewDatabaseRestoreResult> RestoreIntoNewDatabaseAsync(string fileName, CancellationToken ct);

    /// <summary>
    /// ADR-052 (D1.4): intercambia los NOMBRES de la base viva y la base recién restaurada, para que la app
    /// siga usando exactamente la misma connection string. Secuencia: <c>ALLOW_CONNECTIONS false</c> sobre el
    /// nombre vivo (sin eso, el worker de Hangfire reconecta entre el <c>pg_terminate_backend</c> y el
    /// <c>RENAME</c>, y el rename falla) → cortar conexiones → <c>RENAME</c> viva a
    /// <c>&lt;db&gt;_old_&lt;ts&gt;</c> → <c>RENAME</c> la nueva al nombre vivo → <c>ALLOW_CONNECTIONS true</c>.
    ///
    /// <para><b>Invariante crítica</b>: pase lo que pase, al salir de acá la base que tenga el NOMBRE VIVO queda
    /// con <c>ALLOW_CONNECTIONS true</c>. Si eso se olvidara, el sistema queda muerto para todos aunque los
    /// datos estén perfectos — es el riesgo nuevo más serio de esta obra (ver el runbook).</para>
    ///
    /// <para><b>Reintentos y reconciliación por ESTADO</b> (condición C1 de la re-review): cada intento decide
    /// qué hacer consultando <c>pg_database</c> (qué bases existen con qué nombre), nunca ejecutando una
    /// secuencia ciega de pasos — así es idempotente y un intento a medias lo termina el siguiente.</para>
    /// </summary>
    Task<DatabaseSwapResult> SwapRestoredDatabaseIntoLiveAsync(string newDatabaseName, CancellationToken ct);

    /// <summary>
    /// ADR-052 (D4): vuelta atrás por intercambio de nombres (segundos, sin un segundo <c>pg_restore</c>).
    /// Deja el sistema EXACTAMENTE como estaba antes del intento: la base original vuelve al nombre vivo y la
    /// que falló queda estacionada como <c>&lt;db&gt;_fallido_&lt;ts&gt;</c> para diagnóstico.
    ///
    /// <para><b>Reconciliación por estado e IDEMPOTENTE</b> (condición C1): si la base original YA tiene el
    /// nombre vivo (porque el intercambio nunca llegó a hacerse, o porque esto ya corrió), no hace NADA y
    /// devuelve éxito. Esta propiedad es la que hace seguro llamarla ante cualquier fallo posterior al
    /// intercambio sin averiguar primero "¿hasta dónde llegué?".</para>
    /// </summary>
    /// <param name="previousDatabaseName">
    /// El nombre bajo el que quedó estacionada la base ORIGINAL (lo devuelve
    /// <see cref="SwapRestoredDatabaseIntoLiveAsync"/> en <see cref="DatabaseSwapResult.PreviousDatabaseName"/>,
    /// siempre, incluso cuando el intercambio falló).
    /// </param>
    Task<DatabaseSwapRollbackResult> RollbackSwapAsync(string previousDatabaseName, CancellationToken ct);

    /// <summary>
    /// ADR-052 (D1.5 + C1): assert de privilegios ANTES de crear nada y ANTES de pagar el <c>pg_restore</c> y el
    /// resguardo previo. Verifica que el usuario pueda crear bases Y que sea DUEÑO de la base viva (renombrar
    /// exige propiedad, no alcanza <c>rolcreatedb</c>). Fail-closed: si no se puede confirmar, no se puede
    /// restaurar desde la app y el camino es el workflow de rescate.
    /// </summary>
    Task<DatabasePrivilegeCheckResult> CheckDatabaseManagementPrivilegesAsync(CancellationToken ct);

    /// <summary>
    /// ADR-052 (D1.6): dropea, de forma idempotente y best-effort, las sobras de intentos anteriores
    /// (<c>&lt;db&gt;_restore_*</c>, <c>&lt;db&gt;_old_*</c>, <c>&lt;db&gt;_fallido_*</c>). Corre al ARRANCAR
    /// cada restauración total: así el disco queda acotado a ~2 copias durante la operación y 1 al terminar.
    /// Nunca toca la base viva ni la sombra del modo prueba.
    /// </summary>
    Task CleanupLeftoverRestoreDatabasesAsync(CancellationToken ct);

    /// <summary>
    /// ADR-052 (D1.6): dropea UNA base por nombre (se usa para la copia vieja al final del camino feliz).
    /// Best-effort: si falla, queda basura en disco, nunca una pérdida de datos — no puede convertir una
    /// restauración exitosa en error.
    /// </summary>
    Task DropDatabaseAsync(string databaseName, CancellationToken ct);

    /// <summary>
    /// ADR-052 (D2), reescrito: gate AUTORITATIVO de versión. Lee el historial de migraciones del resguardo
    /// (restaurando SOLO <c>__EFMigrationsHistory</c> a una base sombra descartable, mucho más liviano que el
    /// dump completo) y lo compara contra la lista del ENSAMBLADO, más el chequeo de que la base viva no tenga
    /// migraciones pendientes. Devuelve uno de los <see cref="RestoreSchemaVerdict"/> — fail-closed ante
    /// cualquier duda.
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
