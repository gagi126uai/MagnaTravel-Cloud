using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Valores canonicos del campo <see cref="Notification.Type"/>. Los genericos (Info/Success/Error/
/// Warning) gobiernan el estilo visual; los DEDICADOS identifican un aviso concreto para deduplicar
/// sin riesgo de chocar con otros avisos del mismo color/prioridad.
/// </summary>
public static class NotificationTypes
{
    public const string Info = "Info";
    public const string Success = "Success";
    public const string Error = "Error";
    public const string Warning = "Warning";

    /// <summary>
    /// ADR-020 (decision #6): regresion automatica Confirmed -> En gestion. Type DEDICADO para que el
    /// dedup ("ya avise hoy de esta reserva") matchee SOLO regresiones y no cualquier Warning urgente.
    ///
    /// <para>OBSOLETO (2026-06-24): la regresion automatica Confirmed -> En gestion se elimino (las reservas
    /// confirmadas ya no vuelven solas a En gestion; en su lugar quedan marcadas "confirmada con cambios", ver
    /// <see cref="ReservaNeedsReview"/>). Esta constante se conserva para no romper avisos historicos ya
    /// persistidos con este Type, pero NO se generan nuevos.</para>
    /// </summary>
    public const string ReservaAutoRegression = "ReservaAutoRegression";

    /// <summary>
    /// 2026-06-24 (reemplaza a <see cref="ReservaAutoRegression"/>): una reserva confirmada quedo MARCADA
    /// "confirmada con cambios / revisar" porque un servicio dejo de estar resuelto o se quedo sin servicios
    /// (antes esto la regresaba a En gestion; ahora la deja en Confirmed y solo avisa). Type DEDICADO para que
    /// el dedup ("ya avise hoy de esta reserva") matchee SOLO este aviso y no cualquier Warning urgente.
    /// </summary>
    public const string ReservaNeedsReview = "ReservaNeedsReview";
}

/// <summary>
/// Valores canonicos del campo <see cref="Notification.RelatedEntityType"/>. Cada aviso "vive" mientras su CAUSA
/// exista; la causa se identifica por el tipo de entidad relacionada + su id (ver <see cref="NotificationResolutionKeys"/>).
/// Se centralizan como constantes para que productores (jobs), el auto-resolutor y los tests usen el MISMO literal.
/// </summary>
public static class NotificationRelatedEntityTypes
{
    /// <summary>Aviso "esta reserva sale pronto y todavia debe" (lo produce el monitor financiero nocturno).</summary>
    public const string ReservaUnpaidDeparture = "ReservaUnpaidDeparture";

    /// <summary>Aviso ligado a una factura (emision, anulacion, error de anulacion, forzado por excepcion).</summary>
    public const string Invoice = "Invoice";

    /// <summary>Marca "confirmada con cambios / revisar" ligada a una reserva (mismo id que la reserva).</summary>
    public const string Reserva = "Reserva";
}

/// <summary>
/// Arma la CLAVE DE RESOLUCION ("resolution key") de una notificacion: el identificador estable de su CAUSA.
/// Dos avisos con la misma clave hablan del mismo problema; cuando ese problema se resuelve (factura anulada,
/// reserva saldada, marca de revision bajada), se marca <see cref="Notification.ResolvedAt"/> en todos los que
/// comparten la clave y el aviso se apaga solo (decision del dueno: los avisos se apagan cuando la causa muere).
///
/// <para><b>Convencion</b>: <c>"{RelatedEntityType}:{RelatedEntityId}"</c> (ej. <c>"Invoice:42"</c>). Si un productor
/// necesita mas granularidad (varios avisos distintos sobre la MISMA entidad), usa un tipo dedicado como prefijo:
/// <c>"{tipo}:{entidad}"</c> (ej. <c>"ReservaNeedsReview:7"</c> para no chocar con otros avisos de la reserva 7).</para>
/// </summary>
public static class NotificationResolutionKeys
{
    /// <summary>
    /// Clave por defecto a partir del par (tipo de entidad, id). Devuelve null si falta cualquiera de los dos:
    /// un aviso sin entidad relacionada no tiene causa identificable y no participa del auto-resolve.
    /// </summary>
    public static string? ForEntity(string? relatedEntityType, int? relatedEntityId)
    {
        if (string.IsNullOrWhiteSpace(relatedEntityType) || relatedEntityId is null)
            return null;

        return $"{relatedEntityType}:{relatedEntityId}";
    }

    /// <summary>
    /// Clave con prefijo de tipo dedicado, para avisos que comparten <see cref="Notification.RelatedEntityType"/>
    /// con otros pero hablan de una causa distinta (ej. "confirmada con cambios" vs "sale pronto y debe" sobre la
    /// misma reserva). Formato: <c>"{typePrefix}:{relatedEntityId}"</c>.
    /// </summary>
    public static string ForTyped(string typePrefix, int relatedEntityId)
        => $"{typePrefix}:{relatedEntityId}";
}

public class Notification
{
    public int Id { get; set; }
    
    [Required]
    public string UserId { get; set; } = string.Empty; // Who triggered it
    
    [Required]
    public string Message { get; set; } = string.Empty;
    
    public string Type { get; set; } = "Info"; // Success, Error, Info, Warning
    
    public string Priority { get; set; } = "Normal"; // Normal, Urgent
    
    // IsRead / IsDismissed: historico de "ya la vi" de ESTE usuario. Antes gobernaban dos vistas distintas
    // (campanita miraba !IsRead; banner urgente miraba !IsDismissed), lo que dejaba avisos "medio vistos".
    // Desde Tanda 5 marcar leida o descartar setea AMBOS: para un unico usuario "ya la vi" es una sola cosa.
    public bool IsRead { get; set; } = false;

    public bool IsDismissed { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Optional: link to entity
    public int? RelatedEntityId { get; set; }
    public string? RelatedEntityType { get; set; } // "Invoice", "File", etc.

    /// <summary>
    /// (Tanda 5, 2026-07-05) Clave estable de la CAUSA del aviso (ver <see cref="NotificationResolutionKeys"/>).
    /// Convencion <c>"{RelatedEntityType}:{RelatedEntityId}"</c>. Permite apagar de una todos los avisos de una
    /// misma causa cuando esa causa se resuelve, y deduplicar entre dias (no re-crear si ya hay uno vivo con la
    /// misma clave). Null en avisos historicos previos a esta tanda o sin entidad relacionada.
    /// </summary>
    public string? ResolutionKey { get; set; }

    /// <summary>
    /// (Tanda 5, 2026-07-05) Momento en que la CAUSA del aviso murio (factura anulada OK, reserva saldada, marca
    /// de revision bajada, etc.). Null = todavia vigente. Un aviso resuelto deja de mostrarse aunque nadie lo haya
    /// leido: es la pieza que hace que los avisos se apaguen SOLOS (decision del dueno). Se conserva como historico.
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// Un aviso esta "vivo" (debe mostrarse) mientras su causa siga vigente Y el usuario no lo haya visto:
    /// <c>ResolvedAt == null</c> (causa aun presente) y ni leido ni descartado. Semantica UNICA para la campanita
    /// y el banner urgente (cada uno agrega ademas su propio criterio de prioridad). Es una propiedad calculada
    /// (no se mapea a columna): el filtro real en las queries se escribe inline para que EF lo traduzca a SQL.
    /// </summary>
    public bool IsLive => ResolvedAt == null && !IsRead && !IsDismissed;
}
