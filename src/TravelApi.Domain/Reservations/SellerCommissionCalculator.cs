using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Calculador PURO de la comision del vendedor de una reserva, separada POR MONEDA. No toca base de
/// datos ni EF: recibe la reserva con sus servicios ya cargados y una funcion que resuelve el % de
/// comision por (proveedor, tipo de servicio). Asi se puede testear sin Postgres, igual que
/// <see cref="ReservaMoneyCalculator"/>.
///
/// <para><b>Base de calculo</b>: la GANANCIA de cada servicio CONFIRMADO (resuelto, no cancelado),
/// que en este sistema es el campo <c>Commission</c> de cada servicio (venta - costo - impuesto).
/// Por servicio: <c>ganancia * porcentaje_de_regla / 100</c>. Se acumula por la moneda del servicio.</para>
///
/// <para><b>Por que por servicio y no sobre la ganancia total de la moneda</b>: cada servicio puede caer
/// en una <see cref="CommissionRule"/> distinta (segun proveedor y tipo), con % distinto. Sumar la
/// ganancia y aplicar un % unico daria un numero incorrecto. Por eso se resuelve el % servicio por servicio
/// y recien despues se agrupa por moneda.</para>
///
/// <para><b>Tope cero (regla del dueño)</b>: si la reserva NO esta totalmente cobrada, o esta cancelada/
/// perdida, o no tiene vendedor responsable, el resultado es vacio (sin lineas) — el llamador revierte a 0
/// las comisiones que existieran. Nunca devuelve montos negativos.</para>
/// </summary>
public static class SellerCommissionCalculator
{
    /// <summary>Resultado por moneda: cuanto se devenga y el % efectivo (promedio ponderado por ganancia).</summary>
    public sealed class CommissionLine
    {
        public string Currency { get; }
        public decimal Amount { get; }
        public decimal EffectiveRatePercent { get; }

        public CommissionLine(string currency, decimal amount, decimal effectiveRatePercent)
        {
            Currency = currency;
            Amount = amount;
            EffectiveRatePercent = effectiveRatePercent;
        }
    }

    /// <summary>
    /// Estados de reserva en los que una comision PUEDE devengar (reserva viva). Fuera de estos
    /// (Cancelada, Perdida, etc.) nunca se devenga: el dueño pidio tope cero en cancelacion.
    /// </summary>
    private static readonly HashSet<string> AccruableStatuses = new(StringComparer.Ordinal)
    {
        EstadoReserva.InManagement,
        EstadoReserva.Confirmed,
        EstadoReserva.Traveling,
        EstadoReserva.ToSettle,
        EstadoReserva.Closed,
    };

    /// <summary>
    /// Calcula las comisiones del vendedor por moneda. Devuelve lista vacia si la reserva no devenga
    /// (no cobrada, sin vendedor, o en estado no devengable). Funcion pura: no muta nada.
    /// </summary>
    /// <param name="reserva">Reserva con los 6 tipos de servicio cargados (Includes responsabilidad del llamador).</param>
    /// <param name="resolveRatePercent">
    /// Devuelve el % de comision (0..100) para un (supplierId, serviceType). DEBE devolver 0 si no hay
    /// regla aplicable (el dueño pidio: sin regla, no se inventa %).
    /// </param>
    public static IReadOnlyList<CommissionLine> Calculate(
        Reserva reserva,
        Func<int?, string, decimal> resolveRatePercent)
    {
        ArgumentNullException.ThrowIfNull(reserva);
        ArgumentNullException.ThrowIfNull(resolveRatePercent);

        // Tope cero / atribucion: si la reserva no esta para devengar, devolvemos vacio y el llamador
        // pone en 0 lo que hubiera. Los tres motivos por los que NO se devenga:
        //   1) no hay vendedor responsable (no inventamos dueño de la comision);
        //   2) el estado no es devengable (cancelada/perdida/cotizacion/presupuesto);
        //   3) la reserva no esta totalmente cobrada (Balance > 0).
        if (string.IsNullOrWhiteSpace(reserva.ResponsibleUserId)) return Array.Empty<CommissionLine>();
        if (!AccruableStatuses.Contains(reserva.Status)) return Array.Empty<CommissionLine>();
        if (reserva.Balance > 0m) return Array.Empty<CommissionLine>();

        // Acumulador por moneda: monto de comision + ganancia base (para derivar el % efectivo al final).
        var byCurrency = new Dictionary<string, CurrencyAccumulator>(StringComparer.Ordinal);

        AccumulateFlights(reserva, resolveRatePercent, byCurrency);
        AccumulateHotels(reserva, resolveRatePercent, byCurrency);
        AccumulateTransfers(reserva, resolveRatePercent, byCurrency);
        AccumulatePackages(reserva, resolveRatePercent, byCurrency);
        AccumulateAssistances(reserva, resolveRatePercent, byCurrency);
        AccumulateGenerics(reserva, resolveRatePercent, byCurrency);

        var lines = new List<CommissionLine>();
        foreach (var (currency, acc) in byCurrency)
        {
            // Solo emitimos lineas con monto > 0. Una moneda con ganancia pero sin regla (% 0) no
            // genera comision y por lo tanto no genera fila (el llamador la revierte a 0 si existia).
            if (acc.CommissionAmount <= 0m) continue;

            decimal effectivePercent = acc.ConfirmedProfit > 0m
                ? Math.Round(acc.CommissionAmount / acc.ConfirmedProfit * 100m, 4)
                : 0m;

            lines.Add(new CommissionLine(
                currency: currency,
                amount: Math.Round(acc.CommissionAmount, 2),
                effectiveRatePercent: effectivePercent));
        }

        return lines;
    }

    // ============================================================================================
    // Acumulacion por tipo de servicio. Cada servicio CONFIRMADO (resuelto y no cancelado) aporta su
    // ganancia * % de regla a la moneda que el servicio declara. Un servicio sin ganancia (Commission <= 0)
    // no aporta. Misma definicion de "resuelto" que ReservaMoneyCalculator (ServiceResolutionRules).
    // ============================================================================================

    private static void AccumulateFlights(Reserva reserva, Func<int?, string, decimal> resolvePercent, Dictionary<string, CurrencyAccumulator> byCurrency)
    {
        if (reserva.FlightSegments == null) return;
        foreach (var flight in reserva.FlightSegments)
        {
            if (!ServiceResolutionRules.IsResolved(flight)) continue;
            decimal percent = resolvePercent(flight.SupplierId, ServiceTypes.Flight);
            AddProfit(byCurrency, flight.Currency, flight.Commission, percent);
        }
    }

    private static void AccumulateHotels(Reserva reserva, Func<int?, string, decimal> resolvePercent, Dictionary<string, CurrencyAccumulator> byCurrency)
    {
        if (reserva.HotelBookings == null) return;
        foreach (var hotel in reserva.HotelBookings)
        {
            if (!ServiceResolutionRules.IsResolved(hotel)) continue;
            decimal percent = resolvePercent(hotel.SupplierId, ServiceTypes.Hotel);
            AddProfit(byCurrency, hotel.Currency, hotel.Commission, percent);
        }
    }

    private static void AccumulateTransfers(Reserva reserva, Func<int?, string, decimal> resolvePercent, Dictionary<string, CurrencyAccumulator> byCurrency)
    {
        if (reserva.TransferBookings == null) return;
        foreach (var transfer in reserva.TransferBookings)
        {
            if (!ServiceResolutionRules.IsResolved(transfer)) continue;
            decimal percent = resolvePercent(transfer.SupplierId, ServiceTypes.Transfer);
            AddProfit(byCurrency, transfer.Currency, transfer.Commission, percent);
        }
    }

    private static void AccumulatePackages(Reserva reserva, Func<int?, string, decimal> resolvePercent, Dictionary<string, CurrencyAccumulator> byCurrency)
    {
        if (reserva.PackageBookings == null) return;
        foreach (var package in reserva.PackageBookings)
        {
            if (!ServiceResolutionRules.IsResolved(package)) continue;
            decimal percent = resolvePercent(package.SupplierId, ServiceTypes.Package);
            AddProfit(byCurrency, package.Currency, package.Commission, percent);
        }
    }

    private static void AccumulateAssistances(Reserva reserva, Func<int?, string, decimal> resolvePercent, Dictionary<string, CurrencyAccumulator> byCurrency)
    {
        if (reserva.AssistanceBookings == null) return;
        foreach (var assistance in reserva.AssistanceBookings)
        {
            if (!ServiceResolutionRules.IsResolved(assistance)) continue;
            decimal percent = resolvePercent(assistance.SupplierId, ServiceTypes.Insurance);
            AddProfit(byCurrency, assistance.Currency, assistance.Commission, percent);
        }
    }

    private static void AccumulateGenerics(Reserva reserva, Func<int?, string, decimal> resolvePercent, Dictionary<string, CurrencyAccumulator> byCurrency)
    {
        if (reserva.Servicios == null) return;
        foreach (var service in reserva.Servicios)
        {
            if (!ServiceResolutionRules.IsResolved(service)) continue;
            // El generico tiene su propio tipo (ServiceType, ej. "Excursion"/"Otro"); si viene null lo
            // tratamos como "Otro" para que la resolucion de regla tenga un tipo concreto.
            string serviceType = string.IsNullOrWhiteSpace(service.ServiceType) ? ServiceTypes.Other : service.ServiceType;
            decimal percent = resolvePercent(service.SupplierId, serviceType);
            AddProfit(byCurrency, service.Currency, service.Commission, percent);
        }
    }

    /// <summary>
    /// Suma la comision de UN servicio confirmado a la linea de su moneda. La comision del servicio es
    /// <c>ganancia * porcentaje / 100</c>. Una ganancia &lt;= 0 (servicio sin margen o con perdida) no
    /// aporta: no tiene sentido devengar comision sobre una ganancia inexistente.
    /// </summary>
    private static void AddProfit(Dictionary<string, CurrencyAccumulator> byCurrency, string? rawCurrency, decimal serviceProfit, decimal percent)
    {
        if (serviceProfit <= 0m) return;
        if (percent <= 0m) return;

        string currency = Monedas.Normalizar(rawCurrency);
        if (!byCurrency.TryGetValue(currency, out var acc))
        {
            acc = new CurrencyAccumulator();
            byCurrency[currency] = acc;
        }

        acc.ConfirmedProfit += serviceProfit;
        acc.CommissionAmount += serviceProfit * percent / 100m;
    }

    private sealed class CurrencyAccumulator
    {
        public decimal ConfirmedProfit;
        public decimal CommissionAmount;
    }
}
