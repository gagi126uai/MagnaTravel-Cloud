namespace TravelApi.Domain.Helpers;

/// <summary>
/// ADR-017 F1.3 (catalogo find-or-create, 2026-06-05): valores cerrados del tipo de servicio del
/// catalogo. El <c>Rate.ServiceType</c> de un producto creado en venta DEBE ser uno de estos, porque
/// el buscador <c>catalog-search</c> filtra exactamente por estas etiquetas.
/// </summary>
public static class CatalogServiceTypes
{
    public const string Hotel = "Hotel";
    public const string Aereo = "Aereo";
    public const string Traslado = "Traslado";
    public const string Paquete = "Paquete";
    public const string Asistencia = "Asistencia";
}

/// <summary>
/// ADR-017 F1.3 (§2.1): valores cerrados de <c>RateSupplierSale.LastPriceUnit</c>. Indican en que unidad
/// estan expresados los montos UNITARIOS de la sugerencia, para poder re-multiplicarlos en la proxima
/// venta sin ambiguedad.
/// </summary>
public static class CatalogPriceUnits
{
    public const string NocheHabitacion = "noche_habitacion"; // Hotel: 1 noche x 1 habitacion (decision D4)
    public const string Pasajero = "pasajero";                 // Aereo / Paquete: por pasajero
    public const string Servicio = "servicio";                 // Traslado: el trayecto completo es la unidad
    public const string PasajeroDia = "pasajero_dia";          // Asistencia: por pasajero por dia de vigencia
}

/// <summary>
/// ADR-017 F1.3 (§2.1, cierra R6): unitarizacion de precios del catalogo.
///
/// <para><b>Por que existe</b>: el booking guarda montos TOTALES del servicio (lo que ve el cliente),
/// pero la sugerencia del tarifario se guarda y se muestra como precio UNITARIO ("$X la noche", "$X por
/// pasajero") para poder re-multiplicarla por las noches/pasajeros de la PROXIMA venta. Esta clase
/// concentra las dos conversiones — total -> unitario (al guardar la venta) y unitario -> total (cuando
/// la cadena de costo D7 repone el costo de una referencia previa) — para que el divisor de cada tipo
/// viva en UN solo lugar y no se desincronicen.</para>
///
/// <para><b>Redondeo</b>: <see cref="MidpointRounding.AwayFromZero"/> a 2 decimales, espejo del
/// <c>roundMoney</c> del front. Al re-multiplicar puede haber deriva de centavos: aceptado y documentado
/// en el ADR, porque la sugerencia es una referencia editable (amarillo), nunca una tarifa firme.</para>
/// </summary>
public static class CatalogUnitization
{
    /// <summary>Resultado de unitarizar los montos TOTALES de un servicio.</summary>
    public readonly record struct Unitized(
        decimal UnitNetCost,
        decimal UnitTax,
        decimal UnitSalePrice,
        int Divisor,
        string PriceUnit);

    // === Divisores por tipo (siempre >= 1; las guardas de divisor-cero estan aca, una sola vez) ===

    /// <summary>Hotel (D4): por noche POR HABITACION. Divisor = noches x habitaciones.</summary>
    public static int HotelDivisor(int nights, int rooms) => Math.Max(nights, 1) * Math.Max(rooms, 1);

    /// <summary>Aereo: por pasajero del segmento.</summary>
    public static int FlightDivisor(int passengerCount) => Math.Max(passengerCount, 1);

    /// <summary>Traslado: el trayecto completo es la unidad (divisor 1).</summary>
    public static int TransferDivisor() => 1;

    /// <summary>Paquete: por persona; los niños cuentan como persona entera (simplificacion deliberada §2.1).</summary>
    public static int PackageDivisor(int adults, int children) => Math.Max(adults + children, 1);

    /// <summary>Asistencia: por pasajero por dia de vigencia.</summary>
    public static int AssistanceDivisor(int adults, int children, int days)
        => Math.Max(adults + children, 1) * Math.Max(days, 1);

    /// <summary>
    /// Dias de vigencia de una asistencia, a partir de fechas date-only. Una poliza de un solo dia
    /// (<c>validTo == validFrom</c>) cuenta como 1 (no como 0) para no inflar el precio por dia.
    /// </summary>
    public static int AssistanceDays(DateTime validFrom, DateTime validTo)
        => Math.Max((validTo.Date - validFrom.Date).Days, 1);

    // === Conversiones base ===

    /// <summary>Total -> unitario, redondeado. El divisor se fuerza a un minimo de 1 por seguridad.</summary>
    public static decimal ToUnit(decimal total, int divisor)
        => Math.Round(total / Math.Max(divisor, 1), 2, MidpointRounding.AwayFromZero);

    /// <summary>Unitario -> total, redondeado (la inversa que usa la cadena de costo D7).</summary>
    public static decimal ToTotal(decimal unit, int divisor)
        => Math.Round(unit * Math.Max(divisor, 1), 2, MidpointRounding.AwayFromZero);

    // === Unitarizadores por tipo (totales del booking -> Unitized listo para guardar) ===

    public static Unitized ForHotel(decimal totalNet, decimal totalTax, decimal totalSale, int nights, int rooms)
        => Build(totalNet, totalTax, totalSale, HotelDivisor(nights, rooms), CatalogPriceUnits.NocheHabitacion);

    public static Unitized ForFlight(decimal totalNet, decimal totalTax, decimal totalSale, int passengerCount)
        => Build(totalNet, totalTax, totalSale, FlightDivisor(passengerCount), CatalogPriceUnits.Pasajero);

    public static Unitized ForTransfer(decimal totalNet, decimal totalTax, decimal totalSale)
        => Build(totalNet, totalTax, totalSale, TransferDivisor(), CatalogPriceUnits.Servicio);

    public static Unitized ForPackage(decimal totalNet, decimal totalTax, decimal totalSale, int adults, int children)
        => Build(totalNet, totalTax, totalSale, PackageDivisor(adults, children), CatalogPriceUnits.Pasajero);

    public static Unitized ForAssistance(decimal totalNet, decimal totalTax, decimal totalSale, int adults, int children, int days)
        => Build(totalNet, totalTax, totalSale, AssistanceDivisor(adults, children, days), CatalogPriceUnits.PasajeroDia);

    private static Unitized Build(decimal totalNet, decimal totalTax, decimal totalSale, int divisor, string priceUnit)
        => new(
            UnitNetCost: ToUnit(totalNet, divisor),
            UnitTax: ToUnit(totalTax, divisor),
            UnitSalePrice: ToUnit(totalSale, divisor),
            Divisor: divisor,
            PriceUnit: priceUnit);
}
