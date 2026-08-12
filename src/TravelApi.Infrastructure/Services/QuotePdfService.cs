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

    // NOTA (alcance de esta tanda): la maqueta pide un superíndice rojo "+1" cuando el vuelo llega al
    // día siguiente. Hoy OutboundDepartureTime/ReturnDepartureTime son solo HORA (TimeOnly, sin fecha) —
    // no hay forma de calcular "cruza medianoche" sin inventar una fecha. Se deja sin implementar a
    // propósito (regla madre: nunca se inventa un dato) hasta que exista un campo de fecha real para
    // ese cálculo.

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
            headerColumn.Item().PaddingTop(14).Element(e => ComposeDestinoYDatos(e, reserva));
        });

        page.Content().PaddingHorizontal(30).PaddingTop(10).Column(content =>
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
    // VUELOS: recuadro con una fila por tramo vivo (no cancelado, no perteneciente a un grupo ambiguo —
    // esos se listan en OPCIONES). Version "completa" (horario estructurado) o "simple" (una línea).
    // ============================================================================================

    private void ComposeVuelos(ColumnDescriptor column, Reserva reserva, HashSet<string> ambiguousGroups)
    {
        var flights = (reserva.FlightSegments ?? new List<FlightSegment>())
            .Where(f => IsLive(f.Status, isFlight: true) && !OptionGroupRules.BelongsToAmbiguousGroup(f.OptionGroup, ambiguousGroups))
            .ToList();

        if (flights.Count == 0) return;

        column.Item().PaddingTop(8).Border(1).BorderColor(FlightBoxBorderColor).Padding(8).Column(box =>
        {
            foreach (var flight in flights)
            {
                box.Item().PaddingBottom(4).Element(e => ComposeFlightRow(e, flight));
            }
        });
    }

    private void ComposeFlightRow(IContainer container, FlightSegment flight)
    {
        // Hoy el front no carga los horarios estructurados (OutboundDepartureTime/ReturnDepartureTime):
        // la version "completa" queda lista para cuando exista esa pantalla, pero el camino normal hoy
        // es la version simple de una linea.
        if (!QuoteBudgetPdfRules.HasStructuredSchedule(flight))
        {
            container.Text(QuoteBudgetPdfRules.BuildFlightSummaryLine(flight)).FontSize(13);
            return;
        }

        container.Row(row =>
        {
            row.RelativeItem().Column(times =>
            {
                if (flight.OutboundDepartureTime.HasValue)
                    times.Item().Text($"Sale {flight.OutboundDepartureTime:HH:mm}hs").Bold().FontSize(16);
                if (flight.ReturnDepartureTime.HasValue)
                    times.Item().Text($"Vuelve {flight.ReturnDepartureTime:HH:mm}hs").Bold().FontSize(16);

                var originDestino = BuildRouteLabel(flight);
                if (!string.IsNullOrWhiteSpace(originDestino))
                    times.Item().Text(originDestino).FontSize(10).FontColor(Colors.Grey.Darken1);
            });

            if (flight.IsDirect == true)
            {
                row.ConstantItem(70).AlignMiddle().AlignCenter()
                    .Border(1).BorderColor(DirectChipBorderColor).Background(DirectChipBackgroundColor)
                    .Padding(4).Text("Directo").FontSize(9).FontColor(DirectChipTextColor);
            }
        });
    }

    private string? BuildRouteLabel(FlightSegment flight)
    {
        var origin = flight.OriginCity ?? flight.Origin;
        var destination = flight.DestinationCity ?? flight.Destination;
        if (string.IsNullOrWhiteSpace(origin) && string.IsNullOrWhiteSpace(destination)) return null;
        return $"{origin} → {destination}";
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
            var titleWithStars = hotel.StarRating is > 0
                ? $"{hotel.HotelName}  {new string('★', Math.Min(hotel.StarRating.Value, 5))}"
                : hotel.HotelName;

            column.Item().Text(titleWithStars).FontSize(17).FontColor(DestinationTitleColor).Bold();

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
        });
    }

    private string? BuildRoomLine(HotelBooking hotel)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(hotel.RoomType)) parts.Add(hotel.RoomType);
        if (!string.IsNullOrWhiteSpace(hotel.RoomCategory)) parts.Add(hotel.RoomCategory);
        return parts.Count == 0 ? null : string.Join(" – ", parts);
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
    // OTROS SERVICIOS (traslados/paquetes/asistencias): la maqueta firmada no detalla un bloque propio
    // para estos tres tipos (solo la línea resumen "TRASLADO:" de los datos generales), pero omitir su
    // tarifa del PDF escondería plata que el cliente debe pagar. Se imprimen con el mismo criterio de
    // tarifa que Hoteles, en una lista compacta — alcance a revisar con UX en la tanda de frontend.
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
                        row.ConstantItem(140).AlignRight().Element(e =>
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
    // PIE: razón social (color de banda) + legajo + dirección/teléfono. Cada parte se omite si su
    // dato de origen está vacío.
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
                column.Item().AlignCenter().Text(mainName).FontSize(11).Bold().FontColor(bandColor);
            }

            var footerLineParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(agencySettings.AgencyLicenseNumber))
                footerLineParts.Add($"Legajo {agencySettings.AgencyLicenseNumber}");
            if (!string.IsNullOrWhiteSpace(agencySettings.Address))
                footerLineParts.Add(agencySettings.Address);
            if (!string.IsNullOrWhiteSpace(agencySettings.Phone))
                footerLineParts.Add(agencySettings.Phone);

            if (footerLineParts.Count > 0)
            {
                column.Item().AlignCenter().Text(string.Join(" | ", footerLineParts)).FontSize(10.5f).FontColor(Colors.Grey.Darken1);
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
            headerColumn.Item().PaddingTop(14).AlignCenter().Text("INFORMACIÓN IMPORTANTE")
                .FontSize(20).Bold().FontColor(DestinationTitleColor);
        });

        page.Content().PaddingHorizontal(30).PaddingTop(14).Column(content =>
        {
            foreach (var block in conditions)
            {
                content.Item().PaddingBottom(10).Column(section =>
                {
                    section.Item().Text(BudgetConditionBlockKindText.ToDisplayText(block.Kind)).Bold().FontSize(13);
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
