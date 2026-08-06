using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;

namespace TravelApi.Controllers;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): expone la sugerencia de tipo de cambio
/// que la pantalla de factura precarga en el casillero de USD. Controller FINO: valida entrada,
/// llama al resolver, arma la respuesta — toda la logica de fallback/precedencia vive en
/// <see cref="IExchangeRateResolver"/>.
///
/// <para><b>Nunca bloquea</b> (regla P-21): sin sugerencia responde 204, nunca un error — el
/// casillero queda editable y el usuario carga el numero a mano, igual que hoy.</para>
/// </summary>
[ApiController]
[Route("api/exchange-rates")]
[Authorize]
public class ExchangeRatesController : ControllerBase
{
    private readonly IExchangeRateResolver _resolver;

    public ExchangeRatesController(IExchangeRateResolver resolver)
    {
        _resolver = resolver;
    }

    /// <summary>
    /// Sugerencia de tipo de cambio para <paramref name="currency"/> en <paramref name="date"/>.
    /// Si <paramref name="date"/> no viene, es HOY en hora argentina (§5.3).
    /// </summary>
    /// <param name="currency">Codigo ISO 4217 ("USD"). "ARS"/"PES" siempre da 1.</param>
    /// <param name="date">Fecha (YYYY-MM-DD) para la que se quiere el tipo de cambio.</param>
    [HttpGet("suggestion")]
    [RequirePermission(Permissions.ReservasView)]
    public async Task<ActionResult<ExchangeRateSuggestionResponse>> GetSuggestion(
        [FromQuery] string? currency,
        [FromQuery] DateOnly? date,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedCurrency(currency))
        {
            return BadRequest(new { message = "Indicá una moneda válida (pesos o dólares) para buscar el tipo de cambio." });
        }

        // FIX (detalle #6, revision post-implementacion 2026-08-05): en pesos no hay NADA que
        // sugerir — el TC de pesos es 1 por definicion, no un dato que haya que "consultar" ni
        // mostrarle al usuario como si fuera una cotizacion real. Antes esto devolvia 200 con
        // "Dólar oficial: 1", una leyenda sin sentido para una moneda que no es dolar. El
        // casillero de pesos ni siquiera existe en pantalla (solo se muestra para USD), asi que
        // 204 es el contrato correcto: "no hay sugerencia para esto".
        var normalizedCurrency = currency!.Trim().ToUpperInvariant();
        if (IsPesos(normalizedCurrency))
        {
            return NoContent();
        }

        // "Hoy" SIEMPRE en hora argentina (regla obligatoria §5.3, uno de los 4 lugares donde aplica):
        // interpretar la ausencia de fecha con el reloj crudo del servidor (sin ArgentinaTime) haria que, entre las 21:00 y
        // las 24:00 ART, el sistema pida la cotizacion de MAÑANA (que todavia no existe).
        var hoyArgentina = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());
        var effectiveDate = date ?? hoyArgentina;

        var suggestion = await _resolver.GetSuggestionAsync(normalizedCurrency, effectiveDate, cancellationToken);
        if (suggestion is null)
        {
            // Sin dato util: NO es un error (el job todavia no cubrio esta fecha, o esta fuera de la
            // ventana de respaldo). El casillero queda vacio y el front invita a cargar a mano.
            return NoContent();
        }

        // "Ayuda invisible" (spec firmada 2026-08-06, A3): cuando el numero lo completa el motor, la
        // pantalla no dibuja NADA — ni casillero, ni leyenda, ni el equivalente en pesos. Por eso no le
        // mandamos el numero (no es plata de verdad) ni ningun texto: mandarselo seria confiar en que el
        // front se acuerde de esconderlo.
        if (suggestion.LoCompletaElSistema)
        {
            return Ok(new ExchangeRateSuggestionResponse
            {
                TipoCambio = null,
                Fecha = suggestion.RateDate,
                EsDeOtraFecha = suggestion.IsStale,
                Leyenda = string.Empty,
                TopeDelDia = null,
                LoCompletaElSistema = true,
            });
        }

        // "Ayuda invisible" (spec A5.7): el techo del dia lo dice el motor, nunca lo calcula la pantalla.
        var topeDelDia = await _resolver.GetInvoicingCeilingAsync(normalizedCurrency, effectiveDate, cancellationToken);

        return Ok(new ExchangeRateSuggestionResponse
        {
            TipoCambio = suggestion.Rate,
            Fecha = suggestion.RateDate,
            EsDeOtraFecha = suggestion.IsStale,
            Leyenda = BuildLeyenda(suggestion),
            TopeDelDia = topeDelDia,
            LoCompletaElSistema = false,
        });
    }

    /// <summary>
    /// TRABAJO 2 (boton "actualizar" de la tira del dolar, 2026-08-05, orden textual del dueño
    /// mirando el dashboard EN VIVO: "yo pondría un botón para actualizar"). Encola una
    /// sincronizacion on-demand del dolar (fire-and-forget, via <see cref="IExchangeRateResolver.RequestManualSyncAsync"/>)
    /// y responde SIEMPRE 202 sin esperar a que el job termine — nunca se le hace saber al usuario
    /// si el pedido estaba debounced (detalle tecnico) o si realmente encolo de nuevo: para el, el
    /// dolar "ya se esta buscando" en cualquiera de los dos casos.
    /// </summary>
    [HttpPost("refresh")]
    [RequirePermission(Permissions.ReservasView)]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        // MVP: el job de sincronizacion solo trae dolar (ver ExchangeRateSyncJob). El boton de la
        // tira tampoco pide moneda: refresca "el dolar", que es lo unico que la tira muestra.
        await _resolver.RequestManualSyncAsync("USD", cancellationToken);

        return Accepted(new { message = "Buscando el dólar de hoy. En unos segundos se actualiza." });
    }

    private static bool IsSupportedCurrency(string? currency) =>
        !string.IsNullOrWhiteSpace(currency)
        && (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(currency, "ARS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(currency, "PES", StringComparison.OrdinalIgnoreCase));

    private static bool IsPesos(string normalizedCurrency) =>
        normalizedCurrency is "ARS" or "PES";

    /// <summary>
    /// Arma el texto gris que va debajo del casillero (T-13: el front recibe el texto ya armado, no
    /// deduce fechas ni arma frases).
    ///
    /// <para><b>Textos EXACTOS de la spec firmada 2026-08-06 (tabla A6)</b>: <c>Dólar oficial del 6 de
    /// agosto.</c> / <c>Dólar Banco Nación del 5 de agosto.</c> — qué dólar es y de qué día, nada más.
    /// Dos cosas MURIERON en esta obra, las dos por decision firmada del dueño:</para>
    /// <list type="number">
    ///   <item>La muletilla <i>"Si ponés otro número, lo tomamos a mano."</i> (decision P1=A): es un
    ///   sermón sobre cómo funciona el sistema, y además se explica solo — apenas el usuario pisa el
    ///   número le aparece el renglón que le pregunta de dónde lo sacó.</item>
    ///   <item>La leyenda larga del modo de ensayo: en ese modo ya no hay casillero que leyendear, así
    ///   que la funcion ni se llama (ver <c>ExchangeRateSuggestion.LoCompletaElSistema</c>).</item>
    /// </list>
    ///
    /// <para>La variante "de hoy (…)" tambien se fue: la spec pide UNA sola forma, "del {dia}", valga
    /// para hoy o para una fecha pasada. Menos ramas, menos formas de decir lo mismo (P-16).</para>
    ///
    /// <para><b>Qué dólar es</b>: "Dólar oficial" cuando el dato lo publicó el organismo fiscal; para
    /// cualquier otra fuente (el respaldo de Banco Nación y las APIs públicas que lo replican) el
    /// nombre honesto es "Dólar Banco Nación" — es el mismo nombre que ya usa la tira del inicio.</para>
    /// </summary>
    private static string BuildLeyenda(ExchangeRateSuggestion suggestion)
    {
        var fechaEnCastellano = FormatFechaEnCastellano(suggestion.RateDate);
        var sustantivo = suggestion.Source == ExchangeRateSource.AfipOficial
            ? "Dólar oficial"
            : "Dólar Banco Nación";

        return $"{sustantivo} del {fechaEnCastellano}.";
    }

    private static readonly CultureInfo SpanishArgentina = CultureInfo.GetCultureInfo("es-AR");

    private static string FormatFechaEnCastellano(DateOnly date) =>
        date.ToDateTime(TimeOnly.MinValue).ToString("d 'de' MMMM", SpanishArgentina);
}
