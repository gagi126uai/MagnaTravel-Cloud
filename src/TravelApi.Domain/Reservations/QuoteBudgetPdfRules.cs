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
    /// Línea de cuotas del hotel ("6 CUOTAS 280 USD", decisión firmada del dueño, 2026-08-13): SOLO se
    /// arma si el vendedor cargó los DOS datos (<see cref="HotelBooking.InstallmentsCount"/> Y
    /// <see cref="HotelBooking.InstallmentAmount"/>) — una cantidad de cuotas sin monto, o un monto sin
    /// cantidad, no dice nada útil y la regla espejo (decisión #8) prohíbe completar el que falta. Una
    /// cantidad de cuotas cargada en 0 o negativa (dato sin sentido, no debería pasar el form pero no se
    /// confía en el frontend) tampoco imprime línea.
    /// </summary>
    public static string? BuildInstallmentsLine(int? installmentsCount, decimal? installmentAmount, string? currency)
    {
        if (!installmentsCount.HasValue || !installmentAmount.HasValue) return null;
        if (installmentsCount.Value <= 0) return null;

        var amountLabel = BuildAmountLabel(installmentAmount.Value, currency);
        return $"{installmentsCount.Value} CUOTAS {amountLabel}";
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

    /// <summary>
    /// Línea del servicio "Otro" (<see cref="ServicioReserva"/>, el servicio genérico sin tipo propio):
    /// mismo criterio que <see cref="BuildTrasladoLine"/> — usa <c>Description</c> (el texto que carga el
    /// vendedor, mismo campo que usan las fichas del front) y cae a "Otro" cuando quedó vacío. A
    /// diferencia de los 5 servicios tipados, este NO tiene <c>ProductName</c>; <c>Description</c> es la
    /// ÚNICA fuente de nombre legible.
    /// </summary>
    public static string BuildOtroServiceDisplayName(ServicioReserva servicio)
    {
        ArgumentNullException.ThrowIfNull(servicio);
        return string.IsNullOrWhiteSpace(servicio.Description) ? "Otro" : servicio.Description.Trim();
    }

    // ============================================================================================
    // Bloque de vuelos REHECHO a la decisión firmada del dueño del 2026-08-13 ("PDF completo"): un
    // vuelo se dibuja como DOS FILAS COMPLETAS — IDA y VUELTA — calcando el ejemplo BAYAHIBE, cada una
    // con su hora de salida Y de llegada. Esto SUPERA el diseño de la ronda anterior (una fila por
    // "tramo" leyendo <see cref="FlightSegment.DepartureTime"/>/<c>ArrivalTime</c> como si fueran
    // horarios reales): esos dos campos SOLO guardan la FECHA de ida/vuelta desde la ficha
    // "producto-primero" (ver el comentario grande en <see cref="FlightSegment"/>), nunca una hora de
    // verdad — por eso el "+1"/duración de la ronda anterior podían salir mal para vuelos cargados por
    // esa ficha.
    //
    // Los horarios REALES de cada tramo viven en <see cref="FlightSegment.OutboundDepartureTime"/>/
    // <c>OutboundArrivalTime</c> (ida) y <c>ReturnDepartureTime</c>/<c>ReturnArrivalTime</c> (vuelta) —
    // los 4 son <c>TimeOnly?</c>, SIN fecha. Esto simplifica el cálculo de "+1"/duración: ya no hace
    // falta distinguir "medianoche real" de "medianoche de relleno" (el bug de la ronda anterior,
    // <c>LooksLikeMissingSchedule</c>) porque un <c>TimeOnly?</c> nulo es SIEMPRE "no cargado", nunca
    // un relleno ambiguo.
    // ============================================================================================

    /// <summary>
    /// Etiqueta de aeropuerto para UNA punta del tramo ("EZE · BUENOS AIRES"): código IATA + ciudad, en
    /// mayúsculas (así la pide la maqueta, chica y gris debajo de la hora). Cada dato es independiente:
    /// si el vendedor cargó uno solo de los dos, se muestra ese solo; sin ninguno de los dos → null (sin
    /// línea, nunca se inventa un aeropuerto). Se reusa tanto para la fila IDA (Origin/OriginCity) como
    /// para la fila VUELTA (Destination/DestinationCity, invertidos — ver <see cref="HasReturnLeg"/>).
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
    /// True si ESTE vuelo tiene una fila de VUELTA para imprimir: el vendedor cargó una fecha de vuelta
    /// (<see cref="FlightSegment.ArrivalTime"/> — que, por el reparto firmado, guarda la FECHA de vuelta,
    /// no una hora de llegada). Sin fecha de vuelta cargada, el vuelo es de ida sola: solo se imprime la
    /// fila IDA (regla espejo, decisión #8: no se inventa una vuelta que nadie cargó).
    /// </summary>
    public static bool HasReturnLeg(FlightSegment flight)
    {
        ArgumentNullException.ThrowIfNull(flight);
        return flight.ArrivalTime.HasValue;
    }

    /// <summary>
    /// Texto para el lado de SALIDA de una fila de vuelo (ida o vuelta): la hora real cargada ("08:30")
    /// si el vendedor la anotó, o si no, la FECHA corta del tramo ("10/02/2027") — nunca "00:00"
    /// inventado. NUNCA null: <paramref name="fallbackLegDate"/> siempre tiene un valor real cuando esta
    /// fila se decide dibujar (la fecha de ida sale de <see cref="FlightSegment.DepartureTime"/>,
    /// obligatoria; la fecha de vuelta sale de <c>ArrivalTime</c>, que ya se verificó con
    /// <see cref="HasReturnLeg"/> antes de llamar acá).
    /// </summary>
    public static string BuildFlightLegDepartureText(TimeOnly? structuredDepartureTime, DateTime fallbackLegDate)
    {
        return structuredDepartureTime.HasValue
            ? structuredDepartureTime.Value.ToString("HH:mm")
            : $"{fallbackLegDate:dd/MM/yyyy}";
    }

    /// <summary>
    /// Texto para el lado de LLEGADA de una fila de vuelo. Null cuando el vendedor no cargó la hora de
    /// llegada de ESTE tramo — a diferencia de la salida, acá NO hay fallback de fecha: el tramo ya tiene
    /// UNA sola fecha (la que se usó del lado de la salida cuando falta la hora), repetirla del lado de
    /// la llegada no aporta nada y un tramo "solo con hora de salida" es un dato válido y frecuente
    /// (vuelo con salida confirmada, llegada todavía sin anotar).
    /// </summary>
    public static string? BuildFlightLegArrivalText(TimeOnly? structuredArrivalTime)
        => structuredArrivalTime?.ToString("HH:mm");

    /// <summary>
    /// True si la llegada cae DESPUÉS de medianoche respecto de la salida (el vuelo cruza el día) — la
    /// maqueta lo marca con un "+1" chiquito en rojo al lado de la hora de llegada. Al ser horas SIN
    /// fecha (<c>TimeOnly</c>), alcanza con comparar "¿la llegada es más temprana en el reloj que la
    /// salida?": si sale 23:15 y llega 01:40, forzosamente llegó al día siguiente. Null en cualquiera de
    /// las dos puntas → false (no hay dato real para marcar el cruce, nunca se inventa el badge).
    /// </summary>
    public static bool IsFlightLegNextDay(TimeOnly? departureTime, TimeOnly? arrivalTime)
    {
        if (!departureTime.HasValue || !arrivalTime.HasValue) return false;
        return arrivalTime.Value < departureTime.Value;
    }

    /// <summary>
    /// Duración del tramo ("3h 45m") a partir de las dos horas cargadas. Null si falta alguna (nunca se
    /// inventa una duración con un solo dato). Si la llegada "parece" anterior a la salida
    /// (<see cref="IsFlightLegNextDay"/>, cruce de medianoche) se le suman 24hs antes de restar, para no
    /// mostrar nunca una duración negativa.
    /// </summary>
    public static string? BuildFlightLegDuration(TimeOnly? departureTime, TimeOnly? arrivalTime)
    {
        if (!departureTime.HasValue || !arrivalTime.HasValue) return null;

        var duration = arrivalTime.Value - departureTime.Value;
        if (duration < TimeSpan.Zero)
        {
            duration += TimeSpan.FromHours(24);
        }

        var hours = (int)duration.TotalHours;
        var minutes = duration.Minutes;

        return minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
    }

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
