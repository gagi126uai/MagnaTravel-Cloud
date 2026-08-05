using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): unica puerta de lectura hacia la
/// libreta de cotizaciones (<c>ExchangeRateQuotes</c>). Ningun servicio de negocio habla
/// directo con esa tabla ni con ARCA para "que dolar sugiero" — todos pasan por aca.
///
/// <para><b>El camino interactivo NO le pega a ARCA</b> (decision de arquitectura firmada): esta
/// implementacion SOLO lee lo que el job diario (<c>ExchangeRateSyncJob</c>) ya dejo guardado.
/// Si no hay dato util, devuelve <c>null</c> y el que llama cae a carga manual — nunca inventa un
/// numero, nunca bloquea al usuario (regla P-21, "el sistema sugiere, no decide").</para>
/// </summary>
public interface IExchangeRateResolver
{
    /// <summary>
    /// Sugiere el tipo de cambio de <paramref name="currency"/> para <paramref name="date"/>.
    /// Escalera de fallback: match exacto de la fecha pedida -&gt; la fila mas reciente dentro de
    /// una ventana de 5 dias hacia atras -&gt; <c>null</c> (sin dato util).
    /// </summary>
    /// <param name="currency">Codigo ISO 4217 ("USD", "ARS"). "ARS"/"PES" siempre devuelve 1 sin
    /// consultar la base.</param>
    /// <param name="date">Fecha para la que se quiere el tipo de cambio.</param>
    Task<ExchangeRateSuggestion?> GetSuggestionAsync(string currency, DateOnly date, CancellationToken ct);
}

/// <summary>
/// Sugerencia de tipo de cambio, SIN nombres internos (T-5): esto es lo que un servicio de
/// negocio necesita para decidir si un TC que llego del usuario coincide con el oficial. La
/// respuesta HTTP que ve el front (<c>ExchangeRatesController</c>) recorta esto todavia mas.
/// </summary>
/// <param name="Rate">El valor del tipo de cambio.</param>
/// <param name="RateDate">Fecha REAL del dato (puede ser anterior a la pedida si vino del walk-back).</param>
/// <param name="Source">Fuente fiscal del dato (<see cref="ExchangeRateSource.AfipOficial"/> o el respaldo BNA).</param>
/// <param name="ProviderName">Origen tecnico real ("ARCA_WSFEv1" / "BNA_Scraper"). Uso interno/auditoria.</param>
/// <param name="ArcaFchCotiz">El <c>FchCotiz</c> que devolvio ARCA, si la fila vino de ahi.</param>
/// <param name="IsStale">true = la fila es de otra fecha (walk-back) o viene del respaldo, no del match exacto.</param>
/// <param name="QuoteId">Id de la fila en <c>ExchangeRateQuotes</c> — el puntero de procedencia que
/// termina en <c>Invoice.ExchangeRateQuoteId</c> si el usuario acepta esta sugerencia tal cual.</param>
/// <param name="FetchedAt">Momento (UTC) en que el job efectivamente trajo este dato de la fuente
/// (no confundir con el momento en que se factura: eso lo decide quien consume la sugerencia).</param>
public record ExchangeRateSuggestion(
    decimal Rate,
    DateOnly RateDate,
    ExchangeRateSource Source,
    string ProviderName,
    DateOnly? ArcaFchCotiz,
    bool IsStale,
    int QuoteId,
    DateTime FetchedAt);
