namespace TravelApi.Application.Interfaces;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "hallazgo del dueño en vivo"): respaldo REAL del dolar oficial via
/// dos APIs publicas, para cuando ARCA no sirve un numero util. Motivo concreto: en homologacion,
/// <c>FEParamGetCotizacion</c> devuelve cotizaciones de JUGUETE (verificado 2026-08-05: dio 1152.202
/// cuando el oficial real de ese dia rondaba 1496) — sin este respaldo, el dashboard mostraba un
/// numero falso o quedaba mudo.
///
/// <para><b>Solo lo usa <see cref="ExchangeRateSyncJob"/></b> (mismo criterio que
/// <see cref="IBnaExchangeRateService"/>): el camino interactivo de las pantallas nunca le pega a una
/// API externa en vivo, solo lee lo que el job diario ya dejo guardado en <c>ExchangeRateQuotes</c>
/// (via <see cref="IExchangeRateResolver"/>).</para>
///
/// <para><b>Nunca tira una excepcion hacia afuera</b> (mismo criterio T-12 que el resto de la
/// escalera de tipo de cambio): cualquier falla de red, timeout, o respuesta con forma inesperada se
/// atrapa adentro y devuelve <c>null</c> — el job decide que hacer con eso (seguir con el proximo
/// respaldo, o dejar el hueco para la proxima corrida).</para>
/// </summary>
public interface IOfficialDollarPublicApiService
{
    /// <summary>
    /// Cotizacion de HOY desde dolarapi.com (<c>GET /v1/dolares/oficial</c>). Usa el campo
    /// <c>venta</c> (lo que el cliente paga), consistente con el resto de la escalera (ARCA
    /// <c>MonCotiz</c>, BNA vendedor).
    /// </summary>
    Task<PublicDollarRateReading?> GetTodayRateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cotizacion de una fecha PASADA desde argentinadatos.com
    /// (<c>GET /v1/cotizaciones/dolares/oficial/{yyyy}/{MM}/{dd}</c>). Usada solo por el backfill del
    /// job — esta API si tiene historial por fecha (a diferencia del scraper BNA, que solo sirve el
    /// dato de "ahora"). Devuelve <c>null</c> si la API no tiene fila para esa fecha (404).
    /// </summary>
    Task<PublicDollarRateReading?> GetRateForDateAsync(DateOnly date, CancellationToken cancellationToken);
}

/// <summary>
/// Lectura minima de una de las dos APIs publicas. <see cref="ProviderName"/> identifica CUAL de las
/// dos respondio ("dolarapi" / "argentinadatos") — es lo que termina en
/// <c>ExchangeRateQuote.ProviderName</c>, nunca se le muestra al usuario (T-5).
/// </summary>
public record PublicDollarRateReading(decimal Rate, string ProviderName);
