namespace TravelApi.Application.DTOs;

/// <summary>
/// Rediseño de la pantalla de resguardos (2026-07-30, firmado, §7 punto 2): los TRES pasos de una restauración
/// total que la pantalla de espera muestra como checklist. Misma convención de contrato que
/// <c>RestoreModes</c> y <c>BackupVersionStates</c>: un CÓDIGO cerrado en castellano (para que la pantalla sepa
/// cuál está en curso y dibuje el ✓/◐/○ sin parsear texto) más el TEXTO en criollo ya escrito, que viaja al
/// lado y se muestra tal cual (P-13: los textos firmados no se reescriben en el front).
///
/// <para><b>OJO — el orden REAL del motor no es el del dibujo firmado</b>: por ADR-052 (D1.9) el resguardo del
/// estado actual se toma DESPUÉS de comprobar que el resguardo elegido se puede restaurar (así, si el archivo
/// está corrupto, no se pagan minutos de mantenimiento al pedo). El motor publica los pasos en el orden en que
/// REALMENTE ocurren — <see cref="Datos"/> → <see cref="Resguardo"/> → <see cref="Actualizacion"/> — porque
/// mentirle al usuario sobre qué está pasando ahora sería peor que el desvío del dibujo. La pantalla tiene que
/// listar los tres en ESE orden (queda anotado para la implementación del front).</para>
/// </summary>
public static class RestoreProgressSteps
{
    /// <summary>Se está leyendo el resguardo elegido y poniéndolo en marcha (el paso largo).</summary>
    public const string Datos = "datos";

    /// <summary>Se está guardando la foto del estado actual, el "deshacer el deshacer".</summary>
    public const string Resguardo = "resguardo";

    /// <summary>Ya se cambió el sistema por el del resguardo y se está terminando de acomodar todo.</summary>
    public const string Actualizacion = "actualizacion";

    public static readonly string[] All = { Datos, Resguardo, Actualizacion };

    /// <summary>
    /// Texto FIRMADO de cada paso (rediseño 2026-07-30, §4.5), listo para mostrar tal cual. Devuelve
    /// <c>null</c> si el código no es uno de los tres — así un valor viejo leído del archivo de estado nunca
    /// llega a la pantalla como texto crudo (T-5).
    /// </summary>
    public static string? TextFor(string? step) => step switch
    {
        Datos => "Trayendo los datos de la copia elegida",
        Resguardo => "Guardamos una copia de cómo está el sistema ahora",
        Actualizacion => "Poniendo el sistema al día",
        _ => null,
    };
}

/// <summary>
/// Respuesta de <c>GET /api/system/status</c> (Obra "Restaurar TOTAL", 2026-07-28, firmada): estado LIVIANO y
/// PÚBLICO del sistema, pensado para que el front lo consulte cada pocos segundos mientras muestra la pantalla
/// especial "estamos restaurando, volvemos en un minuto". No expone nada sensible — solo si hay mantenimiento,
/// por qué, desde cuándo y en qué paso va.
/// </summary>
public sealed class SystemStatusResponse
{
    public bool EnMantenimiento { get; set; }
    public string? Motivo { get; set; }
    public DateTime? Desde { get; set; }

    /// <summary>
    /// Rediseño 2026-07-30 (§7 punto 2): código del paso en curso, uno de <see cref="RestoreProgressSteps"/>.
    /// <c>null</c> cuando no hay ninguna restauración en curso, o cuando el mantenimiento no viene de una
    /// restauración (por ejemplo, un mantenimiento que quedó sostenido esperando al equipo técnico).
    /// </summary>
    public string? Paso { get; set; }

    /// <summary>Texto en criollo del paso en curso, listo para mostrar tal cual (<c>null</c> si no hay paso).</summary>
    public string? PasoTexto { get; set; }
}
