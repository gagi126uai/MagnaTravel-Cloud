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
    /// </summary>
    public const string ReservaAutoRegression = "ReservaAutoRegression";
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
