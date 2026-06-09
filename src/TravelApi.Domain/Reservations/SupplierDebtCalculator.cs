using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-021 §15.4 (multimoneda, eje proveedor, 2026-06-08): calculador PURO de la deuda de la agencia
/// HACIA un proveedor/operador, SEPARADA por moneda. Es el espejo de <see cref="ReservaMoneyCalculator"/>
/// del lado cliente: en vez de "cuanto nos debe el cliente" calcula "cuanto le debemos al operador".
///
/// <para>La deuda de cada moneda = (compras CONFIRMADAS en esa moneda) - (pagos imputados a esa moneda).
/// Las compras vienen en la moneda del servicio; los pagos en la moneda a la que se IMPUTAN (el
/// equivalente convertido si el pago cruzo moneda). El saldo a favor de una moneda (sobrepago al
/// operador) NO compensa la deuda de otra.</para>
///
/// <para><b>Regla de oro (regresion)</b>: un proveedor 100% en una sola moneda (caso legacy ARS) da
/// exactamente la misma deuda escalar que la cuenta vieja (compras - pagos, sin separar). Funcion pura:
/// recibe los montos ya materializados, no toca EF ni base de datos.</para>
/// </summary>
public static class SupplierDebtCalculator
{
    /// <summary>
    /// Una compra confirmada que aporta a la deuda: su monto (NetCost) y su moneda (null = ARS).
    /// </summary>
    public readonly record struct ConfirmedPurchase(string? Currency, decimal NetCost);

    /// <summary>
    /// Un pago al proveedor: monto real de caja, su moneda real, y (si cruzo) la moneda imputada y el
    /// equivalente imputado. Para imputar la deuda se usa <c>ImputedAmount ?? Amount</c> sobre
    /// <c>ImputedCurrency ?? Currency</c> (idem eje cliente).
    /// </summary>
    public readonly record struct SupplierPaymentInput(
        decimal Amount, string? Currency, string? ImputedCurrency, decimal? ImputedAmount);

    /// <summary>
    /// Calcula la deuda por moneda. Devuelve una linea por cada moneda presente en compras o pagos.
    /// </summary>
    public static IReadOnlyDictionary<string, SupplierDebtLine> Calculate(
        IEnumerable<ConfirmedPurchase> confirmedPurchases,
        IEnumerable<SupplierPaymentInput> payments)
    {
        ArgumentNullException.ThrowIfNull(confirmedPurchases);
        ArgumentNullException.ThrowIfNull(payments);

        var purchasesByCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var paidByCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var purchase in confirmedPurchases)
        {
            string currency = Monedas.Normalizar(purchase.Currency);
            purchasesByCurrency.TryGetValue(currency, out var current);
            purchasesByCurrency[currency] = current + purchase.NetCost;
        }

        foreach (var payment in payments)
        {
            // Imputa el equivalente a la moneda de la deuda (ImputedCurrency); si no cruzo, su propia
            // moneda y su Amount. Para el caso legacy (sin moneda ni imputacion) es ARS + Amount.
            string imputedCurrency = Monedas.Normalizar(payment.ImputedCurrency ?? payment.Currency);
            decimal imputedAmount = payment.ImputedAmount ?? payment.Amount;
            paidByCurrency.TryGetValue(imputedCurrency, out var current);
            paidByCurrency[imputedCurrency] = current + imputedAmount;
        }

        // Union de monedas presentes en compras o pagos.
        var currencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in purchasesByCurrency.Keys) currencies.Add(key);
        foreach (var key in paidByCurrency.Keys) currencies.Add(key);

        var lines = new Dictionary<string, SupplierDebtLine>(StringComparer.Ordinal);
        foreach (var currency in currencies)
        {
            purchasesByCurrency.TryGetValue(currency, out var confirmed);
            paidByCurrency.TryGetValue(currency, out var paid);
            lines[currency] = new SupplierDebtLine(currency, confirmed, paid);
        }
        return lines;
    }

    /// <summary>
    /// Deriva el escalar surrogate de <c>Supplier.CurrentBalance</c> (semaforo de "tiene deuda
    /// pendiente", no monto), espejo de <see cref="ReservaMoneySummary.Balance"/>:
    /// <list type="bullet">
    /// <item><b>Mono-moneda</b>: la deuda cruda de esa unica moneda (puede ser negativa = sobrepago)
    ///   -> identico a la cuenta legacy del proveedor, incluso el sobrepago.</item>
    /// <item><b>Multimoneda</b>: <c>sum(max(0, linea.Balance))</c> — el sobrepago de una moneda no
    ///   compensa la deuda de otra.</item>
    /// </list>
    /// </summary>
    public static decimal ToSurrogateBalance(IReadOnlyDictionary<string, SupplierDebtLine> porMoneda)
    {
        ArgumentNullException.ThrowIfNull(porMoneda);
        if (porMoneda.Count == 0) return 0m;

        if (porMoneda.Count == 1)
        {
            foreach (var line in porMoneda.Values)
                return line.Balance; // crudo (puede ser negativo) = identico a legacy mono-moneda
        }

        decimal surrogate = 0m;
        foreach (var line in porMoneda.Values)
        {
            if (line.Balance > 0m) surrogate += line.Balance;
        }
        return surrogate;
    }
}

/// <summary>
/// ADR-021 §15.3: deuda con un proveedor en UNA moneda. Value object inmutable. La proyeccion
/// persistida es la tabla hija <c>SupplierBalanceByCurrency</c>.
/// </summary>
public sealed class SupplierDebtLine
{
    public string Currency { get; }

    /// <summary>Compras confirmadas (NetCost que cuenta como deuda) en esta moneda.</summary>
    public decimal ConfirmedPurchases { get; }

    /// <summary>Pagado al proveedor imputado a esta moneda.</summary>
    public decimal TotalPaid { get; }

    /// <summary>Deuda de esta moneda = <see cref="ConfirmedPurchases"/> - <see cref="TotalPaid"/> (puede ser negativa).</summary>
    public decimal Balance { get; }

    public SupplierDebtLine(string currency, decimal confirmedPurchases, decimal totalPaid)
    {
        Currency = currency;
        ConfirmedPurchases = confirmedPurchases;
        TotalPaid = totalPaid;
        Balance = confirmedPurchases - totalPaid;
    }
}
