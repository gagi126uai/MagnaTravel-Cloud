namespace TravelApi.Domain.Helpers;

/// <summary>
/// FC1.3.F2.5 (multimoneda, 2026-05-28): fuente de verdad UNICA del catalogo de monedas
/// que el sistema sabe emitir al ARCA en una Nota de Credito parcial.
///
/// <para><b>Por que existe esta clase</b>: el codigo de moneda que usa el negocio es ISO 4217
/// ("USD", "ARS", ...), pero ARCA NO usa ISO — el dolar es "DOL" para ARCA y el peso es "PES".
/// Mandar "USD" en el campo &lt;MonId&gt; del XML SOAP haria rebotar el comprobante. Hace falta
/// un mapeo explicito.</para>
///
/// <para><b>Por que un helper compartido y no el mapeo duplicado</b>: antes de F2.5 este mapeo
/// vivia <c>private static</c> dentro de <c>InvoiceService</c>, y ademas habia un guard duro en
/// <c>BookingCancellationService</c> que rechazaba "todo lo que no sea ARS". Eran DOS fuentes de
/// verdad sobre "que monedas soportamos". Si una sumaba EUR y la otra no, el guard del Booking
/// abortaba algo que el InvoiceService si sabia emitir (o peor, al reves). Centralizar el catalogo
/// aca elimina ese riesgo de drift: agregar una moneda nueva es una sola linea, y los dos lugares
/// la ven a la vez.</para>
///
/// <para><b>Por que devuelve null en vez de tirar</b>: cada caller decide como tratar una moneda
/// no soportada segun su contexto (el guard del Booking aborta temprano con audit; el job de
/// emision marca la NC como Failed). El mapper no tiene contexto para loggear ni para elegir la
/// excepcion correcta — solo responde "esta moneda la se mapear: si/no".</para>
///
/// <para><b>Mantenimiento</b>: el catalogo arranca chico (solo ARS y USD) porque es lo unico que
/// la agencia factura hoy. Sumar EUR/BRL cuando el negocio lo pida es agregar una linea en
/// <see cref="TryMap"/> + un test en <c>ArcaCurrencyMapperTests</c>. Antes de habilitar una moneda
/// nueva en produccion hay que homologar contra ARCA que el codigo del catalogo sea el correcto.</para>
/// </summary>
public static class ArcaCurrencyMapper
{
    /// <summary>
    /// Traduce un codigo de moneda ISO 4217 ("USD", "ARS", ...) al codigo del catalogo de monedas
    /// de ARCA ("DOL", "PES", ...). Devuelve <c>null</c> si la moneda no esta soportada todavia.
    ///
    /// <para>OrdinalIgnoreCase: tolera "usd"/"USD" sin falsos negativos (el snapshot fiscal o el
    /// origen del dato podrian venir en minuscula).</para>
    /// </summary>
    /// <param name="isoCurrency">Codigo ISO 4217 del negocio. Puede venir en cualquier capitalizacion.</param>
    /// <returns>Codigo ARCA ("PES", "DOL") o <c>null</c> si no se soporta.</returns>
    public static string? TryMap(string? isoCurrency)
    {
        if (string.IsNullOrWhiteSpace(isoCurrency))
        {
            return null;
        }

        if (string.Equals(isoCurrency, "ARS", System.StringComparison.OrdinalIgnoreCase))
        {
            return "PES";
        }

        if (string.Equals(isoCurrency, "USD", System.StringComparison.OrdinalIgnoreCase))
        {
            return "DOL";
        }

        return null;
    }

    /// <summary>
    /// Azucar de lectura para los guards: <c>true</c> si <see cref="TryMap"/> sabe convertir la
    /// moneda a un codigo ARCA. Asi el caller que solo quiere validar (no necesita el codigo)
    /// se lee mas claro: <c>if (!ArcaCurrencyMapper.IsSupported(currency)) abortar();</c>
    /// </summary>
    public static bool IsSupported(string? isoCurrency) => TryMap(isoCurrency) is not null;

    /// <summary>
    /// FC1.3.F2.5 (fix m-2, 2026-05-28): valida que un codigo YA en formato ARCA ("PES", "DOL")
    /// sea uno de los que este mapper produce. Distinto de <see cref="IsSupported"/>: aquella
    /// recibe ISO ("USD") y pregunta "se mapear esto?"; esta recibe el codigo ARCA ("DOL") y
    /// pregunta "este codigo es uno de los que yo emito?".
    ///
    /// <para><b>Por que existe</b>: <c>CreateInvoiceRequest.MonId</c> es una prop publica sin
    /// validacion. Un caller equivocado podria setear el ISO ("USD") en vez del codigo ARCA
    /// ("DOL"); el SOAP lo mandaria literal y ARCA rebotaria el comprobante con un error opaco.
    /// El boundary del job (<c>AfipService.ProcessInvoiceJob</c>) usa esto para fallar controlado
    /// ANTES de POSTear, con un mensaje claro de que el codigo es invalido.</para>
    /// </summary>
    /// <param name="arcaCurrencyCode">Codigo de moneda en formato ARCA ("PES", "DOL").</param>
    public static bool IsValidArcaCurrencyCode(string? arcaCurrencyCode)
    {
        if (string.IsNullOrWhiteSpace(arcaCurrencyCode))
        {
            return false;
        }

        return string.Equals(arcaCurrencyCode, "PES", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(arcaCurrencyCode, "DOL", System.StringComparison.OrdinalIgnoreCase);
    }
}
