namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-021 §2.3 (multimoneda, 2026-06-08): el detalle de plata de UNA moneda dentro de una reserva.
/// El <see cref="ReservaMoneySummary"/> tiene un diccionario de estas lineas (una por cada moneda
/// presente en los servicios o pagos de la reserva; hoy a lo sumo dos: ARS y USD).
///
/// <para><b>Por que existe</b>: el dueno decidio que los totales se muestran SEPARADOS por moneda,
/// SIN convertir a una moneda base. Sumar USD + ARS como numeros pelados (lo que hacia el calculo
/// viejo) no tiene sentido contable. Cada linea aisla la matematica de su propia moneda.</para>
///
/// <para>Es un value object inmutable y puro (sin EF, sin base de datos). La proyeccion persistida
/// de estas lineas es la tabla hija <c>ReservaMoneyByCurrency</c>, que escribe el persister de Capa 2.</para>
/// </summary>
public sealed class ReservaMoneyLine
{
    /// <summary>Moneda de esta linea: "ARS" o "USD" (forma canonica de <c>Monedas.Normalizar</c>).</summary>
    public string Currency { get; }

    /// <summary>Suma de SalePrice de los servicios NO cancelados de ESTA moneda (valor comercial).</summary>
    public decimal TotalSale { get; }

    /// <summary>
    /// Suma de SalePrice de los servicios RESUELTOS (ADR-020) de ESTA moneda. Es la deuda exigible
    /// de esta moneda; alimenta el <see cref="Balance"/>.
    /// </summary>
    public decimal ConfirmedSale { get; }

    /// <summary>Suma de NetCost de los servicios NO cancelados de ESTA moneda.</summary>
    public decimal TotalCost { get; }

    /// <summary>
    /// Pagado imputado a ESTA moneda. Para un pago no cruzado es su <c>Amount</c>; para un pago
    /// cruzado es su <c>ImputedAmount</c> (el equivalente convertido que baja del saldo de esta moneda).
    /// </summary>
    public decimal TotalPaid { get; }

    /// <summary>
    /// Saldo de ESTA moneda = <see cref="ConfirmedSale"/> - <see cref="TotalPaid"/>. Puede ser
    /// negativo (saldo a favor del cliente en esta moneda). El saldo a favor de una moneda NO
    /// compensa la deuda de otra (decision §2.4: deber USD no se cancela con sobrepago ARS).
    /// </summary>
    public decimal Balance { get; }

    public ReservaMoneyLine(string currency, decimal totalSale, decimal confirmedSale, decimal totalCost, decimal totalPaid)
    {
        Currency = currency;
        TotalSale = totalSale;
        ConfirmedSale = confirmedSale;
        TotalCost = totalCost;
        TotalPaid = totalPaid;
        Balance = confirmedSale - totalPaid;
    }
}
