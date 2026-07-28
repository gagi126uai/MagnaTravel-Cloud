namespace TravelApi.Application.DTOs;

/// <summary>
/// Respuesta de <c>GET /api/system/status</c> (Obra "Restaurar TOTAL", 2026-07-28, firmada): estado LIVIANO y
/// PÚBLICO del sistema, pensado para que el front lo consulte cada pocos segundos mientras muestra la pantalla
/// especial "estamos restaurando, volvemos en un minuto". No expone nada sensible — solo si hay mantenimiento,
/// por qué, y desde cuándo.
/// </summary>
public sealed class SystemStatusResponse
{
    public bool EnMantenimiento { get; set; }
    public string? Motivo { get; set; }
    public DateTime? Desde { get; set; }
}
