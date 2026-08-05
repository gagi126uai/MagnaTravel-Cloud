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

        return Ok(new ExchangeRateSuggestionResponse
        {
            TipoCambio = suggestion.Rate,
            Fecha = suggestion.RateDate,
            EsDeOtraFecha = suggestion.IsStale,
            Leyenda = BuildLeyenda(suggestion, hoyArgentina),
        });
    }

    private static bool IsSupportedCurrency(string? currency) =>
        !string.IsNullOrWhiteSpace(currency)
        && (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(currency, "ARS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(currency, "PES", StringComparison.OrdinalIgnoreCase));

    private static bool IsPesos(string normalizedCurrency) =>
        normalizedCurrency is "ARS" or "PES";

    /// <summary>
    /// Arma el texto que va debajo del casillero (T-13: el front recibe el texto ya armado, no
    /// deduce fechas). Mismo tono que ya usa la pantalla de multas para el dolar BNA.
    ///
    /// <para><b>FIX (detalle #6, revision post-implementacion 2026-08-05)</b>: la version anterior
    /// tenia DOS bugs de honestidad:
    /// <list type="number">
    ///   <item>Decia "Dólar oficial" SIEMPRE, aunque el dato viniera del respaldo de Banco Nación
    ///   (<c>ExchangeRateSource.BNA_Minorista</c>) — un dato que no salio de ARCA no es "oficial".
    ///   Ahora el sustantivo depende de <see cref="ExchangeRateSuggestion.Source"/>.</item>
    ///   <item>Decia "de hoy" para CUALQUIER match exacto (<c>IsStale=false</c>), pero un match
    ///   exacto tambien ocurre cuando el usuario pide una fecha PASADA y esa fecha puntual SI tiene
    ///   fila (ej. corrigiendo una factura de la semana pasada) — "de hoy" ahi es directamente
    ///   falso. Ahora se compara <c>RateDate</c> contra el HOY real (Argentina), no contra
    ///   <c>IsStale</c>.</item>
    /// </list></para>
    /// </summary>
    private static string BuildLeyenda(ExchangeRateSuggestion suggestion, DateOnly hoyArgentina)
    {
        var fechaEnCastellano = FormatFechaEnCastellano(suggestion.RateDate);
        var sustantivo = suggestion.Source == ExchangeRateSource.AfipOficial
            ? "Dólar oficial"
            : "Dólar Banco Nación";
        bool esHoy = suggestion.RateDate == hoyArgentina;

        return esHoy
            ? $"{sustantivo} de hoy ({fechaEnCastellano}). Si ponés otro número, lo tomamos a mano."
            : $"{sustantivo} del {fechaEnCastellano}. Si ponés otro número, lo tomamos a mano.";
    }

    private static readonly CultureInfo SpanishArgentina = CultureInfo.GetCultureInfo("es-AR");

    private static string FormatFechaEnCastellano(DateOnly date) =>
        date.ToDateTime(TimeOnly.MinValue).ToString("d 'de' MMMM", SpanishArgentina);
}
