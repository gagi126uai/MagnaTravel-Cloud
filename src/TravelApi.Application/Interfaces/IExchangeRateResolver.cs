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
    /// <param name="excludePracticeOfficialData">
    /// ADR-011 (enmienda 2026-08-05, "hallazgo normativo ARCA 10240"): en <c>false</c> (default,
    /// el camino de SIEMPRE que usa la pantalla de facturar) el comportamiento NO cambia — una fila
    /// <see cref="ExchangeRateSource.AfipOficial"/> de homologacion se sigue sirviendo como sugerencia,
    /// porque facturar en homologacion EXIGE que el TC declarado coincida con el numero de juguete que
    /// ARCA va a validar (si sugiriéramos el dolar real, la factura de prueba rebotaria con el error
    /// 10240 de ARCA). En <c>true</c> (lo usa el dashboard, que NO factura nada, solo muestra una
    /// referencia de negocio) una fila <see cref="ExchangeRateSource.AfipOficial"/> que no vino del
    /// entorno productivo de ARCA (dato de juguete) se descarta SIEMPRE, sin importar en que entorno
    /// esta corriendo el sistema ahora mismo; el resto de fuentes (BNA_*, <see
    /// cref="ExchangeRateSource.OficialPorApi"/>) son datos reales y valen en cualquier entorno.
    /// </param>
    Task<ExchangeRateSuggestion?> GetSuggestionAsync(
        string currency, DateOnly date, CancellationToken ct, bool excludePracticeOfficialData = false);

    /// <summary>
    /// TRABAJO 2 (boton "actualizar" de la tira del dolar, 2026-08-05, orden textual del dueño):
    /// dispara EXPLICITAMENTE una sincronizacion on-demand para <paramref name="currency"/> — mismo
    /// mecanismo fire-and-forget (<c>IBackgroundJobClient.Enqueue</c>) y MISMO debounce de 5 minutos
    /// (misma clave de cache) que ya usa <see cref="GetSuggestionAsync"/> cuando detecta que no hay
    /// fila de hoy. Compartir la clave de debounce es a proposito: un click del usuario y el disparo
    /// automatico de una consulta normal NO deberian poder encolar el job dos veces en la misma
    /// ventana de 5 minutos.
    ///
    /// <para>A diferencia del disparo automatico de <see cref="GetSuggestionAsync"/>, este metodo NO
    /// chequea si ya hay fila de hoy antes de encolar: es un pedido EXPLICITO del usuario ("el dolar
    /// esta viejo, buscalo de nuevo AHORA"), asi que si el debounce lo permite, encola sin
    /// condiciones extra.</para>
    /// </summary>
    /// <returns>
    /// <c>true</c> si efectivamente encolo el job en esta llamada; <c>false</c> si estaba debounced
    /// (ya se encolo hace menos de 5 minutos, por este mismo boton o por el disparo automatico) o si
    /// no hay <c>IBackgroundJobClient</c> disponible (tests con el ctor corto). En los dos casos el
    /// endpoint que llama a esto responde 202 igual (P-21: nunca se le hace saber al usuario un
    /// detalle tecnico como "estaba debounced" — para el, el pedido "ya esta en camino" de cualquier
    /// forma).
    /// </returns>
    Task<bool> RequestManualSyncAsync(string currency, CancellationToken ct);

    /// <summary>
    /// "Ayuda invisible del tipo de cambio" (spec firmada 2026-08-06, A5.7): el TECHO del dia — el tipo
    /// de cambio MAS ALTO que un comprobante en <paramref name="currency"/> puede declarar para
    /// <paramref name="date"/> sin que el organismo lo rechace.
    ///
    /// <para>Sale SIEMPRE de la cotizacion oficial del organismo (<see cref="ExchangeRateSource.AfipOficial"/>)
    /// mas el margen que fija <see cref="TravelApi.Domain.Helpers.ArcaInvoicingRateCeiling"/>. A proposito
    /// NO se calcula sobre las fuentes de respaldo (Banco Nacion, APIs publicas): el techo es una regla
    /// del organismo contra SU propio numero, y calcularlo sobre otro dato podria bajarle el valor a una
    /// factura legitima. Si no hay cotizacion oficial conocida para esa fecha (ni caminando hacia atras
    /// dentro de la ventana de respaldo), devuelve <c>null</c> = "no sabemos el techo" y nadie acomoda
    /// nada (mismo comportamiento que antes de esta obra).</para>
    /// </summary>
    Task<decimal?> GetInvoicingCeilingAsync(string currency, DateOnly date, CancellationToken ct);
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
/// <param name="IsProductionSource">
/// ADR-011 (enmienda 2026-08-05): entorno de ARCA del que salio la fila (solo tiene sentido fiscal
/// para <see cref="ExchangeRateSource.AfipOficial"/> — ver <see cref="ExchangeRateQuote.IsProductionSource"/>).
/// El controller de facturas lo usa para armar la leyenda "dolar de prueba" cuando la sugerencia es
/// un AfipOficial de homologacion (T-13: el texto se arma en el motor, no en el front).
/// </param>
public record ExchangeRateSuggestion(
    decimal Rate,
    DateOnly RateDate,
    ExchangeRateSource Source,
    string ProviderName,
    DateOnly? ArcaFchCotiz,
    bool IsStale,
    int QuoteId,
    DateTime FetchedAt,
    bool IsProductionSource)
{
    /// <summary>
    /// "Ayuda invisible del tipo de cambio" (spec firmada 2026-08-06, A3): <c>true</c> cuando este
    /// numero NO es plata de verdad — es el que el organismo exige mientras el sistema emite
    /// comprobantes de ensayo. En ese caso el motor completa el tipo de cambio SOLO y la pantalla ni
    /// dibuja el casillero: si le sugirieramos el dolar real, el comprobante rebotaria.
    ///
    /// <para><b>Fuente unica del criterio (regla T-6)</b>: antes esta misma comparacion estaba escrita
    /// a mano en tres lugares (la leyenda de facturar, la tarjeta del inicio y el gate de emision). Al
    /// vivir en el propio dato, los tres no pueden volver a decir cosas distintas.</para>
    /// </summary>
    public bool LoCompletaElSistema =>
        Source == ExchangeRateSource.AfipOficial && !IsProductionSource;
}
