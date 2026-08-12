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

    /// <summary>
    /// Opciones A/B/C (decisión #1, 2026-08-11/12): devuelve los nombres de grupo (normalizados) que
    /// hoy tienen MÁS DE UNA alternativa VIVA en <paramref name="reserva"/>. Función pura, misma fuente
    /// que usa <see cref="Calculate"/> para no duplicar los totales — la reutiliza también
    /// <c>ReservaAutoStateService</c> (fix B1 "cinturón", review de seguridad 2026-08-12): el motor
    /// automático NO confirma una reserva con un grupo todavía ambiguo, aunque el candado de escritura
    /// (<c>BookingService.EnsureOptionGroupOnlySetDuringPresupuesto</c>) sea la defensa PRINCIPAL.
    /// </summary>
    public static HashSet<string> FindAmbiguousOptionGroups(Reserva reserva)
    {
        ArgumentNullException.ThrowIfNull(reserva);
        return OptionGroupRules.FindAmbiguousGroups(BuildOptionGroupServiceInfos(reserva));
    }

    // ============================================================================================
    // Servicios: cada servicio aporta su SalePrice/NetCost a la moneda que el servicio declara.
    // El filtro de "cuenta o no" (cotizado / resuelto) es EXACTAMENTE el mismo de antes; lo unico
    // nuevo es que ahora el monto cae en la linea de su moneda en vez de un escalar global.
    // ============================================================================================

    private static void AccumulateServices(Reserva reserva, Dictionary<string, CurrencyAccumulator> porMoneda)
    {
        // Opciones A/B/C (decisión #1 firmada, 2026-08-11/12): si dos o más servicios de la reserva
        // compiten por el mismo OptionGroup (ej. "Hotel A" vs "Hotel B") y todavía no se resolvió cuál
        // quedó, NINGUNO de los dos suma a los totales — sumar ambos duplicaría la venta/el costo de un
        // servicio que en los hechos es UNO SOLO (el cliente todavía no eligió). Apenas queda una sola
        // alternativa viva en el grupo (las demás se cancelaron o se borraron al resolver, ver
        // BookingService.ResolveOptionGroupAsync), el grupo deja de ser ambiguo y esa única opción
        // vuelve a contar normal. Ver TravelApi.Domain.Reservations.OptionGroupRules.
        var ambiguousGroups = FindAmbiguousOptionGroups(reserva);

        if (reserva.FlightSegments != null)
            foreach (var flight in reserva.FlightSegments)
            {
                if (OptionGroupRules.BelongsToAmbiguousGroup(flight.OptionGroup, ambiguousGroups)) continue;
                AddService(porMoneda, flight.Currency,
                    quoted: IsQuotedFlight(flight),
                    resolved: ServiceResolutionRules.IsResolved(flight),
                    salePrice: flight.SalePrice, netCost: flight.NetCost);
            }

        if (reserva.HotelBookings != null)
            foreach (var hotel in reserva.HotelBookings)
            {
                if (OptionGroupRules.BelongsToAmbiguousGroup(hotel.OptionGroup, ambiguousGroups)) continue;
                AddService(porMoneda, hotel.Currency,
                    quoted: IsQuotedHotel(hotel),
                    resolved: ServiceResolutionRules.IsResolved(hotel),
                    salePrice: hotel.SalePrice, netCost: hotel.NetCost);
            }

        if (reserva.TransferBookings != null)
            foreach (var transfer in reserva.TransferBookings)
            {
                if (OptionGroupRules.BelongsToAmbiguousGroup(transfer.OptionGroup, ambiguousGroups)) continue;
                AddService(porMoneda, transfer.Currency,
                    quoted: IsQuotedTransfer(transfer),
                    resolved: ServiceResolutionRules.IsResolved(transfer),
                    salePrice: transfer.SalePrice, netCost: transfer.NetCost);
            }

        if (reserva.PackageBookings != null)
            foreach (var package in reserva.PackageBookings)
            {
                if (OptionGroupRules.BelongsToAmbiguousGroup(package.OptionGroup, ambiguousGroups)) continue;
                AddService(porMoneda, package.Currency,
                    quoted: IsQuotedPackage(package),
                    resolved: ServiceResolutionRules.IsResolved(package),
                    salePrice: package.SalePrice, netCost: package.NetCost);
            }

        if (reserva.AssistanceBookings != null)
            foreach (var assistance in reserva.AssistanceBookings)
            {
                if (OptionGroupRules.BelongsToAmbiguousGroup(assistance.OptionGroup, ambiguousGroups)) continue;
                AddService(porMoneda, assistance.Currency,
                    quoted: IsQuotedAssistance(assistance),
                    resolved: ServiceResolutionRules.IsResolved(assistance),
                    salePrice: assistance.SalePrice, netCost: assistance.NetCost);
            }

        // ServicioReserva (generico/legacy) NO participa de las opciones A/B/C: no tiene alta desde la
        // ficha moderna (ver ServiciosReservaController, el POST directo esta deprecado con 410 Gone),
        // asi que nunca puede llegar con OptionGroup cargado. Se mantiene fuera de este mecanismo a
        // proposito (menos superficie, ver reporte de la obra "PDF de presupuesto").
        if (reserva.Servicios != null)
            foreach (var service in reserva.Servicios)
                AddService(porMoneda, service.Currency,
                    quoted: IsQuotedGeneric(service),
                    resolved: ServiceResolutionRules.IsResolved(service),
                    salePrice: service.SalePrice, netCost: service.NetCost);
    }

    /// <summary>
    /// Junta, de los 5 servicios tipados, (OptionGroup, IsLive) para que <see cref="OptionGroupRules"/>
    /// calcule qué grupos siguen ambiguos. "Vivo" = cotizado (mismo predicado <c>IsQuotedXxx</c> que ya
    /// decide si el servicio suma a TotalSale): un servicio cancelado deja de competir por su grupo.
    /// </summary>
    private static IEnumerable<OptionGroupRules.OptionGroupServiceInfo> BuildOptionGroupServiceInfos(Reserva reserva)
    {
        if (reserva.FlightSegments != null)
            foreach (var flight in reserva.FlightSegments)
                yield return new OptionGroupRules.OptionGroupServiceInfo(flight.OptionGroup, IsQuotedFlight(flight));

        if (reserva.HotelBookings != null)
            foreach (var hotel in reserva.HotelBookings)
                yield return new OptionGroupRules.OptionGroupServiceInfo(hotel.OptionGroup, IsQuotedHotel(hotel));

        if (reserva.TransferBookings != null)
            foreach (var transfer in reserva.TransferBookings)
                yield return new OptionGroupRules.OptionGroupServiceInfo(transfer.OptionGroup, IsQuotedTransfer(transfer));

        if (reserva.PackageBookings != null)
            foreach (var package in reserva.PackageBookings)
                yield return new OptionGroupRules.OptionGroupServiceInfo(package.OptionGroup, IsQuotedPackage(package));

        if (reserva.AssistanceBookings != null)
            foreach (var assistance in reserva.AssistanceBookings)
                yield return new OptionGroupRules.OptionGroupServiceInfo(assistance.OptionGroup, IsQuotedAssistance(assistance));
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

            // Los cobros de open items fiscales independientes (por ejemplo, una multa documentada luego
            // de anular la venta) mueven caja, pero no reducen nuevamente el saldo operativo de la reserva.
            if (!payment.AffectsReservaBalance) continue;

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
