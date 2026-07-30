namespace TravelApi.Application.DTOs;

/// <summary>
/// Obra "Restaurar desde la app" (2026-07-27, Parte B firmada) + Parte C "Restaurar TOTAL" (2026-07-28,
/// firmada por el dueño): nombres de los tres modos de restauración.
/// <c>Prueba</c> restaura el backup completo a una base SOMBRA separada (nunca toca la base viva, sirve solo
/// para verificar "¿esto tiene lo que necesito?"). <c>Real</c> restaura SOLO las tablas de configuración
/// (data-only, y solo si están vacías) directamente sobre la base viva. <c>Total</c> reemplaza TODA la base
/// viva por la foto del backup elegido (con backup previo obligatorio del estado actual, modo mantenimiento
/// mientras dura, y todo dentro de una única transacción de Postgres) — ver el comentario completo en
/// <c>ISystemDataRestoreService</c> para el detalle de las tres garantías.
/// </summary>
public static class RestoreModes
{
    public const string Prueba = "prueba";
    public const string Real = "real";
    public const string Total = "total";

    public static readonly string[] All = { Prueba, Real, Total };
}

/// <summary>
/// ADR-052 (D5): valores del contrato de <c>versionResguardo</c> — strings en castellano, misma convención que
/// <see cref="RestoreModes"/> ("prueba"/"real"/"total"). Es información para AVISAR, no para habilitar: ningún
/// valor apaga el botón de restaurar (decisión firmada, cierra el menor M1 de la re-review), porque la lectura
/// que los calcula es barata y puede equivocarse — el único veredicto que frena algo es el del motor, que avisa
/// sin tocar nada (regla P-9).
/// </summary>
public static class BackupVersionStates
{
    /// <summary>El resguardo es de la misma versión del sistema que corre hoy: se restaura como siempre.</summary>
    public const string Actual = "actual";

    /// <summary>Resguardo de una versión ANTERIOR: se puede restaurar y el sistema se actualiza solo después.</summary>
    public const string Anterior = "anterior";

    /// <summary>Resguardo que parece ser de una versión MÁS NUEVA: muy probablemente el motor lo rechace.</summary>
    public const string Posterior = "posterior";

    /// <summary>No se pudo determinar de qué versión es (archivo ilegible, historial no parseable). NUNCA se degrada a "actual".</summary>
    public const string Desconocida = "desconocida";

    public static readonly string[] All = { Actual, Anterior, Posterior, Desconocida };
}

/// <summary>
/// Rediseño de la pantalla de resguardos (2026-07-30, firmado, §7 punto 1): las TRES frases posibles de la
/// columna "Por qué se guardó". A diferencia de <see cref="BackupVersionStates"/> (que son códigos y la
/// pantalla traduce), acá el motor manda la frase YA ESCRITA en criollo: el origen sale de un detalle interno
/// (el prefijo con el que se bautizó el archivo) que no puede cruzar la frontera de la API (T-5), así que la
/// traducción tiene que pasar del lado del servidor. Los textos son los FIRMADOS en el rediseño: no se
/// reescriben ni se resumen (P-13).
/// </summary>
public static class BackupOriginLabels
{
    /// <summary>La copia la generó "Empezar de cero", justo antes de borrar.</summary>
    public const string AfterWipe = "Antes de empezar de cero";

    /// <summary>La copia la generó "Restaurar todo", como foto del estado anterior (el "deshacer el deshacer").</summary>
    public const string BeforeRestore = "Antes de volver a una copia";

    /// <summary>
    /// Cualquier otro origen que el motor no pueda determinar (un archivo dejado a mano en el volumen, el
    /// resguardo diario del sidecar, etc.). Es el valor por DEFECTO a propósito: un origen que no consta jamás
    /// se adivina.
    /// </summary>
    public const string Manual = "Guardada a mano";

    public static readonly string[] All = { AfterWipe, BeforeRestore, Manual };
}

/// <summary>Un backup disponible para restaurar, tal como lo va a ver el usuario en la lista (Parte B).</summary>
public sealed class BackupFileSummaryDto
{
    public string Archivo { get; set; } = string.Empty;
    public DateTime FechaUtc { get; set; }
    public long TamanioBytes { get; set; }

    /// <summary>
    /// ADR-052 (D5): marca informativa de versión, uno de <see cref="BackupVersionStates"/>. SIN ids de
    /// migración ni conteos internos (T-5). La pantalla la usa para avisar; el motor decide aparte.
    /// </summary>
    public string VersionResguardo { get; set; } = BackupVersionStates.Desconocida;

    /// <summary>
    /// Rediseño 2026-07-30 (§7 punto 1): POR QUÉ se guardó esta copia, en criollo y listo para mostrar tal cual
    /// (uno de <see cref="BackupOriginLabels"/>). Nunca el nombre del archivo ni su prefijo (T-5).
    /// </summary>
    public string PorQueSeGuardo { get; set; } = BackupOriginLabels.Manual;
}

/// <summary>Respuesta de <c>GET /admin/danger/backups</c>: lista de resguardos disponibles, mas nuevo primero.</summary>
public sealed class SystemDataBackupsResponse
{
    public List<BackupFileSummaryDto> Backups { get; set; } = new();
}

/// <summary>Body de <c>POST /admin/danger/restore/verify</c>. Este endpoint NO restaura nada, solo valida el archivo.</summary>
public sealed class SystemDataRestoreVerifyRequest
{
    public string Archivo { get; set; } = string.Empty;
}

/// <summary>
/// Respuesta de <c>POST /admin/danger/restore/verify</c>: resumen del contenido del backup, leído con
/// <c>pg_restore --list</c> (el índice del archivo), sin restaurar nada todavía.
/// </summary>
public sealed class SystemDataRestoreVerifyResponse
{
    public bool Valido { get; set; }
    public string? Motivo { get; set; }
    public int CantidadTablas { get; set; }
    public bool TieneTablasClave { get; set; }
}

/// <summary>
/// Body de <c>POST /admin/danger/restore</c>. La frase y la contraseña son el mismo candado "a prueba de
/// dedos" que el borrado masivo. <see cref="Tablas"/> solo aplica (y es obligatorio) cuando
/// <see cref="Modo"/> es <see cref="RestoreModes.Real"/>: la lista de tablas de configuración a restaurar
/// (tiene que ser un subconjunto de <c>TravelApi.Application.Constants.WipeGroups.ConfiguracionTables</c>).
/// <see cref="Motivo"/> solo aplica (y es obligatorio, mínimo 10 caracteres) cuando <see cref="Modo"/> es
/// <see cref="RestoreModes.Total"/> (hallazgo B6 de seguridad, F-16): la operación más destructiva del sistema
/// exige que quien la ejecuta escriba POR QUÉ, y ese motivo queda en la auditoría.
/// </summary>
public sealed class SystemDataRestoreRequest
{
    public string Archivo { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Phrase { get; set; } = string.Empty;
    public string Modo { get; set; } = string.Empty;
    public List<string>? Tablas { get; set; }
    public string? Motivo { get; set; }
}

/// <summary>
/// Respuesta 200 de <c>POST /admin/danger/restore</c>. En modo <see cref="RestoreModes.Prueba"/>,
/// <see cref="Conteos"/> trae los conteos de la base sombra recién restaurada (para que el usuario verifique
/// "¿esto es lo que esperaba?"). En modo <see cref="RestoreModes.Real"/>, <see cref="TablasRestauradas"/>/
/// <see cref="TablasSalteadas"/> traen nombres de NEGOCIO (nunca nombres técnicos de tabla, regla T-5) de qué
/// se repuso y qué se salteó por ya tener datos (nunca se sobrescribe — ver <c>ISystemDataRestoreService</c>).
/// </summary>
public sealed class SystemDataRestoreResponse
{
    public string Modo { get; set; } = string.Empty;
    public SystemDataWipeCounts? Conteos { get; set; }

    /// <summary>Modo real: qué se repuso, en nombres de negocio (ej. "la conexión con AFIP").</summary>
    public List<string>? TablasRestauradas { get; set; }

    /// <summary>Modo real: qué NO se tocó porque ya tenía datos cargados, en nombres de negocio.</summary>
    public List<string>? TablasSalteadas { get; set; }

    /// <summary>
    /// Resumen en criollo, listo para mostrar tal cual (modo real): qué se repuso, qué se salteó y, si
    /// corresponde, el aviso de que AFIP se restauró forzado a homologación.
    /// </summary>
    public string? Mensaje { get; set; }

    /// <summary>
    /// Mensaje en criollo cuando algo NO salió 100% perfecto pero la operación igual se considera exitosa
    /// (ej. modo prueba con un backup de una versión anterior del sistema, donde no se pudieron calcular
    /// todos los conteos porque cambió el esquema desde entonces).
    /// </summary>
    public string? Advertencia { get; set; }

    // Fix de review (rediseño 2026-07-30, hallazgo frontend-reviewer): este DTO tenía dos campos más,
    // `BackupPrevio` y `RestauradoDe`, con el nombre CRUDO del archivo de dump (ej.
    // "pre-restore-20260728-100000.dump"). El front nunca los mostró en pantalla (T-5: arma su propia
    // etiqueta a partir del resguardo que el usuario eligió, no del nombre técnico) y, desde el rediseño de
    // "Copias de seguridad", tampoco los lee para nada — se sacan de la respuesta en vez de mandar un nombre
    // de archivo interno que no hace falta y que un futuro consumidor podría terminar mostrando por error.
}
