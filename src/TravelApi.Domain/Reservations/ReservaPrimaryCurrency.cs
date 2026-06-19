namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-035 Decision 2 / C5 (2026-06-19): elige la moneda PRINCIPAL de una reserva, es decir, la que
/// se debe preseleccionar como default al cobrar. El backend es el unico que decide esto; el front
/// solo consume el valor (ADR-035 §7). Antes vivia privado en <c>ReservaService</c>; se extrajo aca
/// para reusarlo tambien en la worklist de cobranza (PaymentService) sin duplicar la regla.
///
/// <para><b>Criterio</b>: la moneda con MAYOR saldo pendiente (Balance &gt; 0), porque es lo que el
/// cliente debe y lo mas probable a cobrar. Si ninguna moneda debe (todas saldadas o con saldo a
/// favor) cae a la de mayor saldo en valor absoluto, para igual ofrecer un default razonable. Una
/// sola moneda: esa. Sin lineas: null.</para>
///
/// <para><b>Desempate</b>: en empate de saldo se queda con la PRIMERA segun el orden en que llegan
/// las lineas. Ambos llamadores (detalle de reserva y worklist) ordenan <c>PorMoneda</c>
/// alfabeticamente por <c>Currency</c> antes de invocar, asi el desempate es estable y reproducible.</para>
///
/// <para>Funcion pura: sin EF ni base de datos. Opera sobre pares (moneda, saldo) para no depender de
/// ningun DTO de la capa de aplicacion ni del entity de persistencia.</para>
/// </summary>
public static class ReservaPrimaryCurrency
{
    /// <summary>
    /// Devuelve la moneda principal a partir de las lineas (moneda + saldo) de la reserva, o null si
    /// no hay lineas. Ver el criterio y el desempate en la doc de la clase.
    /// </summary>
    public static string? Resolve(IReadOnlyList<(string Currency, decimal Balance)> lines)
    {
        if (lines is null || lines.Count == 0)
            return null;

        if (lines.Count == 1)
            return lines[0].Currency;

        // Preferimos la moneda con mayor DEUDA (Balance > 0): es lo que el cliente debe y lo mas
        // probable a cobrar. Comparamos con ">" (no ">=") para que el empate conserve la primera
        // segun el orden de entrada (alfabetico estable en ambos llamadores).
        bool foundDebt = false;
        string bestDebtCurrency = lines[0].Currency;
        decimal bestDebtBalance = decimal.MinValue;
        foreach (var line in lines)
        {
            if (line.Balance > 0m && line.Balance > bestDebtBalance)
            {
                bestDebtBalance = line.Balance;
                bestDebtCurrency = line.Currency;
                foundDebt = true;
            }
        }
        if (foundDebt)
            return bestDebtCurrency;

        // Ninguna moneda debe: tomamos la de mayor saldo en valor absoluto (ej. mayor saldo a favor)
        // como default razonable.
        string fallbackCurrency = lines[0].Currency;
        decimal fallbackAbs = System.Math.Abs(lines[0].Balance);
        foreach (var line in lines)
        {
            decimal abs = System.Math.Abs(line.Balance);
            if (abs > fallbackAbs)
            {
                fallbackAbs = abs;
                fallbackCurrency = line.Currency;
            }
        }
        return fallbackCurrency;
    }
}
