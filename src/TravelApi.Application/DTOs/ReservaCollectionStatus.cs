namespace TravelApi.Application.DTOs;

/// <summary>
/// ADR-033 (E7/A5, 2026-06-16): ESTADO DE COBRO de una reserva, DERIVADO del saldo POR MONEDA. NO se persiste
/// (no hay columna en BD): es la lectura del saldo que ya vive por moneda en <c>ReservaMoneyByCurrency</c>.
///
/// <para>Es un eje INDEPENDIENTE del estado operativo (<c>Reserva.Status</c>) y de la facturacion (existencia
/// de factura con CAE). Una reserva Finalizada puede estar "ConDeuda"; una En gestion puede estar "Saldada".</para>
///
/// <para>Por que por moneda y no por el escalar: el <c>Balance</c> escalar suma ARS + USD, lo que es
/// semanticamente impuro. Una reserva que debe USD y tiene saldo a favor ARS daria escalar ~0 ("Saldada"),
/// cuando en realidad esta "ConDeuda" en USD. La verdad es por moneda.</para>
/// </summary>
public static class ReservaCollectionStatus
{
    /// <summary>Alguna moneda tiene deuda (Balance &gt; 0). Gana sobre SaldoAFavor cuando hay ambas.</summary>
    public const string WithDebt = "ConDeuda";

    /// <summary>No hay deuda en ninguna moneda y alguna tiene sobrepago (Balance &lt; 0).</summary>
    public const string CreditBalance = "SaldoAFavor";

    /// <summary>Todas las monedas en 0 (o la reserva no tiene lineas de plata).</summary>
    public const string Settled = "Saldado";

    /// <summary>
    /// Deriva el estado de cobro a partir de los balances POR MONEDA de la reserva. Regla (ADR-033 A5):
    ///   - si ALGUNA moneda tiene Balance &gt; 0  -&gt; "ConDeuda" (gana sobre el saldo a favor);
    ///   - si no hay deuda y ALGUNA moneda &lt; 0  -&gt; "SaldoAFavor";
    ///   - si todas en 0 (o no hay lineas)         -&gt; "Saldado".
    ///
    /// <para>Se usa una tolerancia chica (1 centavo) para no clasificar como deuda/saldo a favor un resto de
    /// redondeo. Los importes ya vienen redondeados a 2 decimales desde el calculador de plata.</para>
    /// </summary>
    public static string Derive(IEnumerable<decimal> balancesByCurrency)
    {
        // Umbral por debajo del centavo: cualquier resto menor se considera "saldado" en esa moneda.
        const decimal epsilon = 0.005m;

        bool anyDebt = false;
        bool anyCredit = false;

        foreach (var balance in balancesByCurrency)
        {
            if (balance > epsilon)
                anyDebt = true;
            else if (balance < -epsilon)
                anyCredit = true;
        }

        if (anyDebt)
            return WithDebt;

        if (anyCredit)
            return CreditBalance;

        return Settled;
    }
}
