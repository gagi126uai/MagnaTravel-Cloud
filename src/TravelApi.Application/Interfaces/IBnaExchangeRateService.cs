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

    /// <summary>
    /// ADR-044 Fix B (2026-07-13): sugerencia de cotizacion del dolar vendedor BNA para una FECHA pasada, SOLO
    /// LECTURA de lo ya guardado (NO llama a Banco Nacion en vivo). La usa el modal de "corregir monto y moneda"
    /// para pre-escribir el tipo de cambio del dia en que el operador cobro.
    ///
    /// <para><b>LIMITACION IMPORTANTE (dato real del modelo)</b>: <c>BnaExchangeRateSnapshots</c> es un
    /// SINGLETON — guarda UNA sola fila con la ULTIMA cotizacion, NO una serie historica por fecha (cada fetch
    /// pisa la anterior con <c>ON CONFLICT (Id) DO UPDATE</c>). Por eso esta consulta solo puede ofrecer ese
    /// unico snapshot, y lo hace unicamente si es un dato razonable para la fecha pedida: su fecha de publicacion
    /// es &lt;= la fecha pedida y no mas de una ventana de dias hacia atras (cubre findes/feriados en los que el
    /// BNA no cotiza). Si el unico snapshot no cae en esa ventana (fecha pedida vieja, o el snapshot es mas nuevo
    /// que la fecha pedida) devuelve <c>null</c>: el modal cae a "escribilo a mano". NUNCA inventa un numero.</para>
    ///
    /// <para>Devuelve <c>null</c> tambien si no hay snapshot persistido, si la fecha guardada no se puede parsear,
    /// o si la cotizacion no es confiable (&lt;= 0).</para>
    /// </summary>
    /// <param name="requestedDate">Fecha (dia de calendario) para la que se quiere el dolar oficial.</param>
    Task<BnaRateForDateDto?> GetPersistedUsdSellerRateForDateAsync(
        DateOnly requestedDate, CancellationToken cancellationToken);
}

/// <summary>
/// ADR-044 Fix B (2026-07-13): respuesta minima de la sugerencia de TC por fecha, SIN internos. <see cref="Rate"/>
/// es el dolar vendedor BNA (ARS por 1 USD); <see cref="RateDate"/> es la fecha REAL del dato guardado (puede ser
/// unos dias anterior a la pedida, ej. un viernes cuando se pidio el domingo), para que el front pueda mostrar
/// "Dolar oficial del BNA del 04/07" con honestidad.
/// </summary>
public record BnaRateForDateDto(decimal Rate, DateOnly RateDate);
