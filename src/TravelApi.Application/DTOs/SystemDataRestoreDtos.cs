namespace TravelApi.Application.DTOs;

/// <summary>
/// Obra "Restaurar desde la app" (2026-07-27, Parte B firmada): nombres de los dos modos de restauración.
/// <c>Prueba</c> restaura el backup completo a una base SOMBRA separada (nunca toca la base viva, sirve solo
/// para verificar "¿esto tiene lo que necesito?"). <c>Real</c> restaura SOLO las tablas de configuración
/// (data-only, y solo si están vacías) directamente sobre la base viva — ver el comentario completo en
/// <c>ISystemDataRestoreService</c> sobre por qué el modo real NO puede ser un restore total desde dentro del
/// proceso de la API.
/// </summary>
public static class RestoreModes
{
    public const string Prueba = "prueba";
    public const string Real = "real";

    public static readonly string[] All = { Prueba, Real };
}

/// <summary>Un backup disponible para restaurar, tal como lo va a ver el usuario en la lista (Parte B).</summary>
public sealed class BackupFileSummaryDto
{
    public string Archivo { get; set; } = string.Empty;
    public DateTime FechaUtc { get; set; }
    public long TamanioBytes { get; set; }
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
/// </summary>
public sealed class SystemDataRestoreRequest
{
    public string Archivo { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Phrase { get; set; } = string.Empty;
    public string Modo { get; set; } = string.Empty;
    public List<string>? Tablas { get; set; }
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
}
