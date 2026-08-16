using System;
using System.Collections.Generic;
using System.Linq;
using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-022 GAP C (2026-08-16): compara, POR MONEDA, dos numeros de una misma cancelacion que deberian
/// coincidir pero se escriben por caminos SEPARADOS (dos puertas para el mismo hecho economico):
///
/// <list type="bullet">
///   <item><b>Derivado</b>: la suma de <see cref="BookingCancellationLine.ReceivedRefundAmount"/> de la
///   cancelacion. Es el numero que el extracto del proveedor le muestra al usuario como "Reembolso recibido"
///   (<see cref="TravelApi.Domain.Reservations.SupplierAccountStatementLineKinds.RefundReceived"/>). Lo
///   mantiene <c>OperatorRefundService</c> cada vez que se aloca/desaloca un reembolso a esta cancelacion.</item>
///   <item><b>Caja</b>: la suma de los <see cref="CashLedgerEntry"/> VIGENTES (<c>SourceType=OperatorRefund</c>,
///   ni reversados ni reversas) de los reembolsos cuya plata entro por esta cancelacion. Si alguien edita o
///   borra a mano ese movimiento desde Tesoreria (el <c>ManualCashMovement</c> asociado al ingreso), el
///   asiento se revierte pero la cancelacion sigue mostrando el reembolso como "recibido": las dos cuentas
///   dejan de coincidir sin que nadie se entere, hasta que este calculo lo detecta.</item>
/// </list>
///
/// <para><b>Es un calculador PURO</b> (no toca EF/BD): recibe los dos totales por moneda YA CALCULADOS por el
/// job de infraestructura (<c>CashLedgerRefundReconciliationJob</c>) y solo decide si difieren. Asi la regla
/// de "que es una divergencia real" se puede testear sin base de datos.</para>
/// </summary>
public static class CashLedgerRefundReconciliationCalculator
{
    /// <summary>
    /// Tolerancia de redondeo (1 centavo) para no disparar un aviso por una diferencia de centavos que viene
    /// de un redondeo intermedio, no de un dato incoherente.
    /// </summary>
    private const decimal ToleranceCents = 0.01m;

    /// <summary>
    /// Encuentra las monedas donde el total DERIVADO (extracto) y el total de CAJA (libro) no coinciden.
    /// Compara la union de monedas presentes en cualquiera de los dos diccionarios (si una moneda solo
    /// aparece de un lado, el otro lado vale 0 — por ejemplo, un reembolso cuyo asiento fue revertido del
    /// todo no aporta nada a "Caja" pero la cancelacion lo sigue mostrando como recibido en "Derivado").
    /// </summary>
    public static IReadOnlyList<CashLedgerRefundDivergence> FindDivergences(
        IReadOnlyDictionary<string, decimal> derivedReceivedByCurrency,
        IReadOnlyDictionary<string, decimal> liveLedgerByCurrency)
    {
        ArgumentNullException.ThrowIfNull(derivedReceivedByCurrency);
        ArgumentNullException.ThrowIfNull(liveLedgerByCurrency);

        var currencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ccy in derivedReceivedByCurrency.Keys) currencies.Add(Monedas.Normalizar(ccy));
        foreach (var ccy in liveLedgerByCurrency.Keys) currencies.Add(Monedas.Normalizar(ccy));

        var divergences = new List<CashLedgerRefundDivergence>();
        foreach (var currency in currencies.OrderBy(c => c, StringComparer.Ordinal))
        {
            var derived = Round(SumByNormalizedCurrency(derivedReceivedByCurrency, currency));
            var ledger = Round(SumByNormalizedCurrency(liveLedgerByCurrency, currency));

            if (Math.Abs(derived - ledger) <= ToleranceCents)
                continue; // coinciden (dentro de tolerancia de centavos): no es una divergencia real.

            divergences.Add(new CashLedgerRefundDivergence(currency, derived, ledger));
        }

        return divergences;
    }

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

/// <summary>
/// Una moneda donde el extracto (derivado de las lineas de cancelacion) y el Libro de Caja no cierran.
/// <see cref="Delta"/> positivo = el extracto muestra MAS plata recibida que la que hoy esta vigente en caja
/// (el caso tipico: un asiento se revirtio a mano y la cancelacion no se entero).
/// </summary>
public sealed record CashLedgerRefundDivergence(string Currency, decimal DerivedAmount, decimal LedgerAmount)
{
    public decimal Delta => DerivedAmount - LedgerAmount;
}
