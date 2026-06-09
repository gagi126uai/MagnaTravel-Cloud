namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-021 (multimoneda por reserva, 2026-06-08): catalogo de monedas del dominio de
/// reservas y pagos. Es la UNICA fuente de verdad sobre "que monedas opera la plata
/// de una reserva" (costo, venta, saldo, cobros y pagos a proveedor).
///
/// <para><b>Por que existe y no se reusa <c>ArcaCurrencyMapper</c></b>: el dominio de
/// reservas habla codigos ISO 4217 ("ARS"/"USD"); <c>ArcaCurrencyMapper</c> habla el
/// catalogo de ARCA ("PES"/"DOL") para el XML SOAP de facturacion. Ambos soportan
/// exactamente las mismas dos monedas hoy, pero son ejes distintos: el saldo de una
/// reserva no debe acoplarse al detalle fiscal. La validacion cruzada con ARCA sigue
/// viviendo en el boundary de facturacion (<c>ArcaCurrencyMapper.IsSupported</c>), no aca.</para>
///
/// <para><b>Convencion del tipo de cambio (ADR-021 §2.2bis)</b>: cuando un pago cruza
/// moneda, el <c>ExchangeRate</c> se expresa SIEMPRE como <b>unidades de ARS por 1 USD</b>
/// (ej. 1 USD = 1000 ARS -> ExchangeRate = 1000.000000). Es la misma orientacion que
/// <c>Invoice.MonCotiz</c> para facturas en USD, asi el dato queda consistente con
/// facturacion y reconstruible por el contador. Esta clase NO hace la conversion (eso es
/// Capa 2 / calculator); deja la convencion documentada en el lugar canonico del catalogo.</para>
///
/// <para><b>Sumar una moneda futura</b> (EUR/BRL) es agregar una constante aca + la linea
/// en <c>ArcaCurrencyMapper</c> + homologacion ARCA. Fuera del alcance de ADR-021 (solo ARS/USD).</para>
/// </summary>
public static class Monedas
{
    public const string ARS = "ARS";
    public const string USD = "USD";

    /// <summary>Monedas que el sistema opera hoy. Orden estable (ARS primero = moneda por defecto).</summary>
    public static readonly IReadOnlyList<string> Soportadas = new[] { ARS, USD };

    /// <summary>
    /// True si <paramref name="iso"/> es una moneda soportada. Tolera capitalizacion
    /// ("usd"/"USD") porque el dato puede venir del front o de un import en cualquier caja.
    /// </summary>
    public static bool EsSoportada(string? iso) =>
        !string.IsNullOrWhiteSpace(iso) &&
        Soportadas.Any(moneda => string.Equals(moneda, iso, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Normaliza una moneda a su forma canonica en mayuscula. <c>null</c>/vacio -> ARS
    /// (es la regla legacy: una fila sin moneda se lee como pesos, ADR-021 §2.2).
    /// Lo usa el calculator (Capa 2) para agrupar servicios y pagos sin moneda explicita.
    /// </summary>
    public static string Normalizar(string? iso) =>
        string.IsNullOrWhiteSpace(iso) ? ARS : iso.Trim().ToUpperInvariant();
}
