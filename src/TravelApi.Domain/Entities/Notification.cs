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

public class Notification
{
    public int Id { get; set; }
    
    [Required]
    public string UserId { get; set; } = string.Empty; // Who triggered it
    
    [Required]
    public string Message { get; set; } = string.Empty;
    
    public string Type { get; set; } = "Info"; // Success, Error, Info, Warning
    
    public string Priority { get; set; } = "Normal"; // Normal, Urgent
    
    public bool IsRead { get; set; } = false;
    
    public bool IsDismissed { get; set; } = false;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Optional: link to entity
    public int? RelatedEntityId { get; set; }
    public string? RelatedEntityType { get; set; } // "Invoice", "File", etc.
}
