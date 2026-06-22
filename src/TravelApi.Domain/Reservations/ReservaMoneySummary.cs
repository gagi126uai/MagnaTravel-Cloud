namespace TravelApi.Domain.Reservations;

/// <summary>
/// Resultado inmutable del calculo de la plata de una Reserva. Es un value object puro:
/// no toca base de datos ni EF, solo transporta los totales ya calculados.
///
/// <para>ADR-021 (multimoneda, 2026-06-08): ahora trae DOS lecturas coherentes de la misma plata:</para>
/// <list type="bullet">
/// <item><b><see cref="PorMoneda"/></b>: el detalle REAL separado por moneda (una <see cref="ReservaMoneyLine"/>
///   por cada moneda presente). Es lo que vale contablemente; nunca mezcla USD con ARS.</item>
/// <item><b>Los escalares heredados</b> (<see cref="TotalSale"/>, <see cref="ConfirmedSale"/>,
///   <see cref="TotalCost"/>, <see cref="TotalPaid"/>, <see cref="Balance"/>): se conservan para no
///   romper la lectura legacy de golpe. <see cref="Balance"/> pasa a ser un SURROGATE (semaforo de
///   "tiene saldo pendiente"), no un monto; ver su doc.</item>
/// </list>
///
/// <para><b>Regla de oro (regresion)</b>: una reserva 100% en una sola moneda (el caso legacy ARS)
/// produce exactamente los mismos escalares que antes de ADR-021. La multimoneda no cambia nada para
/// datos mono-moneda. Ver <see cref="ReservaMoneyCalculator"/>.</para>
/// </summary>
public sealed class ReservaMoneySummary
{
    /// <summary>
    /// ADR-021: detalle de plata SEPARADO por moneda. Clave = moneda canonica ("ARS"/"USD"); valor =
    /// la linea con los 5 numeros de esa moneda. Es la fuente de verdad del monto real por moneda y
    /// la que se proyecta a la tabla hija <c>ReservaMoneyByCurrency</c>. Vacio si la reserva no tiene
    /// ni servicios ni pagos.
    /// </summary>
    public IReadOnlyDictionary<string, ReservaMoneyLine> PorMoneda { get; }

    /// <summary>ADR-021: true si la reserva mueve mas de una moneda (<see cref="PorMoneda"/> tiene 2+ entradas).</summary>
    public bool EsMultimoneda => PorMoneda.Count > 1;

    /// <summary>
    /// Compat: suma cruda de SalePrice de los servicios NO cancelados de TODAS las monedas. En
    /// multimoneda mezcla USD+ARS (pierde sentido contable); se conserva solo para lectura legacy
    /// hasta migrar cada consumidor a <see cref="PorMoneda"/>. En mono-moneda es identico a hoy.
    /// </summary>
    public decimal TotalSale { get; }

    /// <summary>Compat: suma cruda de la venta confirmada de todas las monedas. Misma salvedad que <see cref="TotalSale"/>.</summary>
    public decimal ConfirmedSale { get; }

    /// <summary>Compat: suma cruda de NetCost de todas las monedas. Misma salvedad que <see cref="TotalSale"/>.</summary>
    public decimal TotalCost { get; }

    /// <summary>Compat: suma cruda de lo pagado (imputado) de todas las monedas. Misma salvedad que <see cref="TotalSale"/>.</summary>
    public decimal TotalPaid { get; }

    /// <summary>
    /// Margen escalar = <see cref="ConfirmedSale"/> - <see cref="TotalCost"/> (suma cruda de todas las monedas).
    /// En multimoneda mezcla monedas (pierde sentido contable), igual que los demas escalares; el margen REAL
    /// por moneda vive en cada <see cref="ReservaMoneyLine.Margin"/>. En mono-moneda es el margen exacto.
    ///
    /// <para><b>DATO SENSIBLE</b>: contiene el costo por resta. Se enmascara junto con <see cref="TotalCost"/>
    /// en el boundary de presentacion (ver ApplyCostMaskingAsync). El value object lo expone crudo.</para>
    /// </summary>
    public decimal TotalMargin { get; }

    /// <summary>
    /// SURROGATE / SEMAFORO de saldo pendiente (ADR-021 §2.4), NO un monto adeudado.
    ///
    /// <para><b>Mono-moneda</b>: es identico al saldo de siempre (<c>ConfirmedSale - TotalPaid</c>,
    /// puede ser negativo = saldo a favor) -> los tests y gates legacy siguen byte-identicos.</para>
    ///
    /// <para><b>Multimoneda</b>: es <c>suma_por_moneda( max(0, linea.Balance) )</c>. Es <c>0</c> sii
    /// NINGUNA moneda debe; si alguna debe, queda <c>&gt; 0</c>. El saldo a favor de una moneda no
    /// compensa la deuda de otra. Sirve a los lectores booleanos (gate <c>&lt;= 0</c>, job de
    /// auto-cierre en SQL); el monto real por moneda vive en <see cref="PorMoneda"/>.</para>
    /// </summary>
    public decimal Balance { get; }

    /// <summary>
    /// Constructor canonico de ADR-021: arma el summary desde el detalle por moneda y deriva los
    /// escalares de compat. Los escalares de venta/costo/pagado son la suma cruda; el
    /// <see cref="Balance"/> surrogate se calcula segun la regla de §2.4 (ver su doc).
    /// </summary>
    public ReservaMoneySummary(IReadOnlyDictionary<string, ReservaMoneyLine> porMoneda)
    {
        PorMoneda = porMoneda ?? throw new ArgumentNullException(nameof(porMoneda));

        decimal totalSale = 0m, confirmedSale = 0m, totalCost = 0m, totalPaid = 0m;
        foreach (var line in porMoneda.Values)
        {
            totalSale += line.TotalSale;
            confirmedSale += line.ConfirmedSale;
            totalCost += line.TotalCost;
            totalPaid += line.TotalPaid;
        }

        TotalSale = totalSale;
        ConfirmedSale = confirmedSale;
        TotalCost = totalCost;
        TotalPaid = totalPaid;
        // Margen escalar = venta confirmada - costo (suma cruda). Coherente con los demas escalares de compat.
        TotalMargin = confirmedSale - totalCost;
        Balance = ComputeSurrogateBalance(porMoneda);
    }

    /// <summary>
    /// Calcula el <see cref="Balance"/> surrogate (§2.4).
    ///
    /// <para><b>Mono-moneda</b>: devuelve el saldo crudo de esa unica moneda (puede ser negativo).
    /// Asi el caso legacy ARS queda byte-identico al calculo anterior, incluido el saldo a favor.
    /// Esto es una refinacion conservadora sobre el ADR (que define el surrogate como
    /// <c>sum(max(0, ...))</c> universal): para una sola moneda ambas formulas coinciden salvo en
    /// el sobrepago, donde preservar el negativo respeta la "regla de oro" de regresion sin afectar
    /// el gate (que usa <c>&lt;= 0</c>).</para>
    ///
    /// <para><b>Multimoneda</b>: <c>sum(max(0, linea.Balance))</c> — el saldo a favor de una moneda
    /// no compensa la deuda de otra.</para>
    /// </summary>
    private static decimal ComputeSurrogateBalance(IReadOnlyDictionary<string, ReservaMoneyLine> porMoneda)
    {
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
