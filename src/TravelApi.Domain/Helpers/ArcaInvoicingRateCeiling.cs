namespace TravelApi.Domain.Helpers;

/// <summary>
/// "Ayuda invisible del tipo de cambio" (spec firmada 2026-08-06, parte A5.7/A6): calcula el TECHO
/// del dia — el tipo de cambio mas alto que un comprobante en moneda extranjera puede declarar.
///
/// <para><b>La regla, en criollo</b>: cuando facturas en dolares tenes que declarar a cuanto vale el
/// dolar. El organismo no acepta cualquier numero: como maximo acepta la cotizacion oficial del dia
/// mas un peso. Si declaras uno mas alto, el comprobante REBOTA y el vendedor ve un error que no sabe
/// arreglar. Por eso el sistema calcula el techo y acomoda el numero solo (ver
/// <c>InvoiceService.ResolveExchangeRateSourceServerSideAsync</c>).</para>
///
/// <para><b>Por que vive en el Dominio y no en el front</b> (reglas T-13 y A5.7 de la spec): el margen
/// de $1 es una regla FISCAL, no un detalle de pantalla. La pantalla recibe el techo ya calculado y
/// jamas le suma un peso a nada por su cuenta — si la regla cambia, cambia UNA sola linea, aca.</para>
/// </summary>
public static class ArcaInvoicingRateCeiling
{
    /// <summary>
    /// Margen que el organismo tolera por encima de la cotizacion oficial del dia, en pesos por unidad
    /// de moneda extranjera. Es la validacion de comprobantes que exige
    /// "cotizacion declarada &lt;= cotizacion oficial del dia + 1" (manual tecnico WSFEv1 v4.7).
    /// </summary>
    public const decimal ToleranceAboveOfficialRate = 1m;

    /// <summary>
    /// Techo del dia a partir de la cotizacion oficial de ese dia.
    /// </summary>
    /// <param name="officialRate">Cotizacion oficial publicada por el organismo para la fecha del comprobante.</param>
    public static decimal FromOfficialRate(decimal officialRate) => officialRate + ToleranceAboveOfficialRate;

    /// <summary>
    /// <c>true</c> si el tipo de cambio que quiere declarar el usuario no entra en el comprobante.
    /// Comparacion decimal EXACTA, sin tolerancia extra: el margen ya esta contemplado en el techo.
    /// </summary>
    public static bool ExceedsCeiling(decimal declaredRate, decimal ceiling) => declaredRate > ceiling;

    /// <summary>
    /// Piso de cordura (hallazgo de seguridad N1, 2026-08-06): una cotizacion oficial de 1 o menos no es
    /// una cotizacion, es un dato corrupto (una moneda extranjera no vale un peso). Con un dato asi el
    /// techo daria 2 y el motor le bajaria el tipo de cambio de una factura legitima a 2 pesos por
    /// dolar, en un comprobante que despues no se puede deshacer. Mismo criterio que ya usa el guard de
    /// emision de <c>InvoiceService</c>.
    /// </summary>
    public static bool IsUsableOfficialRate(decimal officialRate) => officialRate > 1m;

    /// <summary>
    /// Ventana de fechas de la que puede salir la cotizacion oficial que arma el techo (hallazgo de
    /// seguridad B2, 2026-08-06): la fecha del comprobante o el dia habil anterior. Nada mas viejo.
    ///
    /// <para><b>Por que tan corta</b>: el techo BAJA el numero que el usuario declaro. Si la libreta de
    /// cotizaciones viene desactualizada (el organismo no contesto hace dias) y aceptaramos una
    /// cotizacion vieja, el motor acomodaria un tipo de cambio legitimo hacia ABAJO usando un dolar de
    /// la semana pasada, en un comprobante irreversible. Prefirimos quedarnos sin techo: sin techo el
    /// sistema no acomoda nada y el usuario emite como siempre (a lo sumo el comprobante rebota, que es
    /// reversible; una factura mal valuada no).</para>
    ///
    /// <para><b>Limitacion honesta</b>: "dia habil" aca es "no sabado ni domingo". El sistema NO tiene
    /// calendario de feriados. Si el dia habil anterior fue feriado, no hay techo ese dia y se cae al
    /// camino manual de siempre — el error apunta al lado seguro.</para>
    /// </summary>
    public static DateOnly EarliestAcceptableQuoteDate(DateOnly invoiceDate) => invoiceDate.DayOfWeek switch
    {
        // El lunes mira al viernes; el domingo y el sabado tambien (el ultimo dia con cotizacion).
        DayOfWeek.Monday => invoiceDate.AddDays(-3),
        DayOfWeek.Sunday => invoiceDate.AddDays(-2),
        _ => invoiceDate.AddDays(-1),
    };
}
