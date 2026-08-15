using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Renderer del PDF de presupuesto (maqueta "minimalista elegante", spec firmada por el dueño,
/// 2026-08-14, <c>docs/ux/2026-08-14-spec-pdf-minimalista-elegante.md</c>). Espejo de
/// <see cref="InvoicePdfService"/>: recibe TODO ya cargado (reserva, config de agencia, condiciones,
/// logo, color de acento) y solo arma bytes con QuestPDF — no toca la base de datos ni hace I/O.
///
/// <para>La lógica de "qué se muestra / qué se omite / con qué número" vive en
/// <see cref="QuoteBudgetPdfRules"/> (funciones puras, testeables sin generar un PDF). Esta clase es un
/// renderer fino: pinta lo que esa clase ya decidió.</para>
///
/// <para><b>Firma visual de esta maqueta</b>: fondo blanco sin banda de color; un "riel de itinerario"
/// (línea vertical con nodos) marca cada sección; el color de ACENTO (uno solo por documento, elegido
/// por destino — ver <see cref="IDestinationPaletteService"/> — o el de respaldo de la agencia) aparece
/// SOLO en puntos puntuales (eyebrow del hero, nodos del riel, títulos de sección, avioncito, estrellas,
/// filo de la tarjeta de total); todo lo demás es tinta/apagado/filetes neutros.</para>
/// </summary>
public class QuotePdfService : IQuotePdfService
{
    // ============================================================================================
    // Paleta neutra de la maqueta (spec §0, 2026-08-14): fija, no configurable por la agencia. El ÚNICO
    // color que varía por presupuesto es el ACENTO (ver ResolveAccentColor), que llega ya resuelto desde
    // afuera o cae al respaldo de AgencySettings.PdfBandColorHex.
    // ============================================================================================
    private static readonly Color InkColor = Color.FromHex("#1a1a1a");
    private static readonly Color MutedColor = Color.FromHex("#6b7280");
    private static readonly Color RuleColor = Color.FromHex("#e5e7eb");
    private static readonly Color SoftInkColor = Color.FromHex("#3f434a"); // cuerpo de "Formas de pago"/"Condiciones" en página 2.
    private const string DefaultAccentHex = "#0e3a4f"; // respaldo final si ni la IA ni la agencia definieron un color.

    // Tracking (letter-spacing) de la maqueta: QuestPDF lo expresa como un FACTOR proporcional al tamaño
    // de fuente (0 = normal, positivo = mas separado), no en puntos absolutos como el diseño original en
    // px. Estos cuatro valores son una interpretacion razonable de "tracking ancho/1.5/2/leve" de la spec
    // -- no hay un numero exacto especificado en puntos, así que quedan como DECISIÓN del implementador,
    // ajustable a ojo en una proxima ronda visual si Gaston pide mas/menos separación.
    private const float WideTracking = 0.12f; // eyebrow, "PRESUPUESTO", wordmark, paginación.
    private const float SectionTitleTracking = 0.15f; // "tracking 2" de los títulos de sección.
    private const float DataLabelTracking = 0.08f; // "tracking 1.5" de las etiquetas SALIDA/EQUIPAJE/...
    private const float SlightTracking = 0.04f; // "tracking leve" de aeropuertos/cuotas/bloques de condiciones.

    private const float ContentHorizontalPadding = 42f; // 56px * 0.75 (conversión CSS->PDF de la spec).

    // ============================================================================================
    // Tipografía Marcellus (spec §0): SOLO para el wordmark sin logo, el destino del hero y el monto de
    // la tarjeta de total. Se registra UNA vez para todo el proceso en el constructor ESTÁTICO — el
    // runtime de .NET garantiza que un constructor estático corre una única vez y de forma segura entre
    // threads (no hace falta lock manual acá).
    //
    // TRAMPA importante: pedirle a QuestPDF una FontFamily que NO se registró de verdad hace que
    // GeneratePdf() TIRE una excepción (no hay fallback silencioso a la fuente default como uno
    // esperaría). Por eso TODO uso de Marcellus en este archivo está atrás de la bandera
    // MarcellusFontIsRegistered: si el registro falló (recurso corrupto, algo raro del entorno), el PDF
    // sale igual, con Lato (la fuente default), como pide la spec ("jamás se rompe la emisión").
    // ============================================================================================
    private const string MarcellusFontFamily = "Marcellus";
    private static readonly bool MarcellusFontIsRegistered;

    static QuotePdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        MarcellusFontIsRegistered = TryRegisterMarcellusFont();
    }

    private static bool TryRegisterMarcellusFont()
    {
        try
        {
            using var fontStream = typeof(QuotePdfService).Assembly
                .GetManifestResourceStream("TravelApi.Infrastructure.Assets.Fonts.Marcellus-Regular.ttf");
            if (fontStream is null) return false;

            QuestPDF.Drawing.FontManager.RegisterFont(fontStream);
            return true;
        }
        catch
        {
            // Nunca romper la emisión de PDFs por una fuente decorativa que no pudo cargar.
            return false;
        }
    }

    /// <summary>Aplica Marcellus a un span de texto SOLO si el registro fue exitoso (ver nota de arriba).</summary>
    private static TextSpanDescriptor ApplyMarcellusIfAvailable(TextSpanDescriptor span)
        => MarcellusFontIsRegistered ? span.FontFamily(MarcellusFontFamily) : span;

    // ============================================================================================
    // Íconos dibujados como SVG inline (no como texto con glifos "★"/"✈"): la fuente por defecto del
    // contenedor de producción (fonts-dejavu-core) no trae esos glifos y salían como cuadraditos rotos
    // ("tofu") en rondas anteriores de esta obra. Un SVG con un <path> explícito SIEMPRE dibuja la forma
    // pedida, sin depender de qué fuente esté instalada. Se arman como string CADA VEZ (no constantes
    // fijas) porque necesitan el color de ACENTO de ESTE presupuesto, que cambia por destino.
    // ============================================================================================

    /// <summary>Avioncito SOLO (sin círculo de fondo, spec §1) — path estándar de 24x24, coloreado en ACENTO.</summary>
    private static string BuildFlightIconSvg(string accentHex) => $"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
          <path fill="{accentHex}" d="M21 16v-2l-8-5V3.5c0-.83-.67-1.5-1.5-1.5S10 2.67 10 3.5V9l-8 5v2l8-2.5V19l-2.5 1.5V22l3.5-1 3.5 1v-1.5L13 19v-5.5l8 2.5z"/>
        </svg>
        """;

    /// <summary>Estrella de 5 puntas rellena, en color de ACENTO (una por punto en <c>ComposeStarRating</c>).</summary>
    private static string BuildStarSvg(string accentHex) => $"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
          <path fill="{accentHex}" d="M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z"/>
        </svg>
        """;

    /// <summary>
    /// Nodo del riel de itinerario (spec §1, firma visual de esta maqueta): círculo blanco de 7pt con
    /// borde 1.5pt en ACENTO, centrado sobre la línea del riel a la altura del título de cada sección.
    /// </summary>
    private static string BuildRailNodeSvg(string accentHex) => $"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 14 14">
          <circle cx="7" cy="7" r="5" fill="#ffffff" stroke="{accentHex}" stroke-width="3"/>
        </svg>
        """;

    // Íconos de equipaje (sin cambios de dibujo respecto de la ronda anterior de esta obra, 2026-08-14:
    // solo cambia el TAMAÑO en que se dibujan, ver ComposeFlightLegRow). Mochila / equipaje de mano /
    // valija despachada, tres dibujos distintos, color fijo #5b6067 (la spec NO los pone en acento).

    private const string BackpackPathData =
        "M20 8v12c0 1.1-.9 2-2 2H6c-1.1 0-2-.9-2-2V8c0-1.86 1.28-3.41 3-3.86V2h3v2h4V2h3v2.14c1.72.45 3 2 3 3.86zM6 12v2h10v2h2v-4H6z";

    private const string HandBagPathData =
        "M20 6h-4V4c0-1.11-.89-2-2-2h-4c-1.11 0-2 .89-2 2v2H4c-1.11 0-2 .89-2 2v11c0 1.11.89 2 2 2h16c1.11 0 2-.89 2-2V8c0-1.11-.89-2-2-2zm-6 0h-4V4h4v2z";

    private const string LuggagePathData =
        "M17 6h-2V3c0-.55-.45-1-1-1h-4c-.55 0-1 .45-1 1v3H7c-1.1 0-2 .9-2 2v11c0 1.1.9 2 2 2 0 .55.45 1 1 1s1-.45 1-1h6c0 .55.45 1 1 1s1-.45 1-1c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2zM9.5 18H8V9h1.5v9zm3.25 0h-1.5V9h1.5v9zm.75-12h-3V3.5h3V6zm3 12H15V9h1.5v9z";

    /// <summary>
    /// Arma el SVG de un ícono de equipaje con el estado incluido/no-incluido. "Fantasma" (no incluido):
    /// mismo dibujo, bien tenue -- nunca se oculta el ícono entero.
    /// </summary>
    private static string BuildLuggageIconSvg(string pathData, bool included)
    {
        var fillOpacity = included ? "1" : "0.22";
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
              <path fill="#5b6067" fill-opacity="{fillOpacity}" d="{pathData}"/>
            </svg>
            """;
    }

    // ============================================================================================
    // Riel de itinerario: geometría fija.
    // ============================================================================================
    private const float RailColumnWidth = 20f;
    private const float RailLineWidth = 0.75f;
    private const float RailNodeDiameter = 7f;

    public byte[] GenerateQuotePdf(
        Reserva reserva,
        AgencySettings agencySettings,
        IReadOnlyList<BudgetConditionBlock> conditions,
        byte[]? logoBytes,
        bool porPersona,
        int cantidadPasajerosCargados,
        string? accentColorHex = null)
    {
        ArgumentNullException.ThrowIfNull(reserva);
        ArgumentNullException.ThrowIfNull(agencySettings);
        conditions ??= Array.Empty<BudgetConditionBlock>();

        var (accentHex, accentColor) = ResolveAccentColor(accentColorHex, agencySettings.PdfBandColorHex);

        // Fuente única de "qué grupos siguen ambiguos" (la MISMA que decide que no suman al total
        // general en ReservaMoneyCalculator) y de "qué candidatos tiene cada grupo" (para imprimirlos).
        var ambiguousGroups = ReservaMoneyCalculator.FindAmbiguousOptionGroups(reserva);
        var optionGroups = QuoteBudgetPdfRules.BuildAmbiguousOptionGroups(reserva);

        // Página 2 cambió de contenido en esta maqueta (spec §2): ahora lleva "FORMAS DE PAGO" ADEMÁS de
        // "CONDICIONES" (antes formas de pago vivía en la página 1). La página 2 entera solo existe si
        // hay ALGO que mostrar en cualquiera de las dos secciones — se calcula ANTES de armar el
        // documento para decidir si se agrega el segundo Page().
        var paymentTermsText = QuoteBudgetPdfRules.ResolvePaymentTermsText(reserva.BudgetPaymentTermsText, agencySettings.BudgetPaymentTermsTemplate);
        var relevantConditions = QuoteBudgetPdfRules.SelectRelevantConditions(reserva, conditions);
        var hasPage2Content = paymentTermsText is not null || relevantConditions.Count > 0;

        var document = Document.Create(builder =>
        {
            builder.Page(page => ComposeMainPage(
                page, reserva, agencySettings, accentHex, accentColor, logoBytes,
                ambiguousGroups, optionGroups, porPersona, cantidadPasajerosCargados));

            if (hasPage2Content)
            {
                builder.Page(page => ComposeConditionsPage(
                    page, reserva, agencySettings, accentColor, logoBytes, paymentTermsText, relevantConditions));
            }
        });

        return document.GeneratePdf();
    }

    /// <summary>
    /// El color de acento del documento (spec §5): gana el que resolvió el caller (paleta por destino,
    /// vía <see cref="IDestinationPaletteService"/>) si es un hex válido; si no, el respaldo de la
    /// agencia; si tampoco hay, el default fijo de la maqueta. Nunca lanza: un hex corrupto que haya
    /// llegado desde la IA o desde una configuración vieja NUNCA puede romper la emisión de un PDF.
    /// </summary>
    private static (string Hex, Color Color) ResolveAccentColor(string? accentColorHex, string? agencyBandColorHex)
    {
        if (TryParseColor(accentColorHex, out var fromCaller)) return fromCaller;
        if (TryParseColor(agencyBandColorHex, out var fromAgency)) return fromAgency;

        return (DefaultAccentHex, Color.FromHex(DefaultAccentHex));
    }

    private static bool TryParseColor(string? hex, out (string Hex, Color Color) result)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                result = (hex.Trim(), Color.FromHex(hex.Trim()));
                return true;
            }
            catch
            {
                // hex invalido (dato viejo corrupto, o algo raro devuelto por la IA): se sigue al
                // siguiente nivel de respaldo, nunca se rompe la emisión por un color mal formado.
            }
        }

        result = default;
        return false;
    }

    // ============================================================================================
    // PÁGINA 1: identidad de la agencia + hero + riel de itinerario (AÉREOS/HOTEL/TRASLADOS/OPCIONES/
    // OTROS) + tarjeta de total + pie.
    // ============================================================================================

    private void ComposeMainPage(
        PageDescriptor page,
        Reserva reserva,
        AgencySettings agencySettings,
        string accentHex,
        Color accentColor,
        byte[]? logoBytes,
        HashSet<string> ambiguousGroups,
        IReadOnlyDictionary<string, IReadOnlyList<QuoteBudgetPdfRules.QuoteOptionCandidate>> optionGroups,
        bool porPersona,
        int cantidadPasajerosCargados)
    {
        page.Size(PageSizes.A4);
        page.PageColor(Colors.White); // fondo blanco sin banda -- firma visual de esta maqueta (spec §0).
        page.DefaultTextStyle(x => x.FontSize(9).FontColor(InkColor));

        page.Header().PaddingHorizontal(ContentHorizontalPadding).PaddingTop(24).Column(headerColumn =>
        {
            headerColumn.Item().Row(row =>
            {
                row.RelativeItem().Element(e => ComposeAgencyIdentity(e, agencySettings, logoBytes));
                row.ConstantItem(170).Element(e => ComposeBudgetIdentityBlock(e, reserva));
            });

            headerColumn.Item().PaddingTop(33).Element(e => ComposeHero(e, reserva, accentColor));
        });

        // Filtrados UNA sola vez acá: los usan tanto el render de cada sección como los flags
        // hasFlights/hasHotel/... que arma la nota de la tarjeta de total (nunca se afirma "incluye
        // vuelos" si la sección de vuelos, de hecho, no se dibujó).
        var flights = FilterLiveFlights(reserva, ambiguousGroups);
        var hotels = FilterLiveHotels(reserva, ambiguousGroups);
        var transfers = FilterLiveTransfers(reserva, ambiguousGroups);
        var otherRows = BuildOtrosRows(reserva, ambiguousGroups);

        page.Content().PaddingHorizontal(ContentHorizontalPadding).PaddingTop(6).Column(content =>
        {
            if (flights.Count > 0)
            {
                ComposeSectionWithRail(content, "AÉREOS", accentColor, accentHex,
                    section => ComposeVuelosContent(section, flights, accentHex));
            }

            if (hotels.Count > 0)
            {
                ComposeSectionWithRail(content, "HOTEL", accentColor, accentHex,
                    section => ComposeHotelesContent(section, hotels, accentHex, porPersona, cantidadPasajerosCargados));
            }

            if (transfers.Count > 0)
            {
                ComposeSectionWithRail(content, "TRASLADOS", accentColor, accentHex,
                    section => ComposeSimpleServiceRows(section, transfers, porPersona, cantidadPasajerosCargados));
            }

            if (optionGroups.Count > 0)
            {
                ComposeSectionWithRail(content, "OPCIONES", accentColor, accentHex,
                    section => ComposeOpcionesContent(section, optionGroups, porPersona, cantidadPasajerosCargados));
            }

            if (otherRows.Count > 0)
            {
                ComposeSectionWithRail(content, "OTROS", accentColor, accentHex,
                    section => ComposeSimpleServiceRows(section, otherRows, porPersona, cantidadPasajerosCargados));
            }

            ComposeTotalCard(
                content, reserva, accentColor,
                hasFlights: flights.Count > 0, hasHotel: hotels.Count > 0,
                hasTransfers: transfers.Count > 0, hasOthers: otherRows.Count > 0,
                porPersona, cantidadPasajerosCargados);
        });

        page.Footer().Element(e => ComposeFooter(e, agencySettings));
    }

    /// <summary>
    /// Logo de <c>AgencySettings</c> (alto máx 26pt) si la agencia cargó uno; si no, el wordmark en
    /// Marcellus (primera palabra del nombre) + el resto del nombre debajo, chico y apagado (spec §1/§4).
    /// Sin logo NI nombre cargado, el header queda sin identidad — nunca un placeholder inventado.
    /// </summary>
    private void ComposeAgencyIdentity(IContainer container, AgencySettings agencySettings, byte[]? logoBytes)
    {
        if (logoBytes is { Length: > 0 })
        {
            try
            {
                container.Height(26).AlignLeft().Image(logoBytes).FitHeight();
                return;
            }
            catch
            {
                // Logo corrupto/formato no soportado: se sigue con el wordmark de texto (ver abajo), el
                // PDF no se rompe por un archivo que QuestPDF no puede decodificar.
            }
        }

        var mainName = !string.IsNullOrWhiteSpace(agencySettings.AgencyName) ? agencySettings.AgencyName : agencySettings.LegalName;
        if (string.IsNullOrWhiteSpace(mainName)) return;

        var (wordmark, restOfName) = SplitAgencyWordmark(mainName.Trim());

        container.Column(identity =>
        {
            identity.Item().Text(text =>
            {
                // Mayúsculas a propósito (ronda de revisión visual, 2026-08-14): la maqueta muestra
                // "MAGNA" en caps, no "Magna" tal cual está cargado el nombre en Ajustes.
                var span = text.Span(wordmark.ToUpperInvariant()).FontSize(13).FontColor(InkColor).LetterSpacing(WideTracking);
                ApplyMarcellusIfAvailable(span);
            });

            if (restOfName is not null)
            {
                identity.Item().PaddingTop(2)
                    .Text(restOfName.ToUpperInvariant()).FontSize(6.5f).FontColor(MutedColor).LetterSpacing(WideTracking);
            }
        });
    }

    /// <summary>
    /// Parte el nombre de la agencia en "primera palabra" (el wordmark grande, Marcellus) y "el resto"
    /// (chico, debajo). Decisión del implementador: la spec pide el efecto visual pero no dice cómo
    /// partir el nombre — la primera palabra es la interpretación más simple y estable.
    /// </summary>
    private static (string Wordmark, string? RestOfName) SplitAgencyWordmark(string agencyName)
    {
        var spaceIndex = agencyName.IndexOf(' ');
        if (spaceIndex <= 0) return (agencyName, null);

        var rest = agencyName[(spaceIndex + 1)..].Trim();
        return (agencyName[..spaceIndex], rest.Length == 0 ? null : rest);
    }

    /// <summary>Bloque "PRESUPUESTO / número / fecha de emisión", alineado a la derecha (spec §1).</summary>
    private void ComposeBudgetIdentityBlock(IContainer container, Reserva reserva)
    {
        container.Column(column =>
        {
            column.Item().AlignRight().Text("PRESUPUESTO").FontSize(7).FontColor(MutedColor).LetterSpacing(WideTracking);

            if (!string.IsNullOrWhiteSpace(reserva.NumeroReserva))
            {
                column.Item().AlignRight().PaddingTop(2)
                    .Text(reserva.NumeroReserva).FontSize(10.5f).Bold().FontColor(InkColor);
            }

            // "Fecha de emisión" = HOY, el momento real en que se genera este PDF -- no es un dato
            // guardado, es un hecho real del propio acto de emitir (mismo criterio que la fecha de
            // emisión de una factura).
            column.Item().AlignRight().PaddingTop(2)
                .Text(DateTime.UtcNow.Date.ToString("dd/MM/yyyy")).FontSize(7).FontColor(MutedColor);
        });
    }

    /// <summary>
    /// El hero (spec §1): eyebrow en acento, destino en Marcellus (tamaño según el largo), subrayado,
    /// línea meta y la grilla de 2 columnas con SALIDA/EQUIPAJE/TRASLADO/RÉGIMEN. Cierra con un filete
    /// completo que separa el hero del riel de itinerario.
    /// </summary>
    private void ComposeHero(IContainer container, Reserva reserva, Color accentColor)
    {
        var destinationTitle = QuoteBudgetPdfRules.ResolveDestinationTitle(reserva.HotelBookings, reserva.PackageBookings);
        var firstLiveHotel = FirstLiveHotel(reserva);
        var totalPassengers = reserva.AdultCount + reserva.ChildCount + reserva.InfantCount;

        container.Column(hero =>
        {
            hero.Item().Text(QuoteBudgetPdfRules.BuildHeroEyebrowText(firstLiveHotel?.Nights))
                .FontSize(7).Bold().FontColor(accentColor).LetterSpacing(WideTracking);

            if (!string.IsNullOrWhiteSpace(destinationTitle))
            {
                var destinationFontSize = QuoteBudgetPdfRules.ResolveHeroDestinationFontSize(destinationTitle);

                hero.Item().PaddingTop(6).Text(text =>
                {
                    var span = text.Span(destinationTitle.ToUpperInvariant()).FontSize(destinationFontSize).FontColor(InkColor);
                    ApplyMarcellusIfAvailable(span);
                });

                hero.Item().PaddingTop(6).Width(48).Height(2.25f).Background(accentColor);
            }

            var metaLine = QuoteBudgetPdfRules.BuildHeroMetaLine(reserva.StartDate, reserva.EndDate, totalPassengers, firstLiveHotel?.Country);
            if (metaLine is not null)
            {
                hero.Item().PaddingTop(8).Text(metaLine).FontSize(9.5f).FontColor(MutedColor);
            }

            hero.Item().PaddingTop(12).Element(e => ComposeHeroDataGrid(e, reserva, firstLiveHotel));

            hero.Item().PaddingTop(14).BorderBottom(RailLineWidth).BorderColor(RuleColor);
        });
    }

    /// <summary>
    /// Las 4 líneas de datos (SALIDA/EQUIPAJE/TRASLADO/RÉGIMEN) en grilla de 2 columnas: se arma primero
    /// la lista de las que SÍ tienen dato (una línea sin dato de origen no se dibuja) y recién ahí se
    /// reparte de a pares por fila — así una línea faltante no deja un hueco en la grilla.
    /// </summary>
    private void ComposeHeroDataGrid(IContainer container, Reserva reserva, HotelBooking? firstLiveHotel)
    {
        var firstLiveFlight = FirstLiveFlight(reserva);
        var firstLiveTransfer = FirstLiveTransfer(reserva);

        var lines = new List<(string Label, string Value)>();
        void AddIfPresent(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) lines.Add((label, value!));
        }

        AddIfPresent("SALIDA", QuoteBudgetPdfRules.BuildSalidaLine(reserva.StartDate, reserva.EndDate, firstLiveHotel?.Nights));
        AddIfPresent("EQUIPAJE", QuoteBudgetPdfRules.BuildEquipajeLine(firstLiveFlight));
        AddIfPresent("TRASLADO", QuoteBudgetPdfRules.BuildTrasladoLine(firstLiveTransfer));
        AddIfPresent("RÉGIMEN", firstLiveHotel?.MealPlan);

        if (lines.Count == 0) return;

        container.Column(grid =>
        {
            for (var i = 0; i < lines.Count; i += 2)
            {
                grid.Item().PaddingBottom(6).Row(row =>
                {
                    ComposeHeroDataCell(row, lines[i].Label, lines[i].Value);

                    if (i + 1 < lines.Count)
                    {
                        ComposeHeroDataCell(row, lines[i + 1].Label, lines[i + 1].Value);
                    }
                    else
                    {
                        row.RelativeItem(); // fila impar: la segunda columna queda vacía, no rota el ancho.
                    }
                });
            }
        });
    }

    private void ComposeHeroDataCell(RowDescriptor row, string label, string value)
    {
        row.RelativeItem().Row(cell =>
        {
            cell.ConstantItem(55).Text(label + ":").FontSize(6).Bold().FontColor(MutedColor).LetterSpacing(DataLabelTracking);
            cell.RelativeItem().PaddingLeft(4).Text(value).FontSize(8).FontColor(InkColor);
        });
    }

    // ============================================================================================
    // RIEL DE ITINERARIO: envoltorio común de cada sección (AÉREOS/HOTEL/TRASLADOS/OPCIONES/OTROS).
    // Dibuja la línea vertical + el nodo en ACENTO a la altura del título, e indenta el contenido 20pt
    // (spec §1, "firma visual" de esta maqueta).
    // ============================================================================================

    private void ComposeSectionWithRail(ColumnDescriptor column, string title, Color accentColor, string accentHex, Action<ColumnDescriptor> content)
    {
        column.Item().PaddingTop(16).Row(row =>
        {
            // El nodo (capa secundaria, NO afecta la altura del layout) va centrado sobre la línea del
            // riel (capa primaria), pegado arriba, a la altura del título de la sección.
            row.ConstantItem(RailColumnWidth).Layers(layers =>
            {
                layers.PrimaryLayer().AlignCenter().Width(RailLineWidth).Background(RuleColor);
                layers.Layer().AlignTop().AlignCenter()
                    .Width(RailNodeDiameter).Height(RailNodeDiameter).Svg(BuildRailNodeSvg(accentHex));
            });

            row.RelativeItem().Column(sectionColumn =>
            {
                sectionColumn.Item().Row(titleRow =>
                {
                    titleRow.AutoItem().Text(title).FontSize(7).Bold().FontColor(accentColor).LetterSpacing(SectionTitleTracking);
                    titleRow.RelativeItem().PaddingLeft(8).BorderBottom(RailLineWidth).BorderColor(RuleColor);
                });

                sectionColumn.Item().PaddingTop(8).Element(e => content(sectionColumn));
            });
        });
    }

    // ============================================================================================
    // AÉREOS: caja con DOS filas por vuelo vivo (IDA y VUELTA, cuando corresponde) — misma lógica de
    // datos que la ronda anterior de esta obra ("PDF completo", 2026-08-13), solo restyleada.
    // ============================================================================================

    private static List<FlightSegment> FilterLiveFlights(Reserva reserva, HashSet<string> ambiguousGroups)
        => (reserva.FlightSegments ?? new List<FlightSegment>())
            .Where(f => IsLive(f.Status, isFlight: true) && !OptionGroupRules.BelongsToAmbiguousGroup(f.OptionGroup, ambiguousGroups))
            .ToList();

    private void ComposeVuelosContent(ColumnDescriptor column, IReadOnlyList<FlightSegment> flights, string accentHex)
    {
        column.Item().Column(box =>
        {
            var isFirstRow = true;
            foreach (var flight in flights)
            {
                if (!isFirstRow) box.Item().BorderTop(RailLineWidth).BorderColor(RuleColor);
                isFirstRow = false;

                // Fila IDA: siempre se dibuja (todo vuelo cargado tiene, como mínimo, una fecha de ida).
                box.Item().PaddingVertical(8).Element(e => ComposeFlightLegRow(
                    e, flight, accentHex,
                    departureTime: flight.OutboundDepartureTime,
                    arrivalTime: flight.OutboundArrivalTime,
                    fallbackLegDate: flight.DepartureTime,
                    originCode: flight.Origin, originCity: flight.OriginCity,
                    destinationCode: flight.Destination, destinationCity: flight.DestinationCity));

                // Fila VUELTA: aeropuertos invertidos, solo si el vendedor cargó una fecha de vuelta.
                if (QuoteBudgetPdfRules.HasReturnLeg(flight))
                {
                    box.Item().BorderTop(RailLineWidth).BorderColor(RuleColor);
                    box.Item().PaddingVertical(8).Element(e => ComposeFlightLegRow(
                        e, flight, accentHex,
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
    /// Una fila de IDA o de VUELTA: avioncito en acento (sin círculo) + hora/aeropuerto de salida + chip
    /// "Directo" + hora/aeropuerto de llegada (con "+1" si cruza medianoche) + duración + íconos de
    /// equipaje a la derecha. Grilla con anchos FIJOS (spec §1: avión 22/salida 96/chip 50/llegada
    /// 96/duración 60pt) para que ida y vuelta queden alineadas columna contra columna.
    /// </summary>
    private void ComposeFlightLegRow(
        IContainer container,
        FlightSegment flight,
        string accentHex,
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

        var departureTimeText = QuoteBudgetPdfRules.BuildFlightLegDepartureText(departureTime, fallbackLegDate);
        var arrivalTimeText = QuoteBudgetPdfRules.BuildFlightLegArrivalText(arrivalTime);
        var showsArrivalBlock = arrivalTimeText is not null || arrivalAirportLabel is not null;

        container.Row(row =>
        {
            // La columna de grilla (22pt, spec §1) es más ancha que el ícono (15pt): el ícono es
            // CUADRADO (viewBox 24x24) y QuestPDF exige que ancho y alto coincidan para respetar esa
            // relación de aspecto -- pedirle "22 de ancho, 15 de alto" a un SVG cuadrado revienta el
            // layout (DocumentLayoutException). Se centra un cuadro de 15x15 DENTRO de la columna de 22.
            row.ConstantItem(22).AlignMiddle().AlignCenter().Width(15).Height(15).Svg(BuildFlightIconSvg(accentHex));
            row.ConstantItem(8);

            row.ConstantItem(96).AlignMiddle().Column(departure =>
            {
                departure.Item().Text(departureTimeText).ExtraBold().FontSize(13).FontColor(InkColor);
                if (departureAirportLabel is not null)
                    departure.Item().Text(departureAirportLabel).FontSize(7).FontColor(MutedColor).LetterSpacing(SlightTracking);
            });

            // El chip "Directo" se emite SIEMPRE dentro del mismo ancho fijo -- incluso vacío cuando el
            // vuelo no es directo -- para que la columna de llegada no se corra según el vuelo.
            row.ConstantItem(50).AlignMiddle().Element(chip =>
            {
                if (flight.IsDirect == true)
                {
                    // Píldora (ronda de revisión visual, 2026-08-14): con texto 7pt + padding vertical
                    // 3pt arriba/abajo, la caja del chip mide ~14-15pt de alto -- un radio de 8 (algo
                    // más de la mitad) redondea los dos extremos hasta que se ven semicirculares.
                    chip.Border(RailLineWidth).BorderColor(RuleColor).CornerRadius(8)
                        .PaddingHorizontal(6).PaddingVertical(3).Text("Directo").FontSize(7).FontColor(MutedColor);
                }
            });

            row.ConstantItem(96).AlignMiddle().Element(arrivalContainer =>
            {
                if (!showsArrivalBlock) return;

                arrivalContainer.Column(arrival =>
                {
                    if (arrivalTimeText is not null)
                    {
                        arrival.Item().Row(arrivalTimeRow =>
                        {
                            arrivalTimeRow.AutoItem().Text(arrivalTimeText).ExtraBold().FontSize(13).FontColor(InkColor);

                            if (QuoteBudgetPdfRules.IsFlightLegNextDay(departureTime, arrivalTime))
                            {
                                arrivalTimeRow.AutoItem().PaddingLeft(2).AlignTop()
                                    .Text(text => text.Span("+1").FontSize(6.5f).FontColor(Color.FromHex(accentHex)));
                            }
                        });
                    }

                    if (arrivalAirportLabel is not null)
                        arrival.Item().Text(arrivalAirportLabel).FontSize(7).FontColor(MutedColor).LetterSpacing(SlightTracking);
                });
            });

            row.ConstantItem(60).AlignMiddle().Element(durationContainer =>
            {
                if (durationLabel is not null)
                    durationContainer.Text(durationLabel).FontSize(8).FontColor(MutedColor);
            });

            row.RelativeItem(); // separador elástico: empuja el equipaje al borde derecho.

            // Tres íconos SIEMPRE, en este orden (mochila/mano/valija), tamaños 15/14/16pt (spec §1). El
            // que no está incluido en la tarifa se ve "fantasma" (tenue) en vez de desaparecer.
            row.ConstantItem(15).PaddingLeft(4).AlignMiddle()
                .Svg(BuildLuggageIconSvg(BackpackPathData, included: flight.IncludesBackpack == true));
            row.ConstantItem(14).PaddingLeft(3).AlignMiddle()
                .Svg(BuildLuggageIconSvg(HandBagPathData, included: flight.IncludesCarryOn == true));
            row.ConstantItem(16).PaddingLeft(3).AlignMiddle()
                .Svg(BuildLuggageIconSvg(LuggagePathData, included: flight.IncludesCheckedBag == true));
        });
    }

    // ============================================================================================
    // HOTEL: nombre + estrellas en una línea con la tarifa a la derecha; debajo, el subtítulo
    // (habitación · régimen · noches) y las cuotas si están cargadas.
    // ============================================================================================

    private static List<HotelBooking> FilterLiveHotels(Reserva reserva, HashSet<string> ambiguousGroups)
        => (reserva.HotelBookings ?? new List<HotelBooking>())
            .Where(h => IsLive(h.Status, isFlight: false) && !OptionGroupRules.BelongsToAmbiguousGroup(h.OptionGroup, ambiguousGroups))
            .ToList();

    private void ComposeHotelesContent(
        ColumnDescriptor column, IReadOnlyList<HotelBooking> hotels, string accentHex, bool porPersona, int cantidadPasajerosCargados)
    {
        var isFirst = true;
        foreach (var hotel in hotels)
        {
            column.Item().PaddingTop(isFirst ? 0 : 12).Element(e => ComposeHotelBlock(e, hotel, accentHex, porPersona, cantidadPasajerosCargados));
            isFirst = false;
        }
    }

    private void ComposeHotelBlock(IContainer container, HotelBooking hotel, string accentHex, bool porPersona, int cantidadPasajerosCargados)
    {
        container.Column(column =>
        {
            column.Item().Row(titleRow =>
            {
                titleRow.AutoItem().Text(hotel.HotelName).FontSize(12).Bold().FontColor(InkColor);

                if (hotel.StarRating is > 0)
                {
                    titleRow.ConstantItem(8);
                    titleRow.AutoItem().AlignMiddle().Element(e => ComposeStarRating(e, Math.Min(hotel.StarRating.Value, 5), accentHex));
                }

                titleRow.RelativeItem().AlignRight()
                    .Element(e => ComposeBareAmount(e, hotel.SalePrice, hotel.Currency, porPersona, cantidadPasajerosCargados, amountFontSize: 10.5f, currencyFontSize: 7f));
            });

            var subtitleLine = QuoteBudgetPdfRules.BuildHotelSubtitleLine(hotel);
            if (subtitleLine is not null)
            {
                column.Item().PaddingTop(3).Text(subtitleLine).FontSize(8).FontColor(MutedColor);
            }

            var installmentsLine = QuoteBudgetPdfRules.BuildInstallmentsLine(hotel.InstallmentsCount, hotel.InstallmentAmount, hotel.Currency);
            if (installmentsLine is not null)
            {
                column.Item().PaddingTop(2).Text(installmentsLine).FontSize(7.5f).FontColor(MutedColor).LetterSpacing(SlightTracking);
            }
        });
    }

    private void ComposeStarRating(IContainer container, int starCount, string accentHex)
    {
        container.Row(row =>
        {
            for (var i = 0; i < starCount; i++)
            {
                row.ConstantItem(9.5f).PaddingRight(1).Svg(BuildStarSvg(accentHex));
            }
        });
    }

    // ============================================================================================
    // TRASLADOS / OTROS: filas simples "nombre a la izquierda, tarifa en negrita a la derecha" (spec
    // §1: "mismo patrón" que hotel, sin la etiqueta "TARIFA POR PERSONA:"/"TARIFA TOTAL:" de la maqueta
    // anterior). OTROS agrupa lo que antes vivía junto a traslados: paquetes, asistencias y el
    // servicio genérico "Otro" -- traslados ahora tiene SU PROPIA sección en el riel (spec §1 lista
    // "TRASLADOS" aparte de "OTROS").
    // ============================================================================================

    private static List<TransferBooking> FilterLiveTransfers(Reserva reserva, HashSet<string> ambiguousGroups)
        => (reserva.TransferBookings ?? new List<TransferBooking>())
            .Where(t => IsLive(t.Status, isFlight: false) && !OptionGroupRules.BelongsToAmbiguousGroup(t.OptionGroup, ambiguousGroups))
            .ToList();

    private sealed record SimpleServiceRow(string DisplayName, decimal SalePrice, string? Currency);

    private static List<SimpleServiceRow> BuildOtrosRows(Reserva reserva, HashSet<string> ambiguousGroups)
    {
        var rows = new List<SimpleServiceRow>();

        foreach (var package in (reserva.PackageBookings ?? new List<PackageBooking>())
            .Where(p => IsLive(p.Status, isFlight: false) && !OptionGroupRules.BelongsToAmbiguousGroup(p.OptionGroup, ambiguousGroups)))
        {
            rows.Add(new SimpleServiceRow(package.PackageName, package.SalePrice, package.Currency));
        }

        foreach (var assistance in (reserva.AssistanceBookings ?? new List<AssistanceBooking>())
            .Where(a => IsLive(a.Status, isFlight: false) && !OptionGroupRules.BelongsToAmbiguousGroup(a.OptionGroup, ambiguousGroups)))
        {
            var name = string.IsNullOrWhiteSpace(assistance.PlanType) ? "Asistencia" : assistance.PlanType!;
            rows.Add(new SimpleServiceRow(name, assistance.SalePrice, assistance.Currency));
        }

        foreach (var otro in (reserva.Servicios ?? new List<ServicioReserva>())
            .Where(s => IsLive(s.Status, isFlight: false)))
        {
            rows.Add(new SimpleServiceRow(QuoteBudgetPdfRules.BuildOtroServiceDisplayName(otro), otro.SalePrice, otro.Currency));
        }

        return rows;
    }

    private void ComposeSimpleServiceRows(ColumnDescriptor column, IReadOnlyList<TransferBooking> transfers, bool porPersona, int cantidadPasajerosCargados)
        => ComposeSimpleServiceRows(column, transfers.Select(t => new SimpleServiceRow(QuoteBudgetPdfRules.BuildTrasladoLine(t) ?? "Traslado", t.SalePrice, t.Currency)).ToList(), porPersona, cantidadPasajerosCargados);

    private void ComposeSimpleServiceRows(ColumnDescriptor column, IReadOnlyList<SimpleServiceRow> rows, bool porPersona, int cantidadPasajerosCargados)
    {
        var isFirst = true;
        foreach (var row in rows)
        {
            if (!isFirst)
            {
                column.Item().PaddingTop(6).BorderTop(RailLineWidth).BorderColor(RuleColor);
            }

            column.Item().PaddingTop(isFirst ? 0 : 6).Row(rowContent =>
            {
                rowContent.RelativeItem().Text(row.DisplayName).FontSize(9.5f).FontColor(InkColor);
                rowContent.ConstantItem(140).AlignRight()
                    .Element(e => ComposeBareAmount(e, row.SalePrice, row.Currency, porPersona, cantidadPasajerosCargados, amountFontSize: 9f, currencyFontSize: 9f));
            });

            isFirst = false;
        }
    }

    // ============================================================================================
    // OPCIONES A/B/C.
    // ============================================================================================

    private void ComposeOpcionesContent(
        ColumnDescriptor column,
        IReadOnlyDictionary<string, IReadOnlyList<QuoteBudgetPdfRules.QuoteOptionCandidate>> optionGroups,
        bool porPersona,
        int cantidadPasajerosCargados)
    {
        var isFirstGroup = true;
        foreach (var (groupName, candidates) in optionGroups)
        {
            column.Item().PaddingTop(isFirstGroup ? 0 : 10).Column(groupColumn =>
            {
                groupColumn.Item().Text(groupName.ToUpperInvariant()).FontSize(9).Bold().FontColor(InkColor);

                foreach (var candidate in candidates)
                {
                    var label = string.IsNullOrWhiteSpace(candidate.OptionLabel)
                        ? candidate.DisplayName
                        : $"Opción {candidate.OptionLabel} — {candidate.DisplayName}";

                    groupColumn.Item().PaddingTop(4).Row(row =>
                    {
                        row.RelativeItem().Text(label).FontSize(9.5f).FontColor(InkColor);
                        row.ConstantItem(140).AlignRight()
                            .Element(e => ComposeBareAmount(e, candidate.SalePrice, candidate.Currency, porPersona, cantidadPasajerosCargados, amountFontSize: 9f, currencyFontSize: 9f));
                    });
                }
            });
            isFirstGroup = false;
        }
    }

    /// <summary>
    /// El monto "desnudo" (spec §1): sin el prefijo "TARIFA POR PERSONA:"/"TARIFA TOTAL:" de la maqueta
    /// anterior -- el NÚMERO sigue calculándose exactamente igual (misma lógica por persona/total,
    /// opción A firmada 13/08, ver <see cref="QuoteBudgetPdfRules.ResolveDisplayPrice"/>), solo cambia
    /// que ahora se muestra bien a la derecha, en negrita, sin la etiqueta de texto.
    /// </summary>
    private void ComposeBareAmount(
        IContainer container, decimal salePrice, string? currency, bool porPersona, int cantidadPasajerosCargados,
        float amountFontSize, float currencyFontSize)
    {
        var display = QuoteBudgetPdfRules.ResolveDisplayPrice(salePrice, cantidadPasajerosCargados, porPersona);
        var formattedAmount = QuoteBudgetPdfRules.BuildAmountLabel(display.Amount, currency: null);

        container.Text(text =>
        {
            text.Span(formattedAmount).Bold().FontSize(amountFontSize).FontColor(InkColor);

            if (!string.IsNullOrWhiteSpace(currency))
            {
                text.Span(" " + currency).FontSize(currencyFontSize).FontColor(MutedColor);
            }
        });
    }

    // ============================================================================================
    // TARJETA DE TOTAL (spec §1): filete superior en acento + "TOTAL DEL VIAJE" + nota de qué incluye a
    // la izquierda; el monto grande (Marcellus) + "por persona" cuando corresponde, a la derecha.
    // ============================================================================================

    private void ComposeTotalCard(
        ColumnDescriptor column,
        Reserva reserva,
        Color accentColor,
        bool hasFlights,
        bool hasHotel,
        bool hasTransfers,
        bool hasOthers,
        bool porPersona,
        int cantidadPasajerosCargados)
    {
        // ReservaMoneyCalculator es la MISMA fuente que decide "cuánto vale la reserva" en el resto del
        // sistema (cuenta corriente, cobranzas): reusarla acá, en vez de sumar los SalePrice a mano,
        // evita que la tarjeta de total del PDF diga un número distinto del que ve el cajero.
        var summary = ReservaMoneyCalculator.Calculate(reserva);
        var totalLines = summary.PorMoneda.Values
            .Where(line => line.TotalSale > 0m)
            .OrderBy(line => line.Currency, StringComparer.Ordinal)
            .ToList();

        if (totalLines.Count == 0) return; // nada cargado todavía -- la tarjeta entera se omite (espejo de lo cargado).

        var includesNote = QuoteBudgetPdfRules.BuildTotalCardIncludesNote(hasFlights, hasHotel, hasTransfers, hasOthers);

        // Una reserva con MÁS DE UNA moneda viva (ADR-021: nunca se mezcla USD con ARS en un solo
        // número) imprime una línea de monto por moneda -- el tamaño baja un poco para que dos montos
        // grandes en Marcellus no se pisen entre sí.
        var amountFontSize = totalLines.Count > 1 ? 20f : 30f;

        column.Item().PaddingTop(18).Column(card =>
        {
            card.Item().PaddingTop(10).BorderTop(1.5f).BorderColor(accentColor).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text("TOTAL DEL VIAJE").FontSize(7).Bold().FontColor(accentColor).LetterSpacing(SectionTitleTracking);

                    if (includesNote is not null)
                    {
                        left.Item().PaddingTop(4).Width(210).Text(includesNote).FontSize(7).FontColor(MutedColor);
                    }
                });

                row.ConstantItem(190).AlignRight().Column(right =>
                {
                    foreach (var line in totalLines)
                    {
                        right.Item().Text(text =>
                        {
                            var span = text.Span(QuoteBudgetPdfRules.BuildAmountLabel(line.TotalSale, line.Currency))
                                .FontSize(amountFontSize).FontColor(InkColor);
                            ApplyMarcellusIfAvailable(span);
                        });

                        if (porPersona && cantidadPasajerosCargados > 0)
                        {
                            var perPersonAmount = Math.Round(line.TotalSale / cantidadPasajerosCargados, 2, MidpointRounding.AwayFromZero);
                            var perPersonLabel = QuoteBudgetPdfRules.BuildAmountLabel(perPersonAmount, line.Currency);
                            right.Item().Text($"{perPersonLabel} por persona").FontSize(8).FontColor(MutedColor);
                        }
                    }
                });
            });
        });
    }

    // ============================================================================================
    // PIE: legal (nombre + legajo + dirección/teléfono, todos apagados) a la izquierda, paginación a la
    // derecha. Compartido entre página 1 y página 2 (spec §1/§2).
    // ============================================================================================

    private void ComposeFooter(IContainer container, AgencySettings agencySettings)
    {
        container.PaddingTop(10).PaddingHorizontal(ContentHorizontalPadding).PaddingBottom(18)
            .BorderTop(RailLineWidth).BorderColor(RuleColor).PaddingTop(8).Row(row =>
        {
            row.RelativeItem().Element(e => ComposeFooterLegalText(e, agencySettings));

            row.ConstantItem(70).AlignRight().Text(text =>
            {
                text.CurrentPageNumber().Format(n => (n ?? 0).ToString("00")).FontSize(6.5f).FontColor(MutedColor).LetterSpacing(WideTracking);
                text.Span(" / ").FontSize(6.5f).FontColor(MutedColor);
                text.TotalPages().Format(n => (n ?? 0).ToString("00")).FontSize(6.5f).FontColor(MutedColor).LetterSpacing(WideTracking);
            });
        });
    }

    /// <summary>
    /// Leyenda fija "Documento no válido como factura" (pedido firmado por el dueño 2026-08-11, maqueta
    /// aprobada §1): NO depende de ningún dato de Ajustes -- va SIEMPRE, primera, en todo presupuesto.
    /// Este PDF nunca es un comprobante fiscal, así que el texto no puede quedar atado a que la agencia
    /// haya cargado algo.
    /// </summary>
    private const string NotAnInvoiceLegend = "Documento no válido como factura";

    /// <summary>
    /// Leyenda fija + nombre + legajo + dirección/teléfono, todo itálica 6.5pt apagada (spec §1). La
    /// leyenda fija SIEMPRE va; nombre/legajo se omiten si su dato de origen no está cargado -- nunca un
    /// placeholder inventado. "Legajo {número}" imprime el valor TAL CUAL lo cargó la agencia (si incluye
    /// el prefijo "EVT", es porque así lo cargaron).
    /// </summary>
    private void ComposeFooterLegalText(IContainer container, AgencySettings agencySettings)
    {
        container.Column(column =>
        {
            var mainName = !string.IsNullOrWhiteSpace(agencySettings.AgencyName) ? agencySettings.AgencyName : agencySettings.LegalName;
            var legalParts = new List<string> { NotAnInvoiceLegend };
            if (!string.IsNullOrWhiteSpace(mainName)) legalParts.Add(mainName!.Trim());
            if (!string.IsNullOrWhiteSpace(agencySettings.AgencyLicenseNumber)) legalParts.Add($"Legajo {agencySettings.AgencyLicenseNumber}");

            if (legalParts.Count > 0)
            {
                column.Item().Text(string.Join(" · ", legalParts)).FontSize(6.5f).Italic().FontColor(MutedColor);
            }

            var addressAndPhoneParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(agencySettings.Address)) addressAndPhoneParts.Add(agencySettings.Address!.Trim());
            if (!string.IsNullOrWhiteSpace(agencySettings.Phone)) addressAndPhoneParts.Add($"Tel {agencySettings.Phone!.Trim()}");

            if (addressAndPhoneParts.Count > 0)
            {
                column.Item().PaddingTop(1).Text(string.Join(" · ", addressAndPhoneParts)).FontSize(6.5f).Italic().FontColor(MutedColor);
            }
        });
    }

    // ============================================================================================
    // PÁGINA 2 (spec §2, REDISEÑADA): cabecera compacta + "FORMAS DE PAGO" (que en esta maqueta se
    // mudó acá, ya no vive en la página 1) + "CONDICIONES". La página entera solo existe si hay algo
    // que mostrar en cualquiera de las dos secciones (decidido en GenerateQuotePdf).
    // ============================================================================================

    private void ComposeConditionsPage(
        PageDescriptor page,
        Reserva reserva,
        AgencySettings agencySettings,
        Color accentColor,
        byte[]? logoBytes,
        string? paymentTermsText,
        IReadOnlyList<BudgetConditionBlock> conditions)
    {
        page.Size(PageSizes.A4);
        page.PageColor(Colors.White);
        page.DefaultTextStyle(x => x.FontSize(9).FontColor(InkColor));

        var destinationTitle = QuoteBudgetPdfRules.ResolveDestinationTitle(reserva.HotelBookings, reserva.PackageBookings);

        page.Header().PaddingHorizontal(ContentHorizontalPadding).PaddingTop(24)
            .Element(e => ComposePage2Header(e, agencySettings, logoBytes, reserva.NumeroReserva, destinationTitle));

        page.Content().PaddingHorizontal(ContentHorizontalPadding).PaddingTop(18).Column(content =>
        {
            if (paymentTermsText is not null)
            {
                content.Item().Column(section =>
                {
                    section.Item().Text("FORMAS DE PAGO").FontSize(7).Bold().FontColor(accentColor).LetterSpacing(SectionTitleTracking);
                    section.Item().PaddingTop(6)
                        .Text(paymentTermsText).FontSize(8).FontColor(SoftInkColor).LineHeight(1.8f);
                });
            }

            if (conditions.Count > 0)
            {
                content.Item().PaddingTop(paymentTermsText is null ? 0 : 18).Column(section =>
                {
                    section.Item().Text("CONDICIONES").FontSize(7).Bold().FontColor(accentColor).LetterSpacing(SectionTitleTracking);

                    section.Item().PaddingTop(8).Column(blocks =>
                    {
                        foreach (var block in conditions)
                        {
                            var tituloBloque = block.Kind == BudgetConditionBlockKind.Flights
                                ? "Aéreos" // "Aereos" es la CLAVE de la API (sin tilde, no se puede cambiar); al cliente se le muestra bien escrita.
                                : BudgetConditionBlockKindText.ToDisplayText(block.Kind);

                            blocks.Item().PaddingBottom(10).Column(blockColumn =>
                            {
                                blockColumn.Item().Text(tituloBloque.ToUpperInvariant())
                                    .FontSize(6.5f).FontColor(MutedColor).LetterSpacing(SlightTracking);
                                blockColumn.Item().PaddingTop(2).MaxWidth(480)
                                    .Text(block.Text).FontSize(7.5f).FontColor(SoftInkColor).LineHeight(1.65f);
                            });
                        }
                    });
                });
            }
        });

        page.Footer().Element(e => ComposeFooter(e, agencySettings));
    }

    private void ComposePage2Header(IContainer container, AgencySettings agencySettings, byte[]? logoBytes, string numeroReserva, string? destinationTitle)
    {
        container.Column(header =>
        {
            header.Item().Row(row =>
            {
                row.RelativeItem().Element(e => ComposeCompactAgencyIdentity(e, agencySettings, logoBytes));

                var rightParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(numeroReserva)) rightParts.Add(numeroReserva);
                if (!string.IsNullOrWhiteSpace(destinationTitle)) rightParts.Add(destinationTitle!.ToUpperInvariant());

                var rightLabel = rightParts.Count == 0 ? "PRESUPUESTO" : $"PRESUPUESTO {string.Join(" · ", rightParts)}";
                row.ConstantItem(280).AlignRight().Text(rightLabel).FontSize(7).FontColor(MutedColor).LetterSpacing(SlightTracking);
            });

            // TRAMPA (ronda de revisión visual, 2026-08-14): un item de Column sin NINGÚN contenido
            // (ni Text ni Image ni Svg) tiene tamaño natural 0x0 -- BorderBottom() dibuja un borde
            // ALREDEDOR de esa caja, y una caja de 0x0 no tiene un borde visible para pintar. En un Row
            // esto no pasa (el RelativeItem/AutoItem vecino le da un ancho real a la celda), pero acá es
            // un Column.Item() suelto. Se reemplaza el borde por una barra de fondo con ALTO EXPLÍCITO
            // (Height + Background): eso fuerza una caja de verdad, sin depender de que haya contenido.
            header.Item().PaddingTop(10).Height(RailLineWidth).Background(RuleColor);
        });
    }

    /// <summary>Versión chica de la identidad de la agencia para la cabecera de página 2 (spec §2).</summary>
    private void ComposeCompactAgencyIdentity(IContainer container, AgencySettings agencySettings, byte[]? logoBytes)
    {
        if (logoBytes is { Length: > 0 })
        {
            try
            {
                container.Height(14).AlignLeft().Image(logoBytes).FitHeight();
                return;
            }
            catch
            {
                // mismo criterio que en la página 1: un logo que no se puede decodificar no rompe el PDF.
            }
        }

        var mainName = !string.IsNullOrWhiteSpace(agencySettings.AgencyName) ? agencySettings.AgencyName : agencySettings.LegalName;
        if (string.IsNullOrWhiteSpace(mainName)) return;

        // Fix ronda de revisión visual (2026-08-14): la maqueta muestra SOLO el wordmark ("MAGNA"), no
        // el nombre completo de la agencia -- mismo recorte que la página 1 (ver SplitAgencyWordmark),
        // acá se descarta el "resto del nombre" a propósito (la cabecera compacta no tiene lugar para
        // dos líneas).
        var (wordmark, _) = SplitAgencyWordmark(mainName.Trim());

        container.Text(text =>
        {
            var span = text.Span(wordmark.ToUpperInvariant()).FontSize(10).FontColor(InkColor);
            ApplyMarcellusIfAvailable(span);
        });
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
