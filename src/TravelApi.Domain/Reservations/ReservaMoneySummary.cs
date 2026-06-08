namespace TravelApi.Domain.Reservations;

/// <summary>
/// Resultado inmutable del calculo de la plata de una Reserva: los 4 numeros que
/// la Reserva persiste (venta, costo, pagado y saldo). Es un value object puro:
/// no toca base de datos ni EF, solo transporta los totales ya calculados.
///
/// <para>Existe para que <see cref="ReservaMoneyCalculator"/> devuelva un unico objeto
/// con todos los totales en vez de cuatro <c>out decimal</c> sueltos, y para poder
/// testear la matematica sin base de datos.</para>
/// </summary>
public sealed class ReservaMoneySummary
{
    /// <summary>Suma de SalePrice de los servicios NO cancelados (valor comercial del presupuesto).</summary>
    public decimal TotalSale { get; }

    /// <summary>
    /// ADR-020: suma de SalePrice de los servicios RESUELTOS (ServiceResolutionRules.IsResolved).
    /// Es la deuda EXIGIBLE al cliente; alimenta el saldo.
    /// </summary>
    public decimal ConfirmedSale { get; }

    /// <summary>Suma de NetCost de los servicios NO cancelados.</summary>
    public decimal TotalCost { get; }

    /// <summary>Suma de los pagos vivos (Status != "Cancelled" y no borrados).</summary>
    public decimal TotalPaid { get; }

    /// <summary>
    /// Saldo del cliente. ADR-020: <c>ConfirmedSale - TotalPaid</c> (antes era TotalSale - TotalPaid).
    /// Un servicio no resuelto no genera deuda; puede quedar negativo (saldo a favor) si el cliente
    /// pago una sena antes de que se confirme/resuelva el servicio.
    /// </summary>
    public decimal Balance { get; }

    public ReservaMoneySummary(decimal totalSale, decimal confirmedSale, decimal totalCost, decimal totalPaid, decimal balance)
    {
        TotalSale = totalSale;
        ConfirmedSale = confirmedSale;
        TotalCost = totalCost;
        TotalPaid = totalPaid;
        Balance = balance;
    }
}
