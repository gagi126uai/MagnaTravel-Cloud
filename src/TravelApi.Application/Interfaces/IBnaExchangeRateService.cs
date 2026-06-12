namespace TravelApi.Application.Interfaces;

public interface IBnaExchangeRateService
{
    /// <summary>
    /// Cotizacion del dolar vendedor BNA. Puede disparar un fetch HTTP en vivo a bna.com.ar (con timeout) si el
    /// snapshot en cache esta vencido, por lo que NO debe usarse en un camino critico de latencia.
    /// </summary>
    Task<BnaUsdSellerRateDto?> GetUsdSellerRateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Devuelve SOLO el ultimo snapshot PERSISTIDO en DB (lectura local, rapida), sin disparar ningun fetch HTTP
    /// en vivo. Marcado como IsStale=true porque es un respaldo. Devuelve null si nunca se persistio una
    /// cotizacion. Pensado para caminos donde la cotizacion es informativa y no puede bloquear la respuesta
    /// (ej. el dashboard), que se degradan a este snapshot en vez de esperar a Banco Nacion.
    /// </summary>
    Task<BnaUsdSellerRateDto?> GetPersistedUsdSellerRateAsync(CancellationToken cancellationToken);
}
