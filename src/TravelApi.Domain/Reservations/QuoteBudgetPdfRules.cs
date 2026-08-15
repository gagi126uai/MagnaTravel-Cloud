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
    /// Subtítulo del bloque de hotel en la maqueta minimalista elegante (spec 2026-08-14 §1):
    /// "Junior Suite · All inclusive · 7 noches". Junta habitación + régimen + noches, cada parte SOLO
    /// si el vendedor la cargó (regla espejo, decisión #8) — sin ninguna de las tres, no hay subtítulo.
    /// </summary>
    public static string? BuildHotelSubtitleLine(HotelBooking hotel)
    {
        ArgumentNullException.ThrowIfNull(hotel);
        var parts = new List<string>();

        var roomDetail = BuildHotelRoomDetail(hotel);
        if (roomDetail is not null) parts.Add(roomDetail);

        if (!string.IsNullOrWhiteSpace(hotel.MealPlan)) parts.Add(hotel.MealPlan.Trim());

        if (hotel.Nights > 0)
        {
            parts.Add(hotel.Nights == 1 ? "1 noche" : $"{hotel.Nights} noches");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>"Doble – Superior" (habitación + categoría). Cada dato es independiente: sin ninguno, null.</summary>
    private static string? BuildHotelRoomDetail(HotelBooking hotel)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(hotel.RoomType)) parts.Add(hotel.RoomType.Trim());
        if (!string.IsNullOrWhiteSpace(hotel.RoomCategory)) parts.Add(hotel.RoomCategory.Trim());
        return parts.Count == 0 ? null : string.Join(" – ", parts);
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

    // ============================================================================================
    // Ronda 2 (decisión firmada del dueño, 2026-08-14, spec §6): escalas SIMPLES por tramo (ida y vuelta
    // por separado). El chip de escala PISA al "Directo" — nunca conviven — y debajo de las filas de
    // vuelo va un renglón apagado por tramo CON escala, con "Ida:"/"Vuelta:" solo si AMBOS tramos tienen.
    // ============================================================================================

    /// <summary>
    /// Texto del chip de UN tramo (ida o vuelta): si el vendedor cargó escalas (<paramref name="stopsCount"/>
    /// mayor a 0), el chip de escala PISA al de "Directo" — "1 escala" / "N escalas" a secas, SIN el
    /// lugar (decisión de diseño, fix post-inspección visual 2026-08-15: el chip vive en una columna de
    /// ancho fijo de la grilla del vuelo y "1 escala · Lima (LIM)" se partía en dos renglones dentro de
    /// la píldora; el lugar ya se lee en el renglón de detalle debajo de las filas, ver
    /// <see cref="BuildFlightStopDetailLines"/>, así que no hace falta repetirlo acá). Sin escalas
    /// cargadas, cae al comportamiento de siempre: "Directo" si <paramref name="isDirect"/> es true, o
    /// ningún chip.
    /// </summary>
    public static string? ResolveFlightLegChipText(bool? isDirect, int? stopsCount)
    {
        if (stopsCount.HasValue && stopsCount.Value > 0)
        {
            return stopsCount.Value == 1 ? "1 escala" : $"{stopsCount.Value} escalas";
        }

        return isDirect == true ? "Directo" : null;
    }

    /// <summary>
    /// Renglón de detalle de la escala de UN tramo ("Escala en Lima (LIM) · espera 2h 10m"). Cada parte
    /// (lugar/espera) se omite si no está cargada; sin ninguna de las dos (el vendedor solo cargó la
    /// CANTIDAD de escalas, sin detalle), no hay nada más que decir y el renglón se omite entero — el
    /// chip ya avisó "N escalas", este renglón es solo para el DETALLE extra.
    /// </summary>
    private static string? BuildFlightLegStopDetailText(int? stopsCount, string? stopPlace, string? stopWait)
    {
        if (!stopsCount.HasValue || stopsCount.Value <= 0) return null;

        var parts = new List<string>();
        var trimmedPlace = string.IsNullOrWhiteSpace(stopPlace) ? null : stopPlace.Trim();
        var trimmedWait = string.IsNullOrWhiteSpace(stopWait) ? null : stopWait.Trim();

        if (trimmedPlace is not null) parts.Add($"Escala en {trimmedPlace}");
        if (trimmedWait is not null) parts.Add($"espera {trimmedWait}");

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// Arma los renglones de detalle de escala de UN vuelo (0, 1 o 2 líneas: ida y/o vuelta). Cuando
    /// AMBOS tramos tienen detalle de escala, cada línea se prefija "Ida: "/"Vuelta: " para no
    /// confundirlas; con un solo tramo con escala, el prefijo sobra (ya está claro de cuál se habla,
    /// es la única línea del bloque).
    /// </summary>
    public static IReadOnlyList<string> BuildFlightStopDetailLines(FlightSegment flight)
    {
        ArgumentNullException.ThrowIfNull(flight);

        var outboundText = BuildFlightLegStopDetailText(flight.OutboundStopsCount, flight.OutboundStopPlace, flight.OutboundStopWait);
        var returnText = BuildFlightLegStopDetailText(flight.ReturnStopsCount, flight.ReturnStopPlace, flight.ReturnStopWait);
        var bothLegsHaveDetail = outboundText is not null && returnText is not null;

        var lines = new List<string>();
        if (outboundText is not null) lines.Add(bothLegsHaveDetail ? $"Ida: {outboundText}" : outboundText);
        if (returnText is not null) lines.Add(bothLegsHaveDetail ? $"Vuelta: {returnText}" : returnText);

        return lines;
    }

    // ============================================================================================
    // Ronda 2 (2026-08-14): sección PASAJEROS del riel — nombres + edad de los menores.
    // ============================================================================================

    /// <summary>
    /// Fecha contra la que se calcula la edad de un pasajero para la sección PASAJEROS: la fecha de
    /// SALIDA del viaje si ya se conoce (es la fecha real en que el menor va a viajar), o HOY si
    /// todavía no hay fecha de salida cargada — nunca se deja la edad sin calcular por falta de
    /// referencia.
    /// </summary>
    public static DateTime ResolvePassengerAgeReferenceDate(DateTime? tripStartDate)
        => (tripStartDate ?? DateTime.UtcNow).Date;

    /// <summary>
    /// Edad en años cumplidos de un pasajero a la fecha de referencia (resta simple de años, corregida
    /// si todavía no pasó el cumpleaños de ese año). Null cuando no hay fecha de nacimiento cargada, o
    /// cuando el cálculo da negativo (fecha de nacimiento posterior a la referencia — dato inconsistente,
    /// no se inventa una edad rara).
    /// </summary>
    public static int? ComputePassengerAge(DateTime? birthDate, DateTime referenceDate)
    {
        if (!birthDate.HasValue) return null;

        var birth = birthDate.Value.Date;
        var reference = referenceDate.Date;
        var age = reference.Year - birth.Year;
        if (reference < birth.AddYears(age)) age--;

        return age < 0 ? null : age;
    }

    /// <summary>
    /// Línea de UN pasajero para la sección PASAJEROS: el nombre solo, o "{nombre} · N años" cuando es
    /// menor de 18 (la maqueta solo destaca la edad de los menores, no la de un adulto). JAMÁS se
    /// agregan datos de documento (decisión firmada, spec §6: "SIN documentos").
    /// </summary>
    public static string BuildPassengerDisplayLine(string fullName, DateTime? birthDate, DateTime ageReferenceDate)
    {
        var trimmedName = string.IsNullOrWhiteSpace(fullName) ? string.Empty : fullName.Trim();
        var age = ComputePassengerAge(birthDate, ageReferenceDate);

        return age.HasValue && age.Value < 18 ? $"{trimmedName} · {age.Value} años" : trimmedName;
    }

    // ============================================================================================
    // Ronda 2 (2026-08-14): cabecera "Preparado para {cliente}" + etiqueta de tipo por ítem en OTROS.
    // ============================================================================================

    /// <summary>
    /// Cuarta línea del bloque derecho de la cabecera: "Preparado para {cliente}" con el nombre del
    /// pagador de la reserva. Sin pagador cargado, la línea entera se omite (null) — nunca se dibuja un
    /// "Preparado para" sin nombre.
    /// </summary>
    public static string? BuildPreparedForLine(string? payerFullName)
    {
        var trimmed = string.IsNullOrWhiteSpace(payerFullName) ? null : payerFullName.Trim();
        return trimmed is null ? null : $"Preparado para {trimmed}";
    }

    /// <summary>
    /// Etiqueta de tipo de negocio para UN ítem de la sección OTROS ("ASISTENCIA AL VIAJERO", "PAQUETE",
    /// "EXCURSIÓN"...), a partir del <see cref="ServicioReserva.ServiceType"/> del servicio genérico —
    /// el MISMO vocabulario de negocio que ya usa <see cref="BuildAmbiguousOptionGroups"/> para las
    /// OPCIONES (nunca el nombre de una clase C#). Un tipo no reconocido (o "Otro") cae a "SERVICIO": es
    /// la única etiqueta neutra que sigue siendo legible para el cliente sin inventar una categoría que
    /// el vendedor no cargó.
    /// </summary>
    public static string ResolveGenericServiceTypeLabel(string? serviceType)
    {
        var normalized = string.IsNullOrWhiteSpace(serviceType) ? null : serviceType.Trim();

        return normalized switch
        {
            ServiceTypes.Flight => "AÉREO",
            ServiceTypes.Hotel => "HOTEL",
            ServiceTypes.Transfer => "TRASLADO",
            ServiceTypes.Insurance => "ASISTENCIA AL VIAJERO",
            ServiceTypes.Excursion => "EXCURSIÓN",
            ServiceTypes.Package => "PAQUETE",
            _ => "SERVICIO",
        };
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

    // ============================================================================================
    // Maqueta "minimalista elegante" (spec firmada 2026-08-14, docs/ux/2026-08-14-spec-pdf-minimalista-
    // elegante.md): funciones PURAS nuevas para el hero, la tarjeta de total y la paleta por destino.
    // Mismo criterio de siempre — nada se inventa, todo sale de datos ya cargados.
    // ============================================================================================

    /// <summary>Meses cortos en castellano SIN punto ("feb", no "feb."), para la línea meta del hero.</summary>
    private static readonly string[] ShortMonthNamesEs =
    {
        "ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic",
    };

    /// <summary>
    /// Tamaño del destino en el hero según su largo (§1 de la spec): 55pt hasta 14 caracteres, 40pt hasta
    /// 24, 30pt si es más largo — un nombre de destino corto ("PUNTA CANA") se puede permitir ser gigante
    /// sin desbordar la página; uno largo ("SAN CARLOS DE BARILOCHE") necesita achicarse.
    /// </summary>
    public static float ResolveHeroDestinationFontSize(string destinationTitle)
    {
        ArgumentNullException.ThrowIfNull(destinationTitle);
        var length = destinationTitle.Trim().Length;

        if (length <= 14) return 55f;
        if (length <= 24) return 40f;
        return 30f;
    }

    /// <summary>
    /// Eyebrow del hero ("PROPUESTA DE VIAJE" o "PROPUESTA DE VIAJE · 7 NOCHES"): las noches solo se
    /// agregan si se conocen (mismo dato que ya usa <see cref="BuildSalidaLine"/> del lado del hotel) —
    /// sin hotel cargado, o sin <c>Nights</c> cargado en ese hotel, el eyebrow sale sin la parte de
    /// noches en vez de inventar un número.
    /// </summary>
    public static string BuildHeroEyebrowText(int? hotelNights)
    {
        const string baseText = "PROPUESTA DE VIAJE";
        if (!hotelNights.HasValue || hotelNights.Value <= 0) return baseText;

        var nightsLabel = hotelNights.Value == 1 ? "1 NOCHE" : $"{hotelNights.Value} NOCHES";
        return $"{baseText} · {nightsLabel}";
    }

    /// <summary>
    /// Línea meta del hero ("27 feb — 6 mar 2027 · 2 pasajeros · República Dominicana"): junta SOLO las
    /// partes con dato real, separadas por " · " — sin fechas, sin pasajeros cargados (0) o sin país, esa
    /// parte puntual no aparece (regla espejo, decisión #8: nunca se completa lo que nadie cargó).
    /// </summary>
    public static string? BuildHeroMetaLine(DateTime? startDate, DateTime? endDate, int totalPassengers, string? country)
    {
        var parts = new List<string>();

        var dateRange = BuildHeroDateRange(startDate, endDate);
        if (dateRange is not null) parts.Add(dateRange);

        if (totalPassengers > 0)
        {
            parts.Add(totalPassengers == 1 ? "1 pasajero" : $"{totalPassengers} pasajeros");
        }

        var trimmedCountry = string.IsNullOrWhiteSpace(country) ? null : country.Trim();
        if (trimmedCountry is not null) parts.Add(trimmedCountry);

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// "27 feb — 6 mar 2027": el año se imprime UNA sola vez, al final, salvo que el viaje cruce de año
    /// (ahí se imprime en las dos puntas para que no quede ambiguo). Si además cae dentro del mismo mes,
    /// el mes tampoco se repite ("10 — 15 feb 2027"). Sin las dos fechas cargadas → null.
    /// </summary>
    private static string? BuildHeroDateRange(DateTime? startDate, DateTime? endDate)
    {
        if (!startDate.HasValue || !endDate.HasValue) return null;

        var start = startDate.Value.Date;
        var end = endDate.Value.Date;
        var startMonth = ShortMonthNamesEs[start.Month - 1];
        var endMonth = ShortMonthNamesEs[end.Month - 1];

        if (start.Year != end.Year)
        {
            return $"{start.Day} {startMonth} {start.Year} — {end.Day} {endMonth} {end.Year}";
        }

        if (start.Month == end.Month)
        {
            return $"{start.Day} — {end.Day} {endMonth} {end.Year}";
        }

        return $"{start.Day} {startMonth} — {end.Day} {endMonth} {end.Year}";
    }

    /// <summary>
    /// Ciudades/destinos de los servicios de la reserva, para prestarle contexto a la IA que elige la
    /// paleta de color por destino (§5 de la spec) — SOLO ciudades, nada de pasajeros ni datos internos
    /// (gate data-exposure: lo que entra acá viaja tal cual al prompt del modelo).
    /// </summary>
    public static IReadOnlyList<string> CollectDestinationCityHints(Reserva reserva)
    {
        ArgumentNullException.ThrowIfNull(reserva);

        var hints = new List<string>();

        foreach (var hotel in reserva.HotelBookings ?? new List<HotelBooking>())
        {
            if (!IsLiveGenericService(hotel)) continue;
            if (!string.IsNullOrWhiteSpace(hotel.City)) hints.Add(hotel.City.Trim());
        }

        foreach (var package in reserva.PackageBookings ?? new List<PackageBooking>())
        {
            if (!IsLiveGenericService(package)) continue;
            if (!string.IsNullOrWhiteSpace(package.Destination)) hints.Add(package.Destination.Trim());
        }

        foreach (var flight in reserva.FlightSegments ?? new List<FlightSegment>())
        {
            if (!IsLiveFlightService(flight)) continue;
            if (!string.IsNullOrWhiteSpace(flight.DestinationCity)) hints.Add(flight.DestinationCity.Trim());
        }

        return hints
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6) // tope chico: es contexto para clasificar en UNA palabra, no un volcado del itinerario.
            .ToList();
    }

    /// <summary>
    /// Nota chica de la tarjeta de total ("Incluye vuelos, hotel y traslados."): se arma SOLO con las
    /// secciones que el renderer efectivamente dibujó arriba — nunca se afirma que algo está incluido si
    /// no hay una sección real con esa plata sumada al total.
    /// </summary>
    public static string? BuildTotalCardIncludesNote(bool hasFlights, bool hasHotel, bool hasTransfers, bool hasOthers)
    {
        var parts = new List<string>();
        if (hasFlights) parts.Add("vuelos");
        if (hasHotel) parts.Add("hotel");
        if (hasTransfers) parts.Add("traslados");
        if (hasOthers) parts.Add("otros servicios");

        return parts.Count == 0 ? null : "Incluye " + JoinHumanList(parts) + ".";
    }
}

/// <summary>Precio ya resuelto para imprimir: el monto y si terminó siendo "por persona" o "total" (ver <see cref="QuoteBudgetPdfRules.ResolveDisplayPrice"/>).</summary>
public readonly record struct QuotePriceDisplay(decimal Amount, bool IsPerPerson);
