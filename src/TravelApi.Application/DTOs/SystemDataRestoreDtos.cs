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

    /// <summary>
    /// Modo <see cref="RestoreModes.Total"/>: nombre del backup que se generó AUTOMÁTICAMENTE del estado
    /// ACTUAL (antes de sobrescribirlo) — es el "deshacer el deshacer": si la restauración total no era lo
    /// que el usuario quería, este archivo permite volver a como estaba justo antes de ejecutarla.
    /// </summary>
    public string? BackupPrevio { get; set; }

    /// <summary>Modo <see cref="RestoreModes.Total"/>: nombre del archivo de backup que se restauró.</summary>
    public string? RestauradoDe { get; set; }
}
