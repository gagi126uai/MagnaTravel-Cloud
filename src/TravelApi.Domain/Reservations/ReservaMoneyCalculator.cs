using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Calculador PURO de la plata de una Reserva. Centraliza la unica matematica oficial de
/// "cuanto vale la reserva (venta/costo), cuanto se pago y cuanto debe el cliente (saldo)".
///
/// <para>POR QUE EXISTE: historicamente esta cuenta vivia inline dentro de
/// <c>ReservaService.UpdateBalanceAsync</c>. El objetivo de fondo del proyecto es que la
/// Reserva sea la UNICA dueña del numero de la plata; este calculador es el primer paso:
/// saca la cuenta del servicio de infraestructura y la deja en el dominio, sin EF ni base
/// de datos, para que se pueda testear sin Postgres y para que haya un solo lugar donde
/// vive la regla.</para>
///
/// <para>BEHAVIOR-PRESERVING: reproduce EXACTAMENTE la cuenta que hacia
/// <c>UpdateBalanceAsync</c>. Reusa <see cref="WorkflowStatusHelper"/> (no redefine que
/// estados cuentan). No cambia ningun numero.</para>
/// </summary>
public static class ReservaMoneyCalculator
{
    /// <summary>
    /// Calcula los 4 totales de la reserva a partir de sus colecciones ya cargadas
    /// (los 5 tipos de servicio tipados + servicios genericos + pagos). Funcion pura:
    /// no muta la reserva, no toca base de datos, no es async.
    ///
    /// <para>El llamador es responsable de cargar las colecciones (Includes en EF). Si una
    /// coleccion viene null se trata como vacia, igual que la cuenta original con <c>?? 0</c>.</para>
    /// </summary>
    public static ReservaMoneySummary Calculate(Reserva reserva)
    {
        ArgumentNullException.ThrowIfNull(reserva);

        // Venta: suma SalePrice de los servicios cuyo estado mapeado "cuenta" para el saldo.
        // CRITICO: incluye los 5 tipos tipados (vuelo + hotel + transfer + paquete + asistencia)
        // + servicios genericos. Si faltara uno, el saldo quedaria por debajo del real y el
        // cliente "deberia menos" en silencio.
        decimal totalSale =
            SumFlightSegments(reserva, flight => flight.SalePrice) +
            SumHotelBookings(reserva, hotel => hotel.SalePrice) +
            SumTransferBookings(reserva, transfer => transfer.SalePrice) +
            SumPackageBookings(reserva, package => package.SalePrice) +
            SumAssistanceBookings(reserva, assistance => assistance.SalePrice) +
            SumGenericServices(reserva, service => service.SalePrice);

        // Costo: la MISMA seleccion de servicios que la venta, pero sumando NetCost.
        decimal totalCost =
            SumFlightSegments(reserva, flight => flight.NetCost) +
            SumHotelBookings(reserva, hotel => hotel.NetCost) +
            SumTransferBookings(reserva, transfer => transfer.NetCost) +
            SumPackageBookings(reserva, package => package.NetCost) +
            SumAssistanceBookings(reserva, assistance => assistance.NetCost) +
            SumGenericServices(reserva, service => service.NetCost);

        decimal totalPaid = SumLivePayments(reserva);

        // Regla historica: el saldo es venta menos pagado. NO interviene el costo
        // (el costo es lo que la agencia le paga al proveedor, no lo que debe el cliente).
        decimal balance = totalSale - totalPaid;

        return new ReservaMoneySummary(
            totalSale: totalSale,
            totalCost: totalCost,
            totalPaid: totalPaid,
            balance: balance);
    }

    // --- Helpers privados ---
    // Cada tipo de servicio es una clase distinta, asi que no se pueden recorrer con un solo
    // selector. Para que la cuenta de Calculate se lea de corrido, cada coleccion tiene su helper
    // con el mapeo de estado correcto. Vuelos usan el mapeo IATA; el resto, el mapeo generico.

    private static decimal SumFlightSegments(Reserva reserva, Func<FlightSegment, decimal> selector)
    {
        if (reserva.FlightSegments == null) return 0m;

        decimal total = 0m;
        foreach (var flight in reserva.FlightSegments)
        {
            // Los vuelos mapean por codigo IATA (HK/TK/UN/...), distinto del mapeo generico.
            string mappedStatus = WorkflowStatusHelper.MapFlightStatus(flight.Status);
            if (WorkflowStatusHelper.CountsForReservaBalance(mappedStatus))
            {
                total += selector(flight);
            }
        }
        return total;
    }

    private static decimal SumHotelBookings(Reserva reserva, Func<HotelBooking, decimal> selector)
    {
        if (reserva.HotelBookings == null) return 0m;

        decimal total = 0m;
        foreach (var hotel in reserva.HotelBookings)
        {
            string mappedStatus = WorkflowStatusHelper.MapGenericStatus(hotel.Status);
            if (WorkflowStatusHelper.CountsForReservaBalance(mappedStatus))
            {
                total += selector(hotel);
            }
        }
        return total;
    }

    private static decimal SumTransferBookings(Reserva reserva, Func<TransferBooking, decimal> selector)
    {
        if (reserva.TransferBookings == null) return 0m;

        decimal total = 0m;
        foreach (var transfer in reserva.TransferBookings)
        {
            string mappedStatus = WorkflowStatusHelper.MapGenericStatus(transfer.Status);
            if (WorkflowStatusHelper.CountsForReservaBalance(mappedStatus))
            {
                total += selector(transfer);
            }
        }
        return total;
    }

    private static decimal SumPackageBookings(Reserva reserva, Func<PackageBooking, decimal> selector)
    {
        if (reserva.PackageBookings == null) return 0m;

        decimal total = 0m;
        foreach (var package in reserva.PackageBookings)
        {
            string mappedStatus = WorkflowStatusHelper.MapGenericStatus(package.Status);
            if (WorkflowStatusHelper.CountsForReservaBalance(mappedStatus))
            {
                total += selector(package);
            }
        }
        return total;
    }

    private static decimal SumAssistanceBookings(Reserva reserva, Func<AssistanceBooking, decimal> selector)
    {
        if (reserva.AssistanceBookings == null) return 0m;

        decimal total = 0m;
        foreach (var assistance in reserva.AssistanceBookings)
        {
            string mappedStatus = WorkflowStatusHelper.MapGenericStatus(assistance.Status);
            if (WorkflowStatusHelper.CountsForReservaBalance(mappedStatus))
            {
                total += selector(assistance);
            }
        }
        return total;
    }

    private static decimal SumGenericServices(Reserva reserva, Func<ServicioReserva, decimal> selector)
    {
        if (reserva.Servicios == null) return 0m;

        decimal total = 0m;
        foreach (var service in reserva.Servicios)
        {
            string mappedStatus = WorkflowStatusHelper.MapGenericStatus(service.Status);
            if (WorkflowStatusHelper.CountsForReservaBalance(mappedStatus))
            {
                total += selector(service);
            }
        }
        return total;
    }

    private static decimal SumLivePayments(Reserva reserva)
    {
        if (reserva.Payments == null) return 0m;

        decimal total = 0m;
        foreach (var payment in reserva.Payments)
        {
            // Cuenta el pago solo si no esta cancelado ni borrado (soft delete).
            bool isLive = payment.Status != "Cancelled" && !payment.IsDeleted;
            if (isLive)
            {
                total += payment.Amount;
            }
        }
        return total;
    }
}
