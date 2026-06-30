using System;
using System.Collections.Generic;
using System.Linq;
using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Pasos B/C "cuenta del operador" (2026-06-29): una linea del bloque "Circuito de cancelacion" del extracto
/// del proveedor. Es derivada-en-lectura (no se persiste): sale del estado de las cancelaciones del operador.
///
/// <para>A diferencia de las lineas de CAJA (<see cref="SupplierAccountStatementInputLine"/>), estas NO entran
/// al running balance de caja. <see cref="Amount"/> siempre es POSITIVO (un cargo +): tanto la multa retenida
/// como el reembolso recibido SUMAN al lado economico (neutralizan el pago negativo que dejo la anulacion).</para>
/// </summary>
public readonly record struct SupplierCircuitLine(
    DateTime Date,
    string Kind,
    string Description,
    string? DocumentRef,
    string Currency,
    decimal Amount,
    Guid? SourcePublicId);

/// <summary>
/// Resultado del calculo de los DOS numeros de la cuenta del operador, para UNA moneda.
/// </summary>
public sealed class SupplierAccountReconciliationCurrencyBlock
{
    public string Currency { get; }

    /// <summary>Saldo de cierre del extracto de CAJA (compras - pagos). Igual a <c>SupplierBalanceByCurrency.Balance</c>.</summary>
    public decimal CashClosingBalance { get; }

    /// <summary>Multa retenida por el operador (pass-through confirmada), sumada en esta moneda.</summary>
    public decimal PenaltyRetainedTotal { get; }

    /// <summary>Reembolso ya recibido del operador, sumado en esta moneda.</summary>
    public decimal RefundReceivedTotal { get; }

    /// <summary>Total del bloque circuito = multa retenida + reembolso recibido (ambos +).</summary>
    public decimal CircuitTotal { get; }

    /// <summary>Saldo economico = caja + circuito. Derivado, SOLO para el header (no toca el running balance de caja).</summary>
    public decimal EconomicClosingBalance { get; }

    /// <summary>"Me tiene que devolver" (Y) = reembolso por cobrar. Fuente autoritativa: el calculo del receivable.</summary>
    public decimal TheyOweMe { get; }

    /// <summary>"Le debo" (X) = max(0, EconomicClosingBalance + Y). Nunca se netea contra Y.</summary>
    public decimal ITheyOwe { get; }

    /// <summary>Saldo a favor CONSUMIBLE (prepago) = max(0, -(EconomicClosingBalance + Y)).</summary>
    public decimal Prepayment { get; }

    /// <summary>Las lineas del bloque circuito de esta moneda (orden cronologico).</summary>
    public IReadOnlyList<SupplierCircuitLine> CircuitLines { get; }

    public SupplierAccountReconciliationCurrencyBlock(
        string currency,
        decimal cashClosingBalance,
        decimal penaltyRetainedTotal,
        decimal refundReceivedTotal,
        decimal economicClosingBalance,
        decimal theyOweMe,
        decimal iTheyOwe,
        decimal prepayment,
        IReadOnlyList<SupplierCircuitLine> circuitLines)
    {
        Currency = currency;
        CashClosingBalance = cashClosingBalance;
        PenaltyRetainedTotal = penaltyRetainedTotal;
        RefundReceivedTotal = refundReceivedTotal;
        CircuitTotal = penaltyRetainedTotal + refundReceivedTotal;
        EconomicClosingBalance = economicClosingBalance;
        TheyOweMe = theyOweMe;
        ITheyOwe = iTheyOwe;
        Prepayment = prepayment;
        CircuitLines = circuitLines;
    }
}

/// <summary>
/// Resultado completo: un bloque por cada moneda presente.
/// </summary>
public sealed class SupplierAccountReconciliation
{
    public IReadOnlyList<SupplierAccountReconciliationCurrencyBlock> Currencies { get; }

    public SupplierAccountReconciliation(IReadOnlyList<SupplierAccountReconciliationCurrencyBlock> currencies)
    {
        Currencies = currencies;
    }
}

/// <summary>
/// Calculador PURO de los dos numeros de la "cuenta del operador" (Pasos B/C, 2026-06-29). Espejo del
/// <see cref="SupplierAccountStatementBuilder"/> pero del lado ECONOMICO: en vez de mostrar solo la caja
/// (compras - pagos), combina la caja con el "Circuito de cancelacion" (multa retenida + reembolso recibido)
/// y con el receivable "me tiene que devolver" (Y), para producir, por moneda, los dos numeros que el dueño ve:
/// <b>"Le debo X"</b> y <b>"Me tiene que devolver Y"</b>.
///
/// <para><b>Por que existe / la formula</b> (verificada a mano en el diseño rev 2, §2.2): por moneda,
/// <c>Econ = CashClosing + (MultaRetenida + ReembolsoRecibido)</c>;
/// <c>X = max(0, Econ + Y)</c>; <c>Prepago = max(0, -(Econ + Y))</c>. X e Y NUNCA se netean entre si: pueden
/// convivir "Le debo 500" y "Me tiene que devolver 700" en la misma cuenta. El receivable Y YA nace neto de
/// multa (el RefundCap se calcula como pagado - multa), asi que la "Multa retenida" del bloque circuito es la
/// contrapartida de visualizacion del pago negativo, NO un segundo descuento.</para>
///
/// <para><b>Invariante estructural</b> (vale para cualquier dato, lo garantiza la formula):
/// <c>X - Prepago == Econ + Y</c>, con <c>X &gt;= 0</c>, <c>Prepago &gt;= 0</c> y <c>X * Prepago == 0</c>
/// (nunca deuda y saldo a favor positivos en la misma moneda). Asi el calculador nunca produce un estado
/// imposible. Es puro y se testea sin DB.</para>
///
/// <para><b>Que NO hace</b>: no toca EF/BD, no enmascara (el llamador aplica el masking see_cost al mapear),
/// no decide el scope de las fuentes (eso lo arma el llamador de infraestructura: la caja en vivo, la multa y
/// el reembolso sobre las cancelaciones del operador, y el receivable Y por moneda).</para>
/// </summary>
public static class SupplierAccountReconciliationBuilder
{
    /// <summary>
    /// Combina, por moneda, el saldo de caja, las lineas del circuito (multa retenida + reembolso recibido) y
    /// el receivable Y, y deriva X / Prepago. La union de monedas son todas las que aparezcan en cualquiera de
    /// las tres fuentes.
    /// </summary>
    /// <param name="cashClosingByCurrency">Saldo de cierre de CAJA por moneda (== SupplierBalanceByCurrency.Balance).</param>
    /// <param name="circuitLines">Lineas del circuito (PenaltyRetained + RefundReceived), todas con Amount positivo.</param>
    /// <param name="receivableByCurrency">"Me tiene que devolver" (Y) por moneda, ya en positivo.</param>
    public static SupplierAccountReconciliation Build(
        IReadOnlyDictionary<string, decimal> cashClosingByCurrency,
        IEnumerable<SupplierCircuitLine> circuitLines,
        IReadOnlyDictionary<string, decimal> receivableByCurrency)
    {
        ArgumentNullException.ThrowIfNull(cashClosingByCurrency);
        ArgumentNullException.ThrowIfNull(circuitLines);
        ArgumentNullException.ThrowIfNull(receivableByCurrency);

        // Agrupamos las lineas del circuito por moneda, preservando el orden de aparicion (para mostrarlas
        // cronologicamente tal como vinieron del llamador, que ya las ordeno).
        var circuitByCurrency = new Dictionary<string, List<SupplierCircuitLine>>(StringComparer.Ordinal);
        foreach (var line in circuitLines)
        {
            var ccy = Monedas.Normalizar(line.Currency);
            if (!circuitByCurrency.TryGetValue(ccy, out var bucket))
            {
                bucket = new List<SupplierCircuitLine>();
                circuitByCurrency[ccy] = bucket;
            }
            bucket.Add(line);
        }

        // Union de todas las monedas presentes en cualquiera de las tres fuentes.
        var currencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ccy in cashClosingByCurrency.Keys) currencies.Add(Monedas.Normalizar(ccy));
        foreach (var ccy in receivableByCurrency.Keys) currencies.Add(Monedas.Normalizar(ccy));
        foreach (var ccy in circuitByCurrency.Keys) currencies.Add(ccy);

        var blocks = new List<SupplierAccountReconciliationCurrencyBlock>();

        // Orden alfabetico estable entre bloques (ARS antes que USD), coherente con el extracto de caja.
        foreach (var currency in currencies.OrderBy(c => c, StringComparer.Ordinal))
        {
            decimal cashClosing = SumByNormalizedCurrency(cashClosingByCurrency, currency);
            decimal receivableY = SumByNormalizedCurrency(receivableByCurrency, currency);

            var lines = circuitByCurrency.TryGetValue(currency, out var bucket)
                ? bucket
                : new List<SupplierCircuitLine>();

            decimal penaltyRetained = lines
                .Where(l => l.Kind == SupplierAccountStatementLineKinds.PenaltyRetained)
                .Sum(l => l.Amount);
            decimal refundReceived = lines
                .Where(l => l.Kind == SupplierAccountStatementLineKinds.RefundReceived)
                .Sum(l => l.Amount);

            penaltyRetained = Round(penaltyRetained);
            refundReceived = Round(refundReceived);
            cashClosing = Round(cashClosing);
            receivableY = Round(receivableY);

            decimal economic = Round(cashClosing + penaltyRetained + refundReceived);

            // X e Y nunca se netean entre si. Prepago es el saldo a favor consumible (caja negativa que NO es
            // un reembolso por cobrar). Por construccion, X y Prepago no pueden ser ambos positivos.
            decimal econPlusY = Round(economic + receivableY);
            decimal iTheyOwe = econPlusY > 0m ? econPlusY : 0m;
            decimal prepayment = econPlusY < 0m ? Round(-econPlusY) : 0m;

            blocks.Add(new SupplierAccountReconciliationCurrencyBlock(
                currency: currency,
                cashClosingBalance: cashClosing,
                penaltyRetainedTotal: penaltyRetained,
                refundReceivedTotal: refundReceived,
                economicClosingBalance: economic,
                theyOweMe: receivableY,
                iTheyOwe: iTheyOwe,
                prepayment: prepayment,
                circuitLines: lines));
        }

        return new SupplierAccountReconciliation(blocks);
    }

    /// <summary>Suma los valores de un diccionario cuya clave normalizada coincide con la moneda buscada.</summary>
    private static decimal SumByNormalizedCurrency(IReadOnlyDictionary<string, decimal> source, string normalizedCurrency)
    {
        decimal total = 0m;
        foreach (var kvp in source)
        {
            if (Monedas.Normalizar(kvp.Key) == normalizedCurrency)
                total += kvp.Value;
        }
        return total;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
