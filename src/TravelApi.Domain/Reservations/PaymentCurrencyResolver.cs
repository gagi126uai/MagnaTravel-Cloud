using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-021 Capa 4 (multimoneda + cobro cruzado, 2026-06-10): resuelve y VALIDA el bloque de moneda
/// de un pago (cliente o proveedor) antes de persistirlo. Es la unica fuente de verdad de las
/// validaciones §8.4/§8.5/§8.6 y de la formula de imputacion §2.2bis.
///
/// <para><b>Por que existe</b>: el registro de pago (PaymentService) no debe confiar en lo que manda
/// el front. Centralizar aca el "¿es cruzado?, ¿faltan datos de TC?, ¿cuanto baja del saldo?" deja una
/// sola implementacion testeable sin EF, igual que <see cref="ReservaMoneyCalculator"/> con el calculo.</para>
///
/// <para><b>Convencion del TC (§2.2bis, FIJA)</b>: <c>ExchangeRate</c> = unidades de ARS por 1 USD.
/// - Pago ARS imputado a saldo USD: <c>ImputedAmount[USD] = round2(Amount_ARS / ExchangeRate)</c>.
/// - Pago USD imputado a saldo ARS: <c>ImputedAmount[ARS] = round2(Amount_USD * ExchangeRate)</c>.
/// Como hoy solo hay ARS/USD no hace falta triangulacion.</para>
/// </summary>
public static class PaymentCurrencyResolver
{
    /// <summary>
    /// Resultado YA validado del bloque de moneda de un pago, listo para volcar a la entidad.
    /// Para un pago NO cruzado el bloque de TC queda en null y <see cref="ImputedCurrency"/> tambien
    /// (la entidad imputa su propia <see cref="Currency"/> = identico al legacy ARS).
    /// </summary>
    public sealed record Resolved(
        string Currency,
        string? ImputedCurrency,
        decimal? ExchangeRate,
        ExchangeRateSource? ExchangeRateSource,
        DateTime? ExchangeRateAt,
        decimal? ImputedAmount);

    /// <summary>
    /// Valida los campos de moneda/TC de un request de pago y devuelve el bloque listo para persistir.
    /// Lanza <see cref="ArgumentException"/> con mensaje en espanol si algo viola §8 (el caller lo
    /// traduce a 400). <paramref name="amount"/> es el monto real ya redondeado del pago.
    /// </summary>
    /// <param name="round">Funcion de redondeo de plata (se inyecta para reusar EconomicRulesHelper.RoundCurrency).</param>
    public static Resolved Resolve(
        decimal amount,
        string? rawCurrency,
        string? rawImputedCurrency,
        decimal? exchangeRate,
        int? exchangeRateSource,
        DateTime? exchangeRateAt,
        decimal? imputedAmount,
        Func<decimal, decimal> round)
    {
        // §8.4: la moneda real del pago debe ser soportada. null/vacio = ARS (caso legacy).
        string currency = Monedas.Normalizar(rawCurrency);
        if (!Monedas.EsSoportada(currency))
            throw new ArgumentException($"Moneda de pago no soportada: '{rawCurrency}'.");

        // ¿El front declaro una moneda de imputacion distinta? Si no la mando (null), el pago imputa
        // a su propia moneda = NO cruzado.
        bool imputedCurrencyProvided = !string.IsNullOrWhiteSpace(rawImputedCurrency);
        string? imputedCurrency = imputedCurrencyProvided ? Monedas.Normalizar(rawImputedCurrency) : null;

        if (imputedCurrency != null && !Monedas.EsSoportada(imputedCurrency))
            throw new ArgumentException($"Moneda de imputacion no soportada: '{rawImputedCurrency}'.");

        // ¿Es cruzado? Solo si la moneda de imputacion existe y DIFIERE de la moneda real del pago.
        bool isCrossCurrency = imputedCurrency != null && !string.Equals(imputedCurrency, currency, StringComparison.Ordinal);

        bool hasAnyExchangeData =
            exchangeRate.HasValue || exchangeRateSource.HasValue || exchangeRateAt.HasValue || imputedAmount.HasValue;

        if (!isCrossCurrency)
        {
            // §8.6: pago NO cruzado. El bloque de TC debe quedar en null: rechazar si vino algo de TC.
            if (hasAnyExchangeData)
                throw new ArgumentException(
                    "Un pago en la misma moneda del saldo no puede llevar tipo de cambio ni monto imputado.");

            // ImputedCurrency null => la entidad imputa su Amount sobre su propia Currency (legacy ARS = identico).
            return new Resolved(
                Currency: currency,
                ImputedCurrency: null,
                ExchangeRate: null,
                ExchangeRateSource: null,
                ExchangeRateAt: null,
                ImputedAmount: null);
        }

        // ----- A partir de aca el pago ES cruzado: §8.5 exige TC + fuente + fecha + equivalente > 0 -----

        if (!exchangeRate.HasValue || exchangeRate.Value <= 0m)
            throw new ArgumentException("Un pago cruzado requiere un tipo de cambio mayor a cero.");

        if (!exchangeRateSource.HasValue)
            throw new ArgumentException("Un pago cruzado requiere indicar la fuente del tipo de cambio.");

        // El centinela Unset (0) no es una fuente valida: espejo del CHECK fiscal de FC1, no se persiste
        // un TC "sin fuente elegida".
        if (!Enum.IsDefined(typeof(ExchangeRateSource), exchangeRateSource.Value)
            || exchangeRateSource.Value == (int)Entities.ExchangeRateSource.Unset)
            throw new ArgumentException("La fuente del tipo de cambio es invalida.");

        if (!exchangeRateAt.HasValue)
            throw new ArgumentException("Un pago cruzado requiere la fecha del tipo de cambio.");

        // §2.2bis: el equivalente lo calcula el backend con la convencion fija (ARS por 1 USD). Si el
        // front mando un ImputedAmount, NO se confia en el: se recalcula y debe ser > 0. La caja
        // (Amount + Currency) es sagrada y no se toca.
        decimal computedImputedAmount = ComputeImputedAmount(amount, currency, imputedCurrency!, exchangeRate.Value, round);
        if (computedImputedAmount <= 0m)
            throw new ArgumentException("El monto imputado de un pago cruzado debe ser mayor a cero.");

        return new Resolved(
            Currency: currency,
            ImputedCurrency: imputedCurrency,
            ExchangeRate: exchangeRate.Value,
            ExchangeRateSource: (ExchangeRateSource)exchangeRateSource.Value,
            ExchangeRateAt: exchangeRateAt.Value,
            ImputedAmount: computedImputedAmount);
    }

    /// <summary>
    /// Convierte el monto real del pago a la moneda del saldo aplicando la convencion §2.2bis.
    /// Solo se llama en pagos cruzados ARS&lt;-&gt;USD (las dos unicas monedas de hoy).
    /// </summary>
    private static decimal ComputeImputedAmount(
        decimal amount, string currency, string imputedCurrency, decimal exchangeRate, Func<decimal, decimal> round)
    {
        // Pago ARS imputado a saldo USD: divido por el TC (ARS por 1 USD).
        if (string.Equals(currency, Monedas.ARS, StringComparison.Ordinal)
            && string.Equals(imputedCurrency, Monedas.USD, StringComparison.Ordinal))
        {
            return round(amount / exchangeRate);
        }

        // Pago USD imputado a saldo ARS: multiplico por el TC.
        if (string.Equals(currency, Monedas.USD, StringComparison.Ordinal)
            && string.Equals(imputedCurrency, Monedas.ARS, StringComparison.Ordinal))
        {
            return round(amount * exchangeRate);
        }

        // No deberia llegar aca con el catalogo actual (solo ARS/USD). Si en el futuro se agrega una
        // tercera moneda, este es el lugar donde se revisa la convencion (ADR-021 §2.2bis).
        throw new ArgumentException(
            $"No hay convencion de tipo de cambio definida para imputar {currency} a {imputedCurrency}.");
    }
}
