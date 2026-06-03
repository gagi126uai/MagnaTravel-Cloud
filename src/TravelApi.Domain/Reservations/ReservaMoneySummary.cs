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
    /// <summary>Suma de SalePrice de los servicios cuyo estado cuenta para el saldo.</summary>
    public decimal TotalSale { get; }

    /// <summary>Suma de NetCost de los servicios cuyo estado cuenta para el saldo.</summary>
    public decimal TotalCost { get; }

    /// <summary>Suma de los pagos vivos (Status != "Cancelled" y no borrados).</summary>
    public decimal TotalPaid { get; }

    /// <summary>
    /// Saldo del cliente. Regla historica: es TotalSale - TotalPaid (NO usa TotalCost;
    /// el costo es lo que la agencia le paga al proveedor, no afecta lo que debe el cliente).
    /// </summary>
    public decimal Balance { get; }

    public ReservaMoneySummary(decimal totalSale, decimal totalCost, decimal totalPaid, decimal balance)
    {
        TotalSale = totalSale;
        TotalCost = totalCost;
        TotalPaid = totalPaid;
        Balance = balance;
    }
}
