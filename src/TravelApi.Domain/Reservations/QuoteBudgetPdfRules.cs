using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Reglas PURAS (sin QuestPDF, sin base de datos) para armar el PDF de presupuesto que ve el cliente
/// (obra "PDF de presupuesto", maqueta v2 firmada por el dueño, 2026-08-11/12). Centraliza las
/// decisiones de "qué se dibuja, qué se omite, y con qué número" para que <c>QuotePdfService</c> (que SI
/// depende de QuestPDF) sea un renderer fino que solo pinta lo que esta clase ya decidió.
///
/// <para><b>Por qué separado del renderer</b>: un PDF armado con QuestPDF no se puede testear leyendo
/// píxeles. Sacando la LÓGICA de decisión a funciones puras (esta clase), se puede testear con un unit
/// test normal — sin generar un solo byte de PDF — que "sin equipaje cargado no aparece la línea
/// EQUIPAJE", que "0 pasajeros cargados cae a tarifa TOTAL", etc.</para>
///
/// <para><b>Regla madre de la obra (decisión #8, firmada)</b>: EL PDF ES ESPEJO DE LO CARGADO — lo que
/// no se cargó NO se dibuja, JAMÁS se inventa un dato. Todas las funciones de esta clase devuelven
/// null/vacío cuando falta el dato de origen, en vez de completar con un valor por defecto inventado.</para>
/// </summary>
public static class QuoteBudgetPdfRules
{
    /// <summary>
    /// "FORMAS DE PAGO." (decisión #2 firmada): el texto propio DE ESTE presupuesto
    /// (<see cref="Reserva.BudgetPaymentTermsText"/>) gana; si no hay, se usa la plantilla que la agencia
    /// cargó una vez en Configuración (<see cref="AgencySettings.BudgetPaymentTermsTemplate"/>); si
    /// tampoco hay plantilla, no hay nada que mostrar — la sección entera se omite (null).
    /// </summary>
    public static string? ResolvePaymentTermsText(string? reservaText, string? agencyTemplate)
    {
        if (!string.IsNullOrWhiteSpace(reservaText))
        {
            return reservaText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(agencyTemplate))
        {
            return agencyTemplate.Trim();
        }

        return null;
    }

    /// <summary>
    /// Resuelve el precio a imprimir en la tarifa de un servicio (hotel/paquete/etc): por persona o
    /// total, según el interruptor que eligió el vendedor al pedir el PDF (<paramref name="porPersona"/>).
    ///
    /// <para>"Por persona" divide el precio de venta por la cantidad de pasajeros CARGADOS en el
    /// presupuesto. La reserva, en esta etapa, todavía no tiene pasajeros nominales (esos se cargan
    /// recién en "En gestión") — la cantidad viene de <c>Reserva.AdultCount + ChildCount + InfantCount</c>.
    /// Si esa cantidad es 0 (todavía nadie cargó pasajeros), dividir daría un número sin sentido: se cae
    /// a TOTAL en su lugar. Nunca se divide por cero, nunca se inventa un pasajero.</para>
    /// </summary>
    public static QuotePriceDisplay ResolveDisplayPrice(decimal salePrice, int cantidadPasajerosCargados, bool porPersona)
    {
        if (!porPersona || cantidadPasajerosCargados <= 0)
        {
            return new QuotePriceDisplay(salePrice, IsPerPerson: false);
        }

        // Mismo criterio de redondeo comercial que el resto del dominio (ver CatalogUnitization):
        // 2 decimales, AwayFromZero (0.005 redondea "para afuera", no se trunca).
        var perPersonAmount = Math.Round(salePrice / cantidadPasajerosCargados, 2, MidpointRounding.AwayFromZero);
        return new QuotePriceDisplay(perPersonAmount, IsPerPerson: true);
    }

    /// <summary>
    /// Fix post-review (2026-08-12) + REHACER maqueta v2 (2026-08-13, ronda "recalcar la maqueta"): el
    /// monto de una tarifa, CON o SIN etiqueta de moneda. La maqueta firmada pide el MONTO primero y la
    /// moneda DESPUÉS ("1.450 USD" — antes salía "USD 1.450,00", al revés). Con
    /// <paramref name="currency"/> null/vacío se imprime el número solo, sin código de moneda al lado
    /// (regla espejo, decisión #8: nunca afirmar una moneda que nadie cargó).
    /// </summary>
    public static string BuildAmountLabel(decimal amount, string? currency)
    {
        var formattedAmount = FormatAmountForBudgetPdf(amount);
        return string.IsNullOrWhiteSpace(currency) ? formattedAmount : $"{formattedAmount} {currency}";
    }

    /// <summary>
    /// "1.450" (miles con punto, SIN decimales) para un monto redondo; "1.450,50" (coma decimal,
    /// estilo es-AR) cuando el monto tiene centavos de verdad. La maqueta pide ocultar el ",00" —
    /// nadie escribe un presupuesto a mano con "1.450,00", se ve mas prolijo "1.450" — pero un centavo
    /// real (ej. una conversión de moneda que dio 1.450,37) NUNCA se trunca ni se redondea para
    /// esconderlo. Se redondea a 2 decimales AwayFromZero primero (mismo criterio comercial que
    /// <see cref="ResolveDisplayPrice"/>) solo para decidir SI hay centavos que mostrar.
    /// </summary>
    private static string FormatAmountForBudgetPdf(decimal amount)
    {
        var roundedAmount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        var hasRealCents = roundedAmount != Math.Truncate(roundedAmount);

        return hasRealCents
            ? roundedAmount.ToString("N2", EsArCulture)
            : roundedAmount.ToString("N0", EsArCulture);
    }

    // CultureInfo.GetCultureInfo (a diferencia de "new CultureInfo") devuelve una instancia cacheada de
    // solo lectura — mismo criterio que TravelApi.Domain.Helpers.CurrencyDisplayFormat.
    private static readonly System.Globalization.CultureInfo EsArCulture = System.Globalization.CultureInfo.GetCultureInfo("es-AR");

    /// <summary>
    /// Línea "SALIDA:" ("10/02/2027 al 15/02/2027 – 5 noches."). Usa las fechas YA calculadas de la
    /// reserva (<c>Reserva.StartDate/EndDate</c>, el mín/máx de los servicios cargados — no recalcula
    /// nada acá). Las noches se toman del hotel si hay uno cargado (<paramref name="hotelNights"/>); si
    /// no hay hotel, se derivan de la diferencia de días entre las fechas. Sin fechas cargadas → null
    /// (sin línea, nunca se inventa un rango).
    /// </summary>
    public static string? BuildSalidaLine(DateTime? startDate, DateTime? endDate, int? hotelNights)
    {
        if (!startDate.HasValue || !endDate.HasValue)
        {
            return null;
        }

        var nights = hotelNights ?? Math.Max((endDate.Value.Date - startDate.Value.Date).Days, 0);
        var nightsLabel = nights == 1 ? "1 noche" : $"{nights} noches";
        return $"{startDate:dd/MM/yyyy} al {endDate:dd/MM/yyyy} – {nightsLabel}.";
    }

    /// <summary>
    /// Línea "EQUIPAJE:" derivada del PRIMER vuelo vivo (no cancelado) que tenga algún dato de equipaje
    /// cargado. Prioridad: si el vendedor cargó los 3 flags estructurados (mochila/carry on/valija), se
    /// arma una frase humana con lo que SÍ está incluido; si no cargó ningún flag pero escribió el texto
    /// libre <c>Baggage</c> ("23kg"), se usa tal cual; si no hay nada de lo dos → null (sin línea).
    /// </summary>
    public static string? BuildEquipajeLine(FlightSegment? flight)
    {
        if (flight is null)
        {
            return null;
        }

        var anyStructuredFlagLoaded = flight.IncludesBackpack.HasValue
            || flight.IncludesCarryOn.HasValue
            || flight.IncludesCheckedBag.HasValue;

        if (anyStructuredFlagLoaded)
        {
            var included = new List<string>();
            if (flight.IncludesBackpack == true) included.Add("mochila/bolso personal");
            if (flight.IncludesCarryOn == true) included.Add("equipaje de mano");
            if (flight.IncludesCheckedBag == true) included.Add("valija despachada");

            if (included.Count > 0)
            {
                return "Incluye " + JoinHumanList(included) + ".";
            }

            // Los 3 flags estan cargados y en false: el vendedor SI informo el dato (no es "sin
            // informar"), y lo que informo es "no incluye nada adicional". Se lo respeta tal cual
            // (espejo de lo cargado), no se omite la linea.
            return "No incluye equipaje adicional (más allá del personal).";
        }

        if (!string.IsNullOrWhiteSpace(flight.Baggage))
        {
            return flight.Baggage.Trim();
        }

        return null;
    }

    /// <summary>Junta una lista humana ("a, b y c") para las frases de equipaje.</summary>
    private static string JoinHumanList(IReadOnlyList<string> items)
    {
        if (items.Count == 1) return items[0];
        return string.Join(", ", items.Take(items.Count - 1)) + " y " + items[^1];
    }

    /// <summary>
    /// Línea "TRASLADO:" a partir del PRIMER traslado vivo (no cancelado). Usa <c>ProductName</c> si el
    /// vendedor cargó la ficha "producto-primero"; si no, arma "Pickup - Dropoff" cuando ambos están
    /// informados. Sin ninguno de los dos → null (sin línea).
    /// </summary>
    public static string? BuildTrasladoLine(TransferBooking? transfer)
    {
        if (transfer is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(transfer.ProductName))
        {
            return transfer.ProductName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(transfer.PickupLocation) && !string.IsNullOrWhiteSpace(transfer.DropoffLocation))
        {
            return $"{transfer.PickupLocation.Trim()} - {transfer.DropoffLocation.Trim()}";
        }

        return null;
    }

    // ============================================================================================
    // Bloque de vuelos REHECHO a la maqueta v2 (2026-08-13, ronda "recalcar la maqueta"): una fila por
    // TRAMO (no por "ida y vuelta en una sola línea"). <see cref="FlightSegment.DepartureTime"/> es
    // OBLIGATORIO desde que existe el segmento (ver CreateFlightRequest) — todo tramo vivo tiene una
    // hora de salida real, nunca inventada. El resto (llegada, aeropuertos, duración) es opcional y se
    // omite elemento por elemento cuando el vendedor no lo cargó (regla espejo, decisión #8).
    //
    // NOTA para quien retome esta obra: <see cref="FlightSegment.OutboundDepartureTime"/>/
    // <c>ReturnDepartureTime</c> (agregados en la TANDA 1 de esta misma obra) quedan SIN USAR acá — se
    // diseñaron para un vuelo "ida y vuelta como una sola línea de producto", pero la maqueta v2 firmada
    // pide una fila POR TRAMO con hora de llegada y aeropuertos, que esos dos campos no modelan (no
    // tienen fecha ni llegada). Se resuelve con los campos ORIGINALES del segmento (Origin/Destination/
    // DepartureTime/ArrivalTime/IsDirect), que sí encajan con "un tramo, una fila". No se borran los
    // campos de TANDA 1 (fuera del alcance permitido de esta ronda), pero quedan candidatos a revisar
    // con el arquitecto si de verdad hacen falta.
    // ============================================================================================

    /// <summary>
    /// Etiqueta de aeropuerto para UNA punta del tramo ("EZE · BUENOS AIRES"): código IATA + ciudad, en
    /// mayúsculas (así la pide la maqueta, chica y gris debajo de la hora). Cada dato es independiente:
    /// si el vendedor cargó uno solo de los dos, se muestra ese solo; sin ninguno de los dos → null (sin
    /// línea, nunca se inventa un aeropuerto).
    /// </summary>
    public static string? BuildFlightAirportLabel(string? code, string? city)
    {
        var trimmedCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        var trimmedCity = string.IsNullOrWhiteSpace(city) ? null : city.Trim();

        if (trimmedCode is null && trimmedCity is null) return null;
        if (trimmedCode is null) return trimmedCity!.ToUpperInvariant();
        if (trimmedCity is null) return trimmedCode.ToUpperInvariant();

        return $"{trimmedCode} · {trimmedCity}".ToUpperInvariant();
    }

    /// <summary>
    /// Duración del tramo ("3h 45m") a partir de la salida y la llegada. Null cuando no hay hora de
    /// llegada cargada (BUG 2, 2026-06-08: existen tramos solo de ida, ver <see cref="FlightSegment.ArrivalTime"/>),
    /// cuando el dato cargado es incoherente (llegada antes que salida), o cuando la llegada NO es un dato
    /// real (<see cref="IsSameInstantArrival"/>/<see cref="LooksLikeMissingSchedule"/>) — la maqueta omite
    /// el número entero en vez de mostrar algo inventado, negativo o absurdo ("24h" de un tramo sin cargar).
    /// </summary>
    public static string? BuildFlightDuration(DateTime departureTime, DateTime? arrivalTime)
    {
        if (!arrivalTime.HasValue) return null;
        if (IsSameInstantArrival(departureTime, arrivalTime.Value)) return null;
        if (LooksLikeMissingSchedule(departureTime, arrivalTime.Value)) return null;

        var duration = arrivalTime.Value - departureTime;
        if (duration < TimeSpan.Zero) return null;

        var hours = (int)duration.TotalHours;
        var minutes = duration.Minutes;

        return minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
    }

    /// <summary>
    /// True si la llegada cae un día calendario DESPUÉS de la salida (el vuelo cruza medianoche) — la
    /// maqueta lo marca con un "+1" chiquito en rojo al lado de la hora de llegada. A diferencia de
    /// <see cref="FlightSegment.OutboundDepartureTime"/>/<c>ReturnDepartureTime</c> (solo HORA, sin
    /// fecha), <see cref="FlightSegment.DepartureTime"/>/<c>ArrivalTime</c> sí tienen fecha real: el
    /// cálculo es exacto, no una suposición.
    ///
    /// <para>Fix ronda 2 (2026-08-13): se agrega el guard de <see cref="LooksLikeMissingSchedule"/> — sin
    /// él, un tramo sin horarios cargados (salida 00:00 día 1, "llegada" 00:00 día 2 puesta por el
    /// formulario) mostraba un "+1" inventado, porque la fecha SÍ es "el día siguiente" aunque ninguna de
    /// las dos horas sea real.</para>
    /// </summary>
    public static bool IsNextDayArrival(DateTime departureTime, DateTime? arrivalTime)
    {
        if (!arrivalTime.HasValue) return false;
        if (IsSameInstantArrival(departureTime, arrivalTime.Value)) return false;
        if (LooksLikeMissingSchedule(departureTime, arrivalTime.Value)) return false;

        return arrivalTime.Value.Date > departureTime.Date;
    }

    /// <summary>
    /// True cuando la llegada es EXACTAMENTE el mismo instante que la salida (misma fecha y hora, al
    /// segundo). Pasa cuando el formulario copia la salida como valor de arranque de la llegada y el
    /// vendedor nunca lo completó de verdad — ningún vuelo real llega en el mismo segundo en que sale.
    /// En ese caso NO hay llegada real: se omite la hora de llegada, el "+1" y la duración (mostrarlos
    /// sería inventar un dato que nadie cargó, regla espejo decisión #8).
    /// </summary>
    public static bool IsSameInstantArrival(DateTime departureTime, DateTime arrivalTime)
        => arrivalTime == departureTime;

    /// <summary>
    /// True cuando NI la salida NI la llegada tienen una hora real cargada: las dos caen justo a las
    /// 00:00:00 en punto. Bug reportado por el dueño (12/08): un tramo cargado sin horarios rendía
    /// "00:00 [Directo] 00:00 +1 · 24h" — el formulario completa 00:00 por default cuando el vendedor no
    /// carga nada, y esas dos medianoches "de relleno" (aunque tengan fechas de calendario distintas)
    /// generaban un "+1" y una duración de 24h inventados.
    ///
    /// <para>Una medianoche real SOLA (una única punta, la otra con hora distinta de 00:00) sigue siendo
    /// un dato válido y se respeta — por eso se exige que las DOS estén en punto para sospechar "tramo sin
    /// cargar", nunca alcanza con una sola.</para>
    /// </summary>
    public static bool LooksLikeMissingSchedule(DateTime departureTime, DateTime arrivalTime)
        => departureTime.TimeOfDay == TimeSpan.Zero && arrivalTime.TimeOfDay == TimeSpan.Zero;

    /// <summary>
    /// True si corresponde imprimir la hora de SALIDA del tramo. Se omite únicamente en el caso
    /// "medianoche exacta en las dos puntas" (<see cref="LooksLikeMissingSchedule"/>) — el resto de las
    /// veces <see cref="FlightSegment.DepartureTime"/> es un dato real garantizado desde que el segmento
    /// existe (ver <c>CreateFlightRequest</c>), nunca se omite por las dudas.
    /// </summary>
    public static bool ShouldShowDepartureTime(DateTime departureTime, DateTime? arrivalTime)
        => !(arrivalTime.HasValue && LooksLikeMissingSchedule(departureTime, arrivalTime.Value));

    /// <summary>
    /// True si corresponde imprimir la hora de LLEGADA del tramo: hace falta que haya una llegada
    /// cargada, que no sea el mismo instante que la salida (<see cref="IsSameInstantArrival"/>) y que las
    /// dos puntas no parezcan "sin horario cargado" (<see cref="LooksLikeMissingSchedule"/>).
    /// </summary>
    public static bool ShouldShowArrivalTime(DateTime departureTime, DateTime? arrivalTime)
    {
        if (!arrivalTime.HasValue) return false;
        if (IsSameInstantArrival(departureTime, arrivalTime.Value)) return false;
        if (LooksLikeMissingSchedule(departureTime, arrivalTime.Value)) return false;

        return true;
    }

    /// <summary>
    /// True si el tramo tiene ALGÚN dato real para dibujar (hora de salida, hora de llegada, algún
    /// aeropuerto, o una duración calculable). Fix ronda 2 (2026-08-13, decisión firmada): un tramo
    /// cargado sin ningún dato real no debe imprimir una fila vacía o con basura — si esto da false, el
    /// renderer omite la fila entera (y si NINGÚN tramo tiene datos, la caja de vuelos completa no
    /// aparece).
    /// </summary>
    public static bool HasAnyVisibleFlightRowData(
        bool showDepartureTime, bool showArrivalTime, string? departureAirportLabel, string? arrivalAirportLabel, string? durationLabel)
        => showDepartureTime || showArrivalTime || departureAirportLabel is not null || arrivalAirportLabel is not null || durationLabel is not null;

    /// <summary>
    /// Destino que se imprime centrado bajo la banda del PDF. Sale del PRIMER hotel vivo (su ciudad) o,
    /// si no hay hotel, del PRIMER paquete vivo (su destino). Es texto RAW (sin transformar a
    /// mayúsculas: eso es una decisión de estilo del renderer, no de esta regla). Sin dato claro → null
    /// (el PDF omite el título, nunca inventa un destino).
    /// </summary>
    public static string? ResolveDestinationTitle(IEnumerable<HotelBooking>? hotels, IEnumerable<PackageBooking>? packages)
    {
        var hotelCity = hotels?
            .Where(IsLiveGenericService)
            .Select(h => h.City)
            .FirstOrDefault(city => !string.IsNullOrWhiteSpace(city));

        if (!string.IsNullOrWhiteSpace(hotelCity))
        {
            return hotelCity!.Trim();
        }

        var packageDestination = packages?
            .Where(IsLiveGenericService)
            .Select(p => p.Destination)
            .FirstOrDefault(destination => !string.IsNullOrWhiteSpace(destination));

        return string.IsNullOrWhiteSpace(packageDestination) ? null : packageDestination!.Trim();
    }

    // ============================================================================================
    // OPCIONES A/B/C: agrupa los servicios de un OptionGroup ambiguo (2+ alternativas vivas sin
    // resolver todavia) para que el PDF los liste uno debajo del otro. Reusa
    // ReservaMoneyCalculator.FindAmbiguousOptionGroups (fuente unica, la MISMA que decide que no suman
    // al total general) — aca solo agregamos el detalle de CADA candidato para poder imprimirlo.
    // ============================================================================================

    /// <summary>
    /// Un servicio candidato dentro de un grupo de opciones ambiguo: lo necesario para imprimir una fila
    /// "Hotel A — USD 1.200" en el PDF. <see cref="Kind"/> es la etiqueta legible del tipo de servicio
    /// (para que el renderer elija el icono/estilo), NUNCA el nombre interno de la clase C#.
    /// </summary>
    public sealed record QuoteOptionCandidate(string Kind, string? OptionLabel, string DisplayName, decimal SalePrice, string? Currency);

    /// <summary>
    /// Devuelve, agrupados por nombre de grupo normalizado, los candidatos VIVOS de cada OptionGroup
    /// todavía ambiguo (2+ alternativas sin resolver). Grupos ya resueltos (1 sola alternativa viva) NO
    /// aparecen acá — esos ya son un servicio normal y se imprimen en su sección de siempre (Hoteles,
    /// Vuelos, etc.), no como "opción".
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<QuoteOptionCandidate>> BuildAmbiguousOptionGroups(Reserva reserva)
    {
        ArgumentNullException.ThrowIfNull(reserva);

        var ambiguousGroups = ReservaMoneyCalculator.FindAmbiguousOptionGroups(reserva);
        var byGroup = new Dictionary<string, List<QuoteOptionCandidate>>(StringComparer.OrdinalIgnoreCase);

        if (ambiguousGroups.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<QuoteOptionCandidate>>();
        }

        void AddCandidate(string? optionGroup, bool isLive, Func<QuoteOptionCandidate> buildCandidate)
        {
            var normalized = OptionGroupRules.Normalize(optionGroup);
            if (normalized is null || !isLive || !ambiguousGroups.Contains(normalized))
            {
                return;
            }

            if (!byGroup.TryGetValue(normalized, out var list))
            {
                list = new List<QuoteOptionCandidate>();
                byGroup[normalized] = list;
            }

            list.Add(buildCandidate());
        }

        // "Vivo" = mismo predicado "cotizado" (no cancelado) que ReservaMoneyCalculator usa para decidir
        // si un servicio compite por su grupo — ver WorkflowStatusHelper (fuente única del mapeo de
        // estado). No se reimplementa la regla, solo se llama al mismo bloque canónico por tipo.
        if (reserva.HotelBookings != null)
            foreach (var hotel in reserva.HotelBookings)
                AddCandidate(hotel.OptionGroup, IsLiveGenericService(hotel),
                    () => new QuoteOptionCandidate("Hotel", hotel.OptionLabel, BuildHotelDisplayName(hotel), hotel.SalePrice, hotel.Currency));

        if (reserva.FlightSegments != null)
            foreach (var flight in reserva.FlightSegments)
                AddCandidate(flight.OptionGroup, IsLiveFlightService(flight),
                    () => new QuoteOptionCandidate("Vuelo", flight.OptionLabel, BuildFlightDisplayName(flight), flight.SalePrice, flight.Currency));

        if (reserva.TransferBookings != null)
            foreach (var transfer in reserva.TransferBookings)
                AddCandidate(transfer.OptionGroup, IsLiveGenericService(transfer),
                    () => new QuoteOptionCandidate("Traslado", transfer.OptionLabel, BuildTransferDisplayName(transfer), transfer.SalePrice, transfer.Currency));

        if (reserva.PackageBookings != null)
            foreach (var package in reserva.PackageBookings)
                AddCandidate(package.OptionGroup, IsLiveGenericService(package),
                    () => new QuoteOptionCandidate("Paquete", package.OptionLabel, package.PackageName, package.SalePrice, package.Currency));

        if (reserva.AssistanceBookings != null)
            foreach (var assistance in reserva.AssistanceBookings)
                AddCandidate(assistance.OptionGroup, IsLiveGenericService(assistance),
                    () => new QuoteOptionCandidate("Asistencia", assistance.OptionLabel, BuildAssistanceDisplayName(assistance), assistance.SalePrice, assistance.Currency));

        return byGroup.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<QuoteOptionCandidate>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Página 2 "INFORMACIÓN IMPORTANTE" (decisión firmada): filtra las condiciones (que ya vienen SOLO
    /// con texto cargado — ese filtro lo hace el caller antes) a las categorías de servicio PRESENTES en
    /// el presupuesto + Generales (que siempre corresponde si tiene texto). Lista vacía = la página 2 se
    /// omite entera (no existe una página con nada adentro).
    /// </summary>
    public static IReadOnlyList<BudgetConditionBlock> SelectRelevantConditions(Reserva reserva, IReadOnlyList<BudgetConditionBlock> conditions)
    {
        ArgumentNullException.ThrowIfNull(reserva);
        conditions ??= Array.Empty<BudgetConditionBlock>();

        if (conditions.Count == 0)
        {
            return Array.Empty<BudgetConditionBlock>();
        }

        var presentKinds = new HashSet<BudgetConditionBlockKind> { BudgetConditionBlockKind.General };
        if ((reserva.FlightSegments?.Count ?? 0) > 0) presentKinds.Add(BudgetConditionBlockKind.Flights);
        if ((reserva.HotelBookings?.Count ?? 0) > 0) presentKinds.Add(BudgetConditionBlockKind.Hotels);
        if ((reserva.TransferBookings?.Count ?? 0) > 0) presentKinds.Add(BudgetConditionBlockKind.Transfers);
        if ((reserva.PackageBookings?.Count ?? 0) > 0) presentKinds.Add(BudgetConditionBlockKind.Packages);
        if ((reserva.AssistanceBookings?.Count ?? 0) > 0) presentKinds.Add(BudgetConditionBlockKind.Assistances);

        return conditions.Where(c => presentKinds.Contains(c.Kind)).ToList();
    }

    private static string BuildHotelDisplayName(HotelBooking hotel)
        => string.IsNullOrWhiteSpace(hotel.RoomType) ? hotel.HotelName : $"{hotel.HotelName} – {hotel.RoomType}";

    private static string BuildFlightDisplayName(FlightSegment flight)
    {
        if (!string.IsNullOrWhiteSpace(flight.ProductName)) return flight.ProductName.Trim();

        var airline = flight.AirlineName ?? flight.AirlineCode;
        if (!string.IsNullOrWhiteSpace(airline) || !string.IsNullOrWhiteSpace(flight.FlightNumber))
            return $"{airline} {flight.FlightNumber}".Trim();

        return "Vuelo";
    }

    private static string BuildTransferDisplayName(TransferBooking transfer)
        => BuildTrasladoLine(transfer) ?? "Traslado";

    private static string BuildAssistanceDisplayName(AssistanceBooking assistance)
        => string.IsNullOrWhiteSpace(assistance.PlanType) ? "Asistencia" : assistance.PlanType.Trim();

    // --- "Vivo" (cotizado, no cancelado) por tipo — mismo mapeo que WorkflowStatusHelper/ReservaMoneyCalculator ---

    private static bool IsLiveGenericService(HotelBooking h) => WorkflowStatusHelper.CountsForQuotedTotal(WorkflowStatusHelper.MapGenericStatus(h.Status));
    private static bool IsLiveGenericService(TransferBooking t) => WorkflowStatusHelper.CountsForQuotedTotal(WorkflowStatusHelper.MapGenericStatus(t.Status));
    private static bool IsLiveGenericService(PackageBooking p) => WorkflowStatusHelper.CountsForQuotedTotal(WorkflowStatusHelper.MapGenericStatus(p.Status));
    private static bool IsLiveGenericService(AssistanceBooking a) => WorkflowStatusHelper.CountsForQuotedTotal(WorkflowStatusHelper.MapGenericStatus(a.Status));
    private static bool IsLiveFlightService(FlightSegment f) => WorkflowStatusHelper.CountsForQuotedTotal(WorkflowStatusHelper.MapFlightStatus(f.Status));
}

/// <summary>Precio ya resuelto para imprimir: el monto y si terminó siendo "por persona" o "total" (ver <see cref="QuoteBudgetPdfRules.ResolveDisplayPrice"/>).</summary>
public readonly record struct QuotePriceDisplay(decimal Amount, bool IsPerPerson);
