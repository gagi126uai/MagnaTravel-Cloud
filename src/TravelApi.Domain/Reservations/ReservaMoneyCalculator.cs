using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Calculador PURO de la plata de una Reserva. Centraliza la unica matematica oficial de
/// "cuanto vale la reserva (venta/costo), cuanto vale lo CONFIRMADO, cuanto se pago y cuanto
/// debe el cliente (saldo)".
///
/// <para>ADR-020 (2026-06-07): la venta se parte en TotalSale (presupuesto, servicios no
/// cancelados) y ConfirmedSale (deuda exigible, servicios RESUELTOS). El saldo es
/// <c>ConfirmedSale - TotalPaid</c>.</para>
///
/// <para>ADR-021 (2026-06-08, multimoneda): el calculo agrupa cada servicio por SU moneda
/// (<c>servicio.Currency</c>, null = ARS) y cada pago por la moneda a la que se IMPUTA, produciendo
/// un detalle separado por moneda (<see cref="ReservaMoneySummary.PorMoneda"/>) que NUNCA mezcla
/// USD con ARS. Los escalares heredados se derivan de ese detalle para compat (ver
/// <see cref="ReservaMoneySummary"/>).</para>
///
/// <para><b>Regla de oro (regresion)</b>: una reserva 100% en una sola moneda (caso legacy ARS)
/// da exactamente los mismos numeros que antes de ADR-021 — el detalle queda con una sola linea y
/// los escalares coinciden con la cuenta vieja.</para>
///
/// <para>Funcion pura: sin EF ni base de datos, para testear sin Postgres y tener un solo lugar
/// donde vive la regla.</para>
/// </summary>
public static class ReservaMoneyCalculator
{
    /// <summary>
    /// Calcula los totales de la reserva (separados por moneda) a partir de sus colecciones ya
    /// cargadas (los 5 tipos de servicio tipados + servicios genericos + pagos). Funcion pura:
    /// no muta la reserva, no toca base de datos, no es async.
    ///
    /// <para>El llamador es responsable de cargar las colecciones (Includes en EF). Si una
    /// coleccion viene null se trata como vacia.</para>
    /// </summary>
    public static ReservaMoneySummary Calculate(Reserva reserva)
    {
        ArgumentNullException.ThrowIfNull(reserva);

        // Acumulador mutable por moneda. Se vuelca a ReservaMoneyLine (inmutable) al final.
        // Clave = moneda canonica (Monedas.Normalizar). Una entrada por cada moneda que aparezca
        // en algun servicio o pago.
        var porMoneda = new Dictionary<string, CurrencyAccumulator>();

        AccumulateServices(reserva, porMoneda);
        AccumulatePayments(reserva, porMoneda);

        // Volcado a lineas inmutables. El Balance de cada linea lo calcula la propia ReservaMoneyLine
        // (ConfirmedSale - TotalPaid de esa moneda).
        var lines = new Dictionary<string, ReservaMoneyLine>(StringComparer.Ordinal);
        foreach (var (currency, acc) in porMoneda)
        {
            lines[currency] = new ReservaMoneyLine(
                currency: currency,
                totalSale: acc.TotalSale,
                confirmedSale: acc.ConfirmedSale,
                totalCost: acc.TotalCost,
                totalPaid: acc.TotalPaid);
        }

        return new ReservaMoneySummary(lines);
    }

    // ============================================================================================
    // Servicios: cada servicio aporta su SalePrice/NetCost a la moneda que el servicio declara.
    // El filtro de "cuenta o no" (cotizado / resuelto) es EXACTAMENTE el mismo de antes; lo unico
    // nuevo es que ahora el monto cae en la linea de su moneda en vez de un escalar global.
    // ============================================================================================

    private static void AccumulateServices(Reserva reserva, Dictionary<string, CurrencyAccumulator> porMoneda)
    {
        if (reserva.FlightSegments != null)
            foreach (var flight in reserva.FlightSegments)
                AddService(porMoneda, flight.Currency,
                    quoted: IsQuotedFlight(flight),
                    resolved: ServiceResolutionRules.IsResolved(flight),
                    salePrice: flight.SalePrice, netCost: flight.NetCost);

        if (reserva.HotelBookings != null)
            foreach (var hotel in reserva.HotelBookings)
                AddService(porMoneda, hotel.Currency,
                    quoted: IsQuotedHotel(hotel),
                    resolved: ServiceResolutionRules.IsResolved(hotel),
                    salePrice: hotel.SalePrice, netCost: hotel.NetCost);

        if (reserva.TransferBookings != null)
            foreach (var transfer in reserva.TransferBookings)
                AddService(porMoneda, transfer.Currency,
                    quoted: IsQuotedTransfer(transfer),
                    resolved: ServiceResolutionRules.IsResolved(transfer),
                    salePrice: transfer.SalePrice, netCost: transfer.NetCost);

        if (reserva.PackageBookings != null)
            foreach (var package in reserva.PackageBookings)
                AddService(porMoneda, package.Currency,
                    quoted: IsQuotedPackage(package),
                    resolved: ServiceResolutionRules.IsResolved(package),
                    salePrice: package.SalePrice, netCost: package.NetCost);

        if (reserva.AssistanceBookings != null)
            foreach (var assistance in reserva.AssistanceBookings)
                AddService(porMoneda, assistance.Currency,
                    quoted: IsQuotedAssistance(assistance),
                    resolved: ServiceResolutionRules.IsResolved(assistance),
                    salePrice: assistance.SalePrice, netCost: assistance.NetCost);

        if (reserva.Servicios != null)
            foreach (var service in reserva.Servicios)
                AddService(porMoneda, service.Currency,
                    quoted: IsQuotedGeneric(service),
                    resolved: ServiceResolutionRules.IsResolved(service),
                    salePrice: service.SalePrice, netCost: service.NetCost);
    }

    /// <summary>
    /// Aporta un servicio a la linea de su moneda. TotalSale/TotalCost suman si el servicio esta
    /// "cotizado" (no cancelado); ConfirmedSale suma solo si esta "resuelto". Mismo criterio que
    /// el calculo escalar previo: aca solo cambia el destino (linea por moneda).
    /// </summary>
    private static void AddService(
        Dictionary<string, CurrencyAccumulator> porMoneda,
        string? rawCurrency, bool quoted, bool resolved, decimal salePrice, decimal netCost)
    {
        // Un servicio sin nada que aportar (ni cotizado ni resuelto = cancelado) no crea su moneda.
        if (!quoted && !resolved) return;

        var acc = GetOrCreate(porMoneda, rawCurrency);
        if (quoted)
        {
            acc.TotalSale += salePrice;
            acc.TotalCost += netCost;
        }
        if (resolved)
        {
            acc.ConfirmedSale += salePrice;
        }
    }

    // ============================================================================================
    // Pagos: cada pago vivo aporta a la moneda a la que se IMPUTA.
    //   - Pago NO cruzado (ImputedCurrency null o == Currency): imputa su Amount a su propia moneda.
    //   - Pago cruzado (ImputedCurrency != Currency): imputa su ImputedAmount (equivalente convertido)
    //     a la moneda del saldo (ImputedCurrency). La caja real (Amount+Currency) la lee tesoreria
    //     aparte; aca solo nos interesa cuanto bajo la DEUDA de cada moneda (ADR-021 §2.3/§2.8).
    // ============================================================================================

    private static void AccumulatePayments(Reserva reserva, Dictionary<string, CurrencyAccumulator> porMoneda)
    {
        if (reserva.Payments == null) return;

        foreach (var payment in reserva.Payments)
        {
            // Mismo filtro de "pago vivo" de siempre: ni cancelado ni borrado (soft delete).
            bool isLive = payment.Status != "Cancelled" && !payment.IsDeleted;
            if (!isLive) continue;

            // Moneda a la que se imputa y monto imputado. Para el caso legacy (sin moneda ni
            // imputacion) esto es ARS + Amount = identico a hoy.
            string imputedCurrency = Monedas.Normalizar(payment.ImputedCurrency ?? payment.Currency);
            decimal imputedAmount = payment.ImputedAmount ?? payment.Amount;

            var acc = GetOrCreate(porMoneda, imputedCurrency);
            acc.TotalPaid += imputedAmount;
        }
    }

    /// <summary>
    /// Devuelve (creando si hace falta) el acumulador de la moneda canonica de <paramref name="rawCurrency"/>.
    /// Normaliza null/vacio a ARS, de modo que el dato legacy sin moneda cae siempre en la linea ARS.
    /// </summary>
    private static CurrencyAccumulator GetOrCreate(Dictionary<string, CurrencyAccumulator> porMoneda, string? rawCurrency)
    {
        string currency = Monedas.Normalizar(rawCurrency);
        if (!porMoneda.TryGetValue(currency, out var acc))
        {
            acc = new CurrencyAccumulator();
            porMoneda[currency] = acc;
        }
        return acc;
    }

    /// <summary>Acumulador mutable interno por moneda; se vuelca a <see cref="ReservaMoneyLine"/> al final.</summary>
    private sealed class CurrencyAccumulator
    {
        public decimal TotalSale;
        public decimal ConfirmedSale;
        public decimal TotalCost;
        public decimal TotalPaid;
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
}
