using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Calculador PURO de la plata de una Reserva. Centraliza la unica matematica oficial de
/// "cuanto vale la reserva (venta/costo), cuanto vale lo CONFIRMADO, cuanto se pago y cuanto
/// debe el cliente (saldo)".
///
/// <para>ADR-020 (2026-06-07): la plata se parte en DOS numeros de venta:</para>
/// <list type="bullet">
/// <item><b>TotalSale</b>: valor comercial del presupuesto = SalePrice de los servicios NO
///   cancelados (Solicitado + Confirmado). Es lo que el cliente ve cotizado. Conserva su semantica
///   historica.</item>
/// <item><b>ConfirmedSale</b>: SalePrice de los servicios RESUELTOS
///   (<see cref="ServiceResolutionRules"/>.IsResolved). Es la deuda EXIGIBLE: un servicio recien
///   "Solicitado" todavia no genera deuda del cliente. NUEVO en ADR-020.</item>
/// </list>
///
/// <para>El saldo es <c>Balance = ConfirmedSale - TotalPaid</c> (antes era
/// <c>TotalSale - TotalPaid</c>). Si el cliente pago mas de lo confirmado (sena antes de confirmar),
/// el saldo queda negativo = saldo a favor, que es correcto: la sena existe antes que la deuda.</para>
///
/// <para>Funcion pura: sin EF ni base de datos, para testear sin Postgres y tener un solo lugar
/// donde vive la regla.</para>
/// </summary>
public static class ReservaMoneyCalculator
{
    /// <summary>
    /// Calcula los totales de la reserva a partir de sus colecciones ya cargadas
    /// (los 5 tipos de servicio tipados + servicios genericos + pagos). Funcion pura:
    /// no muta la reserva, no toca base de datos, no es async.
    ///
    /// <para>El llamador es responsable de cargar las colecciones (Includes en EF). Si una
    /// coleccion viene null se trata como vacia.</para>
    /// </summary>
    public static ReservaMoneySummary Calculate(Reserva reserva)
    {
        ArgumentNullException.ThrowIfNull(reserva);

        // VENTA COMERCIAL (TotalSale) y COSTO: servicios NO cancelados (Solicitado + Confirmado).
        decimal totalSale =
            SumFlights(reserva, IsQuotedFlight, f => f.SalePrice) +
            SumHotels(reserva, IsQuotedHotel, h => h.SalePrice) +
            SumTransfers(reserva, IsQuotedTransfer, t => t.SalePrice) +
            SumPackages(reserva, IsQuotedPackage, p => p.SalePrice) +
            SumAssistances(reserva, IsQuotedAssistance, a => a.SalePrice) +
            SumGenerics(reserva, IsQuotedGeneric, s => s.SalePrice);

        decimal totalCost =
            SumFlights(reserva, IsQuotedFlight, f => f.NetCost) +
            SumHotels(reserva, IsQuotedHotel, h => h.NetCost) +
            SumTransfers(reserva, IsQuotedTransfer, t => t.NetCost) +
            SumPackages(reserva, IsQuotedPackage, p => p.NetCost) +
            SumAssistances(reserva, IsQuotedAssistance, a => a.NetCost) +
            SumGenerics(reserva, IsQuotedGeneric, s => s.NetCost);

        // VENTA CONFIRMADA (ConfirmedSale): SOLO servicios RESUELTOS. Es la deuda exigible.
        decimal confirmedSale =
            SumFlights(reserva, ServiceResolutionRules.IsResolved, f => f.SalePrice) +
            SumHotels(reserva, ServiceResolutionRules.IsResolved, h => h.SalePrice) +
            SumTransfers(reserva, ServiceResolutionRules.IsResolved, t => t.SalePrice) +
            SumPackages(reserva, ServiceResolutionRules.IsResolved, p => p.SalePrice) +
            SumAssistances(reserva, ServiceResolutionRules.IsResolved, a => a.SalePrice) +
            SumGenerics(reserva, ServiceResolutionRules.IsResolved, s => s.SalePrice);

        decimal totalPaid = SumLivePayments(reserva);

        // ADR-020: el saldo es la VENTA CONFIRMADA menos lo pagado. Un servicio no resuelto no
        // genera deuda; un servicio cancelado sale solo de ConfirmedSale -> el saldo baja solo.
        decimal balance = confirmedSale - totalPaid;

        return new ReservaMoneySummary(
            totalSale: totalSale,
            confirmedSale: confirmedSale,
            totalCost: totalCost,
            totalPaid: totalPaid,
            balance: balance);
    }

    // --- Predicados "cotizado" (no cancelado) por tipo, espejo de WorkflowStatusHelper.CountsForQuotedTotal ---
    // Un servicio cuenta para el total comercial si NO esta cancelado (Solicitado o Confirmado).

    private static bool IsQuotedFlight(FlightSegment f)
        => WorkflowStatusHelper.CountsForQuotedTotal(WorkflowStatusHelper.MapFlightStatus(f.Status));

    private static bool IsQuotedHotel(HotelBooking h)
        => WorkflowStatusHelper.CountsForQuotedTotal(WorkflowStatusHelper.MapGenericStatus(h.Status));

    private static bool IsQuotedTransfer(TransferBooking t)
        => WorkflowStatusHelper.CountsForQuotedTotal(WorkflowStatusHelper.MapGenericStatus(t.Status));

    private static bool IsQuotedPackage(PackageBooking p)
        => WorkflowStatusHelper.CountsForQuotedTotal(WorkflowStatusHelper.MapGenericStatus(p.Status));

    private static bool IsQuotedAssistance(AssistanceBooking a)
        => WorkflowStatusHelper.CountsForQuotedTotal(WorkflowStatusHelper.MapGenericStatus(a.Status));

    private static bool IsQuotedGeneric(ServicioReserva s)
        => WorkflowStatusHelper.CountsForQuotedTotal(WorkflowStatusHelper.MapGenericStatus(s.Status));

    // --- Sumadores por coleccion: filtran con el predicado recibido y suman el selector ---
    // Cada tipo es una clase distinta, asi que no se pueden recorrer con un solo selector.

    private static decimal SumFlights(Reserva reserva, Func<FlightSegment, bool> filter, Func<FlightSegment, decimal> selector)
    {
        if (reserva.FlightSegments == null) return 0m;
        decimal total = 0m;
        foreach (var flight in reserva.FlightSegments)
            if (filter(flight)) total += selector(flight);
        return total;
    }

    private static decimal SumHotels(Reserva reserva, Func<HotelBooking, bool> filter, Func<HotelBooking, decimal> selector)
    {
        if (reserva.HotelBookings == null) return 0m;
        decimal total = 0m;
        foreach (var hotel in reserva.HotelBookings)
            if (filter(hotel)) total += selector(hotel);
        return total;
    }

    private static decimal SumTransfers(Reserva reserva, Func<TransferBooking, bool> filter, Func<TransferBooking, decimal> selector)
    {
        if (reserva.TransferBookings == null) return 0m;
        decimal total = 0m;
        foreach (var transfer in reserva.TransferBookings)
            if (filter(transfer)) total += selector(transfer);
        return total;
    }

    private static decimal SumPackages(Reserva reserva, Func<PackageBooking, bool> filter, Func<PackageBooking, decimal> selector)
    {
        if (reserva.PackageBookings == null) return 0m;
        decimal total = 0m;
        foreach (var package in reserva.PackageBookings)
            if (filter(package)) total += selector(package);
        return total;
    }

    private static decimal SumAssistances(Reserva reserva, Func<AssistanceBooking, bool> filter, Func<AssistanceBooking, decimal> selector)
    {
        if (reserva.AssistanceBookings == null) return 0m;
        decimal total = 0m;
        foreach (var assistance in reserva.AssistanceBookings)
            if (filter(assistance)) total += selector(assistance);
        return total;
    }

    private static decimal SumGenerics(Reserva reserva, Func<ServicioReserva, bool> filter, Func<ServicioReserva, decimal> selector)
    {
        if (reserva.Servicios == null) return 0m;
        decimal total = 0m;
        foreach (var service in reserva.Servicios)
            if (filter(service)) total += selector(service);
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
            if (isLive) total += payment.Amount;
        }
        return total;
    }
}
