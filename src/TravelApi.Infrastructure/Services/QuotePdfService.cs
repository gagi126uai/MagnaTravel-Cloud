using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Renderer del PDF de presupuesto (obra "PDF de presupuesto", maqueta v2 firmada por el dueño,
/// 2026-08-11/12). Espejo de <see cref="InvoicePdfService"/>: recibe TODO ya cargado (reserva, config de
/// agencia, condiciones, logo) y solo arma bytes con QuestPDF — no toca la base de datos.
///
/// <para>La lógica de "qué se muestra / qué se omite / con qué número" vive en
/// <see cref="QuoteBudgetPdfRules"/> (funciones puras, testeables sin generar un PDF). Esta clase es un
/// renderer fino: pinta lo que esa clase ya decidió.</para>
/// </summary>
public class QuotePdfService : IQuotePdfService
{
    // Colores de la maqueta v2 firmada (12/08/2026). Los únicos configurables por la agencia son el
    // color de banda (AgencySettings.PdfBandColorHex) y su fallback; el resto son fijos de la maqueta.
    private static readonly Color DefaultBandColor = Color.FromHex("#0e3a4f");
    private static readonly Color DestinationTitleColor = Color.FromHex("#1c6b8a");
    private static readonly Color FlightBoxBorderColor = Color.FromHex("#dfe3e6");
    private static readonly Color DirectChipBorderColor = Color.FromHex("#9fd6c6");
    private static readonly Color DirectChipTextColor = Color.FromHex("#2c8a6e");
    private static readonly Color DirectChipBackgroundColor = Color.FromHex("#f4fbf8");
    private static readonly Color FlightMutedTextColor = Color.FromHex("#8a9199");
    private static readonly Color NextDayBadgeColor = Color.FromHex("#d65a5a");
    private static readonly Color FooterGrayColor = Color.FromHex("#555555");

    // Fix ronda 2 (2026-08-13): margen izquierdo inconsistente reportado por el dueño en PROD — las
    // líneas SALIDA/EQUIPAJE/TRASLADO/HOTELES arrancaban pegadas al borde de la hoja porque vivían en
    // page.Header() (sin padding propio) mientras el resto del cuerpo (caja de vuelos, hoteles, formas de
    // pago) vivía en page.Content() con 30pt de margen. La maqueta firmada usa el MISMO padding en TODO
    // el cuerpo, equivalente a 56px de HTML (56px * 72pt/96px = 42pt, conversión estándar CSS→PDF). Se
    // deja como constante única para que ningún bloque nuevo pueda volver a quedar desalineado por usar
    // un número "a mano" distinto. NO se aplica a la banda de color (ComposeBand): esa es decorativa y
    // va de borde a borde a propósito, igual que en la maqueta.
    private const float ContentHorizontalPadding = 42f;

    // ============================================================================================
    // Íconos de la maqueta v2 (2026-08-13, ronda "recalcar la maqueta"): dibujados como SVG inline, NO
    // como texto con glifos ("★", "✈"). BUG CRÍTICO de la versión anterior: la fuente por defecto de
    // QuestPDF (ver nota de DefaultTextStyle mas abajo, el contenedor de producción no instala fuentes
    // de Microsoft) no trae esos glifos, y salían como cuadraditos rotos ("tofu"). Un SVG con un <path>
    // explícito SIEMPRE dibuja la forma pedida, sin depender de qué fuente esté instalada.
    // ============================================================================================

    // Ícono compuesto (círculo gris + avioncito) de la fila de vuelos. QuestPDF no trae un "círculo"
    // como forma primitiva lista para usar — es más simple y robusto resolver las DOS formas (círculo +
    // avión) en UN solo SVG chico que dibujarlas con dos elementos QuestPDF distintos superpuestos.
    private const string FlightIconSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 30 30">
          <circle cx="15" cy="15" r="15" fill="#f1f3f5"/>
          <g transform="translate(7,7) scale(0.6667)">
            <path fill="#1c6b8a" d="M21 16v-2l-8-5V3.5c0-.83-.67-1.5-1.5-1.5S10 2.67 10 3.5V9l-8 5v2l8-2.5V19l-2.5 1.5V22l3.5-1 3.5 1v-1.5L13 19v-5.5l8 2.5z"/>
          </g>
        </svg>
        """;

    // Estrella de 5 puntas rellena, color de marca. Una por punto en <c>ComposeStarRating</c>.
    private const string StarSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
          <path fill="#1c6b8a" d="M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z"/>
        </svg>
        """;

    // ============================================================================================
    // Íconos de equipaje (fix ronda 2.1, 2026-08-13): el dueño rechazó la valija única repetida ("esa
    // valija de mierda... yo te mostré algo y vos mostrás otra cosa") — la maqueta pide TRES conceptos
    // DISTINTOS y bien dibujados, SIEMPRE los tres presentes en la fila del vuelo, en este orden: mochila
    // (equipaje personal), equipaje de mano (carry-on, dibujado MÁS CHICO), valija despachada (mismo
    // dibujo que el carry-on, a tamaño completo — se distinguen por tamaño, como 🧳 chico vs grande).
    // El que SÍ está incluido en la tarifa va gris pleno; el que NO, va "fantasma" (mismo path, bien
    // tenue) — así el cliente ve de un vistazo qué entra y qué no, en vez de mostrar solo los que aplican.
    // ============================================================================================

    private const string BackpackPathData =
        "M20 8v12c0 1.1-.9 2-2 2H6c-1.1 0-2-.9-2-2V8c0-1.86 1.28-3.41 3-3.86V2h3v2h4V2h3v2.14c1.72.45 3 2 3 3.86zM6 12v2h10v2h2v-4H6z";

    private const string LuggagePathData =
        "M17 6h-2V3c0-.55-.45-1-1-1h-4c-.55 0-1 .45-1 1v3H7c-1.1 0-2 .9-2 2v11c0 1.1.9 2 2 2 0 .55.45 1 1 1s1-.45 1-1h6c0 .55.45 1 1 1s1-.45 1-1c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2zM9.5 18H8V9h1.5v9zm3.25 0h-1.5V9h1.5v9zm.75-12h-3V3.5h3V6zm3 12H15V9h1.5v9z";

    /// <summary>
    /// Arma el SVG de un ícono de equipaje con el estado incluido/no-incluido. Se genera el string acá
    /// (en vez de tener 2 constantes fijas por ícono) porque QuestPDF no permite "teñir" un SVG ya armado
    /// — hay que mandarle el color/opacidad correctos DESDE el path original en cada llamada.
    /// </summary>
    private static string BuildLuggageIconSvg(string pathData, bool included)
    {
        var fillOpacity = included ? "1" : "0.22"; // "fantasma": mismo dibujo, bien tenue -- nunca se oculta el ícono entero.
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
              <path fill="#5b6067" fill-opacity="{fillOpacity}" d="{pathData}"/>
            </svg>
            """;
    }

    public QuotePdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] GenerateQuotePdf(
        Reserva reserva,
        AgencySettings agencySettings,
        IReadOnlyList<BudgetConditionBlock> conditions,
        byte[]? logoBytes,
        bool porPersona,
        int cantidadPasajerosCargados)
    {
        ArgumentNullException.ThrowIfNull(reserva);
        ArgumentNullException.ThrowIfNull(agencySettings);
        conditions ??= Array.Empty<BudgetConditionBlock>();

        var bandColor = string.IsNullOrWhiteSpace(agencySettings.PdfBandColorHex)
            ? DefaultBandColor
            : Color.FromHex(agencySettings.PdfBandColorHex);

        // Fuente única de "qué grupos siguen ambiguos" (la MISMA que decide que no suman al total
        // general en ReservaMoneyCalculator) y de "qué candidatos tiene cada grupo" (para imprimirlos).
        var ambiguousGroups = ReservaMoneyCalculator.FindAmbiguousOptionGroups(reserva);
        var optionGroups = QuoteBudgetPdfRules.BuildAmbiguousOptionGroups(reserva);

        // Página 2 solo existe si hay ALGO que mostrar (decisión firmada: página entera ausente si no
        // corresponde ninguna condición). Se calcula ANTES de armar el documento para decidir si se
        // agrega el segundo Page().
        var relevantConditions = QuoteBudgetPdfRules.SelectRelevantConditions(reserva, conditions);

        var document = Document.Create(builder =>
        {
            builder.Page(page => ComposeMainPage(
                page, reserva, agencySettings, bandColor, logoBytes,
                ambiguousGroups, optionGroups, porPersona, cantidadPasajerosCargados));

            if (relevantConditions.Count > 0)
            {
                builder.Page(page => ComposeConditionsPage(page, agencySettings, bandColor, logoBytes, relevantConditions));
            }
        });

        return document.GeneratePdf();
    }

    // ============================================================================================
    // PÁGINA 1: banda + destino + datos + vuelos + hoteles/otros servicios + opciones + formas de pago.
    // ============================================================================================

    private void ComposeMainPage(
        PageDescriptor page,
        Reserva reserva,
        AgencySettings agencySettings,
        Color bandColor,
        byte[]? logoBytes,
        HashSet<string> ambiguousGroups,
        IReadOnlyDictionary<string, IReadOnlyList<QuoteBudgetPdfRules.QuoteOptionCandidate>> optionGroups,
        bool porPersona,
        int cantidadPasajerosCargados)
    {
        page.Size(PageSizes.A4);
        page.PageColor(Colors.White);
        // La maqueta pide "estilo Calibri", pero NO fijamos ese nombre de fuente a proposito: el
        // contenedor de produccion (ver src/TravelApi/Dockerfile) solo instala fonts-dejavu-core, no
        // fuentes de Microsoft — pedir "Calibri" ahi rompería la generación del PDF (o la reemplazaría
        // en silencio por un fallback feo). Se deja SIN FontFamily, igual que InvoicePdfService: QuestPDF
        // usa su fuente por defecto (ya validada en producción por las facturas).
        page.DefaultTextStyle(x => x.FontSize(13));

        page.Header().Column(headerColumn =>
        {
            headerColumn.Item().Element(e => ComposeBand(e, bandColor, logoBytes, heightPt: 74));
            headerColumn.Item().PaddingTop(14).PaddingHorizontal(ContentHorizontalPadding).Element(e => ComposeDestinoYDatos(e, reserva));
        });

        page.Content().PaddingHorizontal(ContentHorizontalPadding).PaddingTop(10).Column(content =>
        {
            ComposeVuelos(content, reserva, ambiguousGroups);
            ComposeHoteles(content, reserva, ambiguousGroups, porPersona, cantidadPasajerosCargados);
            ComposeOtrosServicios(content, reserva, ambiguousGroups, porPersona, cantidadPasajerosCargados);
            ComposeOpciones(content, optionGroups, porPersona, cantidadPasajerosCargados);
            ComposeFormasDePago(content, reserva, agencySettings);
        });

        page.Footer().Element(e => ComposeFooter(e, agencySettings));
    }

    /// <summary>
    /// Banda superior de color (color de la agencia o el default de la maqueta), con el logo alineado a
    /// la derecha SI la agencia cargó uno. Sin logo cargado → banda sola, nunca un placeholder inventado
    /// (regla madre de la obra, decisión #8).
    /// </summary>
    private void ComposeBand(IContainer container, Color bandColor, byte[]? logoBytes, float heightPt)
    {
        // AlignRight sobre el CONTENEDOR entero (no un Row con un espaciador vacío): mas simple y evita
        // depender de que QuestPDF reserve espacio para un item de fila sin contenido.
        var band = container.Height(heightPt).Background(bandColor).Padding(12).AlignRight();

        if (logoBytes is not { Length: > 0 })
        {
            return; // sin logo cargado -> banda sola, nunca un placeholder inventado.
        }

        try
        {
            band.Image(logoBytes).FitHeight();
        }
        catch
        {
            // Un logo corrupto/con un formato que QuestPDF no puede decodificar NO debe romper el PDF
            // entero — se omite en silencio (la banda queda sola).
        }
    }

    /// <summary>
    /// Destino centrado + las 4 líneas de datos (SALIDA/EQUIPAJE/TRASLADO/HOTELES-Régimen). Cada línea es
    /// independiente: si su dato de origen no está cargado, esa línea puntual no se dibuja (espejo).
    /// </summary>
    private void ComposeDestinoYDatos(IContainer container, Reserva reserva)
    {
        var destinationTitle = QuoteBudgetPdfRules.ResolveDestinationTitle(reserva.HotelBookings, reserva.PackageBookings);
        var firstLiveHotel = FirstLiveHotel(reserva);
        var firstLiveFlight = FirstLiveFlight(reserva);
        var firstLiveTransfer = FirstLiveTransfer(reserva);

        var salidaLine = QuoteBudgetPdfRules.BuildSalidaLine(reserva.StartDate, reserva.EndDate, firstLiveHotel?.Nights);
        var equipajeLine = QuoteBudgetPdfRules.BuildEquipajeLine(firstLiveFlight);
        var trasladoLine = QuoteBudgetPdfRules.BuildTrasladoLine(firstLiveTransfer);
        var regimenLine = firstLiveHotel is null ? null : firstLiveHotel.MealPlan;

        container.Column(column =>
        {
            if (!string.IsNullOrWhiteSpace(destinationTitle))
            {
                // Peso normal a proposito (maqueta v2): el destino se destaca por color y tamaño, NO por
                // negrita (a diferencia de los títulos de sección, que sí llevan .Bold()).
                column.Item().AlignCenter().Text(destinationTitle.ToUpperInvariant())
                    .FontSize(27).FontColor(DestinationTitleColor);
            }

            column.Item().PaddingTop(10).Column(dataLines =>
            {
                AddDataLine(dataLines, "SALIDA:", salidaLine);
                AddDataLine(dataLines, "EQUIPAJE:", equipajeLine);
                AddDataLine(dataLines, "TRASLADO:", trasladoLine);
                AddDataLine(dataLines, "HOTELES – Régimen:", regimenLine);
            });
        });
    }

    /// <summary>Una línea "ETIQUETA: valor." — se omite entera si <paramref name="value"/> es null/vacío.</summary>
    private void AddDataLine(ColumnDescriptor column, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        column.Item().PaddingBottom(2).Text(text =>
        {
            text.Span(label + " ").Bold().FontSize(13);
            text.Span(value).FontSize(13);
        });
    }

    // ============================================================================================
    // VUELOS (obra "PDF completo", decisión firmada del dueño, 2026-08-13): recuadro con DOS FILAS
    // COMPLETAS por vuelo vivo — IDA y VUELTA — calcando el ejemplo BAYAHIBE. Cada vuelo cargado imprime
    // SIEMPRE su fila de ida; la fila de vuelta solo aparece si el vendedor cargó una fecha de vuelta
    // (ver QuoteBudgetPdfRules.HasReturnLeg). Los vuelos que pertenecen a un grupo ambiguo (2+
    // alternativas sin resolver) se listan en OPCIONES, no acá.
    // ============================================================================================

    private void ComposeVuelos(ColumnDescriptor column, Reserva reserva, HashSet<string> ambiguousGroups)
    {
        // "Un servicio cargado SIEMPRE aparece" (pedido directo del dueño, ronda 3 del 2026-08-13): la
        // caja existe si hay AL MENOS UN vuelo vivo, sin filtrar por "cuánto contenido visible tiene la
        // fila" — ver ComposeFlightLegRow, que siempre imprime algo del lado de salida (hora real o fecha).
        var flights = (reserva.FlightSegments ?? new List<FlightSegment>())
            .Where(f => IsLive(f.Status, isFlight: true) && !OptionGroupRules.BelongsToAmbiguousGroup(f.OptionGroup, ambiguousGroups))
            .ToList();

        if (flights.Count == 0) return;

        column.Item().PaddingTop(8).Border(1).BorderColor(FlightBoxBorderColor).Padding(8).Column(box =>
        {
            foreach (var flight in flights)
            {
                // Fila IDA: Origin -> Destination, horarios Outbound*. SIEMPRE se dibuja (todo vuelo
                // cargado tiene, como mínimo, una fecha de ida — DepartureTime es obligatorio).
                box.Item().PaddingBottom(8).Element(e => ComposeFlightLegRow(
                    e, flight,
                    departureTime: flight.OutboundDepartureTime,
                    arrivalTime: flight.OutboundArrivalTime,
                    fallbackLegDate: flight.DepartureTime,
                    originCode: flight.Origin, originCity: flight.OriginCity,
                    destinationCode: flight.Destination, destinationCity: flight.DestinationCity));

                // Fila VUELTA: aeropuertos INVERTIDOS (Destination -> Origin), horarios Return*. Solo si
                // el vendedor cargó una fecha de vuelta (FlightSegment.ArrivalTime, ver el reparto firmado
                // en la entidad) — un vuelo de ida sola NUNCA imprime esta fila.
                if (QuoteBudgetPdfRules.HasReturnLeg(flight))
                {
                    box.Item().PaddingBottom(8).Element(e => ComposeFlightLegRow(
                        e, flight,
                        departureTime: flight.ReturnDepartureTime,
                        arrivalTime: flight.ReturnArrivalTime,
                        fallbackLegDate: flight.ArrivalTime!.Value,
                        originCode: flight.Destination, originCity: flight.DestinationCity,
                        destinationCode: flight.Origin, destinationCity: flight.OriginCity));
                }
            }
        });
    }

    /// <summary>
    /// Una fila de IDA o de VUELTA (maqueta BAYAHIBE, 2026-08-13): [ícono avión] [hora u fecha de salida
    /// + aeropuerto de origen] [chip "Directo"] [hora de llegada + aeropuerto de destino, con "+1" si
    /// cruza medianoche] [duración] [íconos de equipaje a la derecha]. El chip "Directo" y los íconos de
    /// equipaje son atributos DEL VUELO completo (no cambian entre ida y vuelta): salen de
    /// <c>flight.IsDirect</c>/<c>Includes*</c> sin importar qué horarios se le pasen a esta fila.
    /// </summary>
    private void ComposeFlightLegRow(
        IContainer container,
        FlightSegment flight,
        TimeOnly? departureTime,
        TimeOnly? arrivalTime,
        DateTime fallbackLegDate,
        string? originCode,
        string? originCity,
        string? destinationCode,
        string? destinationCity)
    {
        var departureAirportLabel = QuoteBudgetPdfRules.BuildFlightAirportLabel(originCode, originCity);
        var arrivalAirportLabel = QuoteBudgetPdfRules.BuildFlightAirportLabel(destinationCode, destinationCity);
        var durationLabel = QuoteBudgetPdfRules.BuildFlightLegDuration(departureTime, arrivalTime);

        // BuildFlightLegDepartureText NUNCA devuelve null (hora real o fecha de fallback) — el lado de
        // salida se dibuja siempre. El de llegada sí puede ser null (hora de llegada no cargada todavía).
        var departureTimeText = QuoteBudgetPdfRules.BuildFlightLegDepartureText(departureTime, fallbackLegDate);
        var arrivalTimeText = QuoteBudgetPdfRules.BuildFlightLegArrivalText(arrivalTime);
        var showsArrivalBlock = arrivalTimeText is not null || arrivalAirportLabel is not null;

        container.Row(row =>
        {
            row.ConstantItem(30).Height(30).Svg(FlightIconSvg);

            row.ConstantItem(10);

            row.AutoItem().Column(departure =>
            {
                departure.Item().Text(departureTimeText).Bold().FontSize(15);
                if (departureAirportLabel is not null)
                    departure.Item().Text(departureAirportLabel).FontSize(10.5f).FontColor(FlightMutedTextColor);
            });

            if (flight.IsDirect == true)
            {
                row.ConstantItem(12);
                row.AutoItem().AlignMiddle()
                    .Border(1).BorderColor(DirectChipBorderColor).Background(DirectChipBackgroundColor)
                    .PaddingHorizontal(8).PaddingVertical(4).Text("Directo").FontSize(11).FontColor(DirectChipTextColor);
            }

            if (showsArrivalBlock)
            {
                row.ConstantItem(20);
                row.AutoItem().Column(arrival =>
                {
                    if (arrivalTimeText is not null)
                    {
                        arrival.Item().Row(arrivalTimeRow =>
                        {
                            arrivalTimeRow.AutoItem().Text(arrivalTimeText).Bold().FontSize(15);

                            if (QuoteBudgetPdfRules.IsFlightLegNextDay(departureTime, arrivalTime))
                            {
                                arrivalTimeRow.AutoItem().PaddingLeft(2).AlignTop()
                                    .Text("+1").FontSize(8).FontColor(NextDayBadgeColor);
                            }
                        });
                    }

                    if (arrivalAirportLabel is not null)
                        arrival.Item().Text(arrivalAirportLabel).FontSize(10.5f).FontColor(FlightMutedTextColor);
                });
            }

            if (durationLabel is not null)
            {
                row.ConstantItem(14);
                row.AutoItem().AlignMiddle().Text(durationLabel).FontSize(11.5f).FontColor(FlightMutedTextColor);
            }

            // Empuja los íconos de equipaje al borde derecho de la fila (item relativo vacío = separador
            // elástico, patrón estándar de QuestPDF para "lo que sigue va pegado a la derecha").
            row.RelativeItem();

            // Fix ronda 2.1 (2026-08-13, rechazo del dueño): los TRES íconos van SIEMPRE, en este orden
            // (mochila, equipaje de mano, valija despachada) — el que no está incluido en la tarifa se ve
            // "fantasma" (tenue) en vez de desaparecer, para que el cliente vea de un vistazo qué entra y
            // qué no. Antes se dibujaba la MISMA valija genérica una vez por cada flag en true.
            row.ConstantItem(15).PaddingLeft(4).AlignMiddle()
                .Svg(BuildLuggageIconSvg(BackpackPathData, included: flight.IncludesBackpack == true));
            row.ConstantItem(13).PaddingLeft(3).AlignMiddle() // equipaje de mano: ~85% del tamaño de la valija despachada.
                .Svg(BuildLuggageIconSvg(LuggagePathData, included: flight.IncludesCarryOn == true));
            row.ConstantItem(15).PaddingLeft(3).AlignMiddle() // valija despachada: mismo dibujo que el de mano, tamaño completo.
                .Svg(BuildLuggageIconSvg(LuggagePathData, included: flight.IncludesCheckedBag == true));
        });
    }

    // ============================================================================================
    // HOTELES: título + estrellas + habitación + tarifa. Excluye los que pertenecen a un grupo
    // ambiguo (esos van en OPCIONES).
    // ============================================================================================

    private void ComposeHoteles(
        ColumnDescriptor column, Reserva reserva, HashSet<string> ambiguousGroups, bool porPersona, int cantidadPasajerosCargados)
    {
        var hotels = (reserva.HotelBookings ?? new List<HotelBooking>())
            .Where(h => IsLive(h.Status, isFlight: false) && !OptionGroupRules.BelongsToAmbiguousGroup(h.OptionGroup, ambiguousGroups))
            .ToList();

        foreach (var hotel in hotels)
        {
            column.Item().PaddingTop(10).Element(e => ComposeHotelBlock(e, hotel, porPersona, cantidadPasajerosCargados));
        }
    }

    private void ComposeHotelBlock(IContainer container, HotelBooking hotel, bool porPersona, int cantidadPasajerosCargados)
    {
        container.Column(column =>
        {
            // Maqueta v2 (2026-08-13): peso NORMAL a propósito — la negrita gruesa del intento anterior
            // fue el error que el dueño marcó ("nada que ver"); la maqueta es fina y elegante.
            column.Item().Row(titleRow =>
            {
                titleRow.AutoItem().Text(hotel.HotelName).FontSize(17).FontColor(DestinationTitleColor);

                if (hotel.StarRating is > 0)
                {
                    titleRow.ConstantItem(8);
                    titleRow.AutoItem().AlignMiddle().Element(e => ComposeStarRating(e, Math.Min(hotel.StarRating.Value, 5)));
                }
            });

            var roomLine = BuildRoomLine(hotel);
            if (!string.IsNullOrWhiteSpace(roomLine))
            {
                column.Item().Text(text =>
                {
                    text.Span("Habitación: ").Bold();
                    text.Span(roomLine);
                });
            }

            column.Item().PaddingTop(4).Element(e => ComposeTarifaLine(e, hotel.SalePrice, hotel.Currency, porPersona, cantidadPasajerosCargados));

            // Obra "PDF completo" (2026-08-13): línea de cuotas debajo de la tarifa ("6 CUOTAS 280
            // USD"), en negrita 13 como pide la maqueta. Ausente si el vendedor no cargó los dos datos
            // (ver QuoteBudgetPdfRules.BuildInstallmentsLine — regla espejo, decisión #8).
            var installmentsLine = QuoteBudgetPdfRules.BuildInstallmentsLine(hotel.InstallmentsCount, hotel.InstallmentAmount, hotel.Currency);
            if (installmentsLine is not null)
            {
                column.Item().PaddingTop(2).Text(installmentsLine).Bold().FontSize(13);
            }
        });
    }

    private string? BuildRoomLine(HotelBooking hotel)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(hotel.RoomType)) parts.Add(hotel.RoomType);
        if (!string.IsNullOrWhiteSpace(hotel.RoomCategory)) parts.Add(hotel.RoomCategory);
        return parts.Count == 0 ? null : string.Join(" – ", parts);
    }

    /// <summary>
    /// BUG CRÍTICO de la versión anterior (2026-08-13): las estrellas salían como cuadraditos rotos
    /// ("▯▯▯▯") porque la fuente por defecto de QuestPDF no tiene el glifo "★". Se dibujan como SVG (una
    /// por punto) en vez de texto — un SVG con un &lt;path&gt; explícito siempre tiene la forma pedida,
    /// sin depender de qué fuente esté instalada en el contenedor de producción.
    /// </summary>
    private void ComposeStarRating(IContainer container, int starCount)
    {
        container.Row(row =>
        {
            for (var i = 0; i < starCount; i++)
            {
                row.ConstantItem(13).PaddingRight(1).Svg(StarSvg);
            }
        });
    }

    /// <summary>"TARIFA POR PERSONA: **{monto} {moneda}**" o "TARIFA TOTAL:" según corresponda. El monto
    /// sale en negrita (única parte destacada de la línea, como pide la maqueta).</summary>
    private void ComposeTarifaLine(IContainer container, decimal salePrice, string? currency, bool porPersona, int cantidadPasajerosCargados)
    {
        var display = QuoteBudgetPdfRules.ResolveDisplayPrice(salePrice, cantidadPasajerosCargados, porPersona);
        var label = display.IsPerPerson ? "TARIFA POR PERSONA:" : "TARIFA TOTAL:";

        // Fix post-review (2026-08-12): antes esto caia a "ARS" cuando el servicio no tenia moneda
        // cargada — eso INVENTA un dato (la regla espejo de la obra prohibe afirmar una moneda que
        // nadie cargo). QuoteBudgetPdfRules.BuildAmountLabel ya decide "con moneda" vs "sin etiqueta".
        var amountLabel = QuoteBudgetPdfRules.BuildAmountLabel(display.Amount, currency);

        container.Text(text =>
        {
            text.Span(label + " ").FontSize(13);
            text.Span(amountLabel).Bold().FontSize(13);
        });
    }

    // ============================================================================================
    // OTROS SERVICIOS (traslados/paquetes/asistencias/genérico "Otro"): la maqueta firmada no detalla un
    // bloque propio para estos tipos (solo la línea resumen "TRASLADO:" de los datos generales), pero
    // omitir su tarifa del PDF escondería plata que el cliente debe pagar. Se imprimen con el mismo
    // criterio de tarifa que Hoteles, en una lista compacta — alcance a revisar con UX en la tanda de
    // frontend.
    //
    // "Otro" (ServicioReserva, obra "PDF completo", 2026-08-13) NO tiene OptionGroup/OptionLabel (es el
    // servicio genérico legacy, no uno de los 5 tipados con opciones A/B/C) — por eso no pasa por el
    // filtro OptionGroupRules.BelongsToAmbiguousGroup como los otros tres.
    // ============================================================================================

    private void ComposeOtrosServicios(
        ColumnDescriptor column, Reserva reserva, HashSet<string> ambiguousGroups, bool porPersona, int cantidadPasajerosCargados)
    {
        var rows = new List<(string DisplayName, decimal SalePrice, string? Currency)>();

        foreach (var transfer in (reserva.TransferBookings ?? new List<TransferBooking>())
            .Where(t => IsLive(t.Status, isFlight: false) && !OptionGroupRules.BelongsToAmbiguousGroup(t.OptionGroup, ambiguousGroups)))
        {
            var name = QuoteBudgetPdfRules.BuildTrasladoLine(transfer) ?? "Traslado";
            rows.Add((name, transfer.SalePrice, transfer.Currency));
        }

        foreach (var package in (reserva.PackageBookings ?? new List<PackageBooking>())
            .Where(p => IsLive(p.Status, isFlight: false) && !OptionGroupRules.BelongsToAmbiguousGroup(p.OptionGroup, ambiguousGroups)))
        {
            rows.Add((package.PackageName, package.SalePrice, package.Currency));
        }

        foreach (var assistance in (reserva.AssistanceBookings ?? new List<AssistanceBooking>())
            .Where(a => IsLive(a.Status, isFlight: false) && !OptionGroupRules.BelongsToAmbiguousGroup(a.OptionGroup, ambiguousGroups)))
        {
            var name = string.IsNullOrWhiteSpace(assistance.PlanType) ? "Asistencia" : assistance.PlanType!;
            rows.Add((name, assistance.SalePrice, assistance.Currency));
        }

        foreach (var otro in (reserva.Servicios ?? new List<ServicioReserva>())
            .Where(s => IsLive(s.Status, isFlight: false)))
        {
            rows.Add((QuoteBudgetPdfRules.BuildOtroServiceDisplayName(otro), otro.SalePrice, otro.Currency));
        }

        if (rows.Count == 0) return;

        column.Item().PaddingTop(10).Column(section =>
        {
            foreach (var row in rows)
            {
                section.Item().PaddingBottom(6).Column(rowColumn =>
                {
                    rowColumn.Item().Text(row.DisplayName).FontSize(14).Bold();
                    rowColumn.Item().Element(e => ComposeTarifaLine(e, row.SalePrice, row.Currency, porPersona, cantidadPasajerosCargados));
                });
            }
        });
    }

    // ============================================================================================
    // OPCIONES A/B/C: cada grupo ambiguo se lista con sus alternativas, una debajo de la otra, con la
    // tarifa de CADA una. Estos servicios NO aparecen en las secciones de arriba (Hoteles/Vuelos/...) —
    // ya se filtraron ahí — y tampoco entran al total general (regla de ReservaMoneyCalculator).
    // ============================================================================================

    private void ComposeOpciones(
        ColumnDescriptor column,
        IReadOnlyDictionary<string, IReadOnlyList<QuoteBudgetPdfRules.QuoteOptionCandidate>> optionGroups,
        bool porPersona,
        int cantidadPasajerosCargados)
    {
        if (optionGroups.Count == 0) return;

        foreach (var (groupName, candidates) in optionGroups)
        {
            column.Item().PaddingTop(10).Column(groupColumn =>
            {
                groupColumn.Item().Text($"OPCIONES – {groupName}").FontSize(15).Bold().FontColor(DestinationTitleColor);

                foreach (var candidate in candidates)
                {
                    groupColumn.Item().PaddingTop(4).PaddingLeft(10).Row(row =>
                    {
                        var label = string.IsNullOrWhiteSpace(candidate.OptionLabel)
                            ? candidate.DisplayName
                            : $"Opción {candidate.OptionLabel} — {candidate.DisplayName}";

                        row.RelativeItem().Text(label).FontSize(13);
                        // 200pt y no menos: "TARIFA POR PERSONA: 1.450 USD" entra entera. Con 140 la
                        // moneda se caía sola a la línea de abajo (defecto visto en el barrido 13/08).
                        row.ConstantItem(200).AlignRight().Element(e =>
                            ComposeTarifaLine(e, candidate.SalePrice, candidate.Currency, porPersona, cantidadPasajerosCargados));
                    });
                }
            });
        }
    }

    // ============================================================================================
    // FORMAS DE PAGO.
    // ============================================================================================

    private void ComposeFormasDePago(ColumnDescriptor column, Reserva reserva, AgencySettings agencySettings)
    {
        var text = QuoteBudgetPdfRules.ResolvePaymentTermsText(reserva.BudgetPaymentTermsText, agencySettings.BudgetPaymentTermsTemplate);
        if (string.IsNullOrWhiteSpace(text)) return; // sin texto propio NI plantilla -> seccion entera ausente.

        column.Item().PaddingTop(16).Column(section =>
        {
            section.Item().Text("FORMAS DE PAGO.").Bold().Underline().Italic().FontSize(13);
            section.Item().PaddingTop(4).Text(text).FontSize(12.5f);
        });
    }

    // ============================================================================================
    // PIE (REHECHO a la maqueta v2, 2026-08-13): 3 líneas, todas centradas.
    //   1) nombre de la agencia en ITÁLICA, color de banda (antes salía en negrita sin itálica — muere).
    //   2) "Legajo XXXX" si está cargado (antes NO se imprimía — muere).
    //   3) "dirección · Tel teléfono" (antes usaba "|" como separador y sin la palabra "Tel").
    // Cada línea se omite si su dato de origen está vacío — nunca un placeholder inventado.
    // ============================================================================================

    private void ComposeFooter(IContainer container, AgencySettings agencySettings)
    {
        var bandColor = string.IsNullOrWhiteSpace(agencySettings.PdfBandColorHex)
            ? DefaultBandColor
            : Color.FromHex(agencySettings.PdfBandColorHex);

        container.PaddingTop(6).AlignCenter().Column(column =>
        {
            var mainName = !string.IsNullOrWhiteSpace(agencySettings.AgencyName) ? agencySettings.AgencyName : agencySettings.LegalName;
            if (!string.IsNullOrWhiteSpace(mainName))
            {
                column.Item().AlignCenter().Text(mainName).FontSize(14).Italic().FontColor(bandColor);
            }

            if (!string.IsNullOrWhiteSpace(agencySettings.AgencyLicenseNumber))
            {
                column.Item().AlignCenter().Text($"Legajo {agencySettings.AgencyLicenseNumber}").FontSize(10.5f).FontColor(FooterGrayColor);
            }

            var addressAndPhoneParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(agencySettings.Address))
                addressAndPhoneParts.Add(agencySettings.Address!.Trim());
            if (!string.IsNullOrWhiteSpace(agencySettings.Phone))
                addressAndPhoneParts.Add($"Tel {agencySettings.Phone!.Trim()}");

            if (addressAndPhoneParts.Count > 0)
            {
                column.Item().AlignCenter().Text(string.Join(" · ", addressAndPhoneParts)).FontSize(10.5f).FontColor(FooterGrayColor);
            }
        });
    }

    // ============================================================================================
    // PÁGINA 2 "INFORMACION IMPORTANTE": solo los bloques de condiciones de los tipos de servicio
    // presentes en el presupuesto + el bloque Generales. Página entera ausente si no hay ninguno
    // (decidido ANTES de llamar acá, ver GenerateQuotePdf).
    // ============================================================================================

    private void ComposeConditionsPage(
        PageDescriptor page, AgencySettings agencySettings, Color bandColor, byte[]? logoBytes, IReadOnlyList<BudgetConditionBlock> conditions)
    {
        page.Size(PageSizes.A4);
        page.PageColor(Colors.White);
        // La maqueta pide "estilo Calibri", pero NO fijamos ese nombre de fuente a proposito: el
        // contenedor de produccion (ver src/TravelApi/Dockerfile) solo instala fonts-dejavu-core, no
        // fuentes de Microsoft — pedir "Calibri" ahi rompería la generación del PDF (o la reemplazaría
        // en silencio por un fallback feo). Se deja SIN FontFamily, igual que InvoicePdfService: QuestPDF
        // usa su fuente por defecto (ya validada en producción por las facturas).
        page.DefaultTextStyle(x => x.FontSize(13));

        page.Header().Column(headerColumn =>
        {
            headerColumn.Item().Element(e => ComposeBand(e, bandColor, logoBytes, heightPt: 54));

            // REHECHO a la maqueta v2 (2026-08-13): antes salía gigante (20), centrado y en negrita
            // simple — el dueño lo marcó como "muere". El título de página 2 lleva el MISMO estilo de
            // sección que "FORMAS DE PAGO." en la página 1 (negrita + subrayado + itálica, 13,
            // alineado a la izquierda), con el mismo padding horizontal que el resto del contenido.
            headerColumn.Item().PaddingTop(14).PaddingHorizontal(ContentHorizontalPadding)
                .Text("INFORMACIÓN IMPORTANTE").Bold().Underline().Italic().FontSize(13);
        });

        page.Content().PaddingHorizontal(ContentHorizontalPadding).PaddingTop(14).Column(content =>
        {
            foreach (var block in conditions)
            {
                content.Item().PaddingBottom(10).Column(section =>
                {
                    // Títulos de bloque en ITÁLICA (no negrita): la maqueta reserva la negrita+subrayado
                    // para el título de sección de arriba, los bloques van más discretos.
                    // "Aereos" es la CLAVE de la API (sin tilde a propósito, no se puede cambiar);
                    // al cliente se le muestra la palabra bien escrita.
                    var tituloBloque = block.Kind == BudgetConditionBlockKind.Flights
                        ? "Aéreos"
                        : BudgetConditionBlockKindText.ToDisplayText(block.Kind);
                    section.Item().Text(tituloBloque).Italic().FontSize(12.5f);
                    section.Item().PaddingTop(2).Text(block.Text).FontSize(12.5f);
                });
            }
        });

        page.Footer().Element(e => ComposeFooter(e, agencySettings));
    }

    // --- Helpers de "vivo" (no cancelado) y de primer-servicio-cargado, para las líneas de datos ---

    private static bool IsLive(string status, bool isFlight)
        => WorkflowStatusHelper.CountsForQuotedTotal(isFlight ? WorkflowStatusHelper.MapFlightStatus(status) : WorkflowStatusHelper.MapGenericStatus(status));

    private static HotelBooking? FirstLiveHotel(Reserva reserva)
        => (reserva.HotelBookings ?? new List<HotelBooking>()).FirstOrDefault(h => IsLive(h.Status, isFlight: false));

    private static FlightSegment? FirstLiveFlight(Reserva reserva)
        => (reserva.FlightSegments ?? new List<FlightSegment>()).FirstOrDefault(f => IsLive(f.Status, isFlight: true) && HasAnyBaggageInfo(f));

    private static bool HasAnyBaggageInfo(FlightSegment flight)
        => flight.IncludesBackpack.HasValue || flight.IncludesCarryOn.HasValue || flight.IncludesCheckedBag.HasValue
           || !string.IsNullOrWhiteSpace(flight.Baggage);

    private static TransferBooking? FirstLiveTransfer(Reserva reserva)
        => (reserva.TransferBookings ?? new List<TransferBooking>()).FirstOrDefault(t => IsLive(t.Status, isFlight: false));
}
