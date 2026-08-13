using System;
using System.Collections.Generic;
using System.IO;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// NO es un test de regresión automática (no assertea píxeles — QuestPDF genera bytes, no se puede leer
/// el contenido visual desde un xUnit test). Es un HARNESS de verificación manual para la ronda
/// "recalcar la maqueta" (2026-08-13): arma un presupuesto con los MISMOS datos que la maqueta firmada
/// (Punta Cana, 10/02-15/02/2027, 2 tramos EZE↔PUJ directos, 2 hoteles de 4 estrellas, formas de pago,
/// condiciones) y guarda el PDF en disco para inspección visual manual (Read tool sobre el PDF).
///
/// <para>Se deja en el repo (no se borra al cerrar la ronda) porque sirve para la PRÓXIMA vez que se
/// toque este renderer: correrlo de nuevo y volver a mirar el PDF a ojo es más rápido que reconstruir
/// el dataset de prueba desde cero.</para>
/// </summary>
public class QuotePdfServiceMaquetaVisualHarnessTests
{
    // Carpeta temporal DEL SISTEMA (no una ruta fija de una máquina puntual): el mismo test corre
    // en Windows local y en el runner Linux del CI sin rutas que no existan.
    private static readonly string OutputPath =
        Path.Combine(Path.GetTempPath(), "pdf-maqueta-check.pdf");

    [Fact]
    public void GenerateQuotePdf_MaquetaSampleData_ProducesTwoPagePdf_SavedForVisualInspection()
    {
        var reserva = BuildSampleReserva();
        var agencySettings = BuildSampleAgencySettings();
        var conditions = BuildSampleConditions();

        var service = new QuotePdfService();

        var pdfBytes = service.GenerateQuotePdf(
            reserva: reserva,
            agencySettings: agencySettings,
            conditions: conditions,
            logoBytes: null, // sin logo cargado -> la banda sale sola, nunca con un placeholder inventado.
            porPersona: false,
            cantidadPasajerosCargados: reserva.AdultCount + reserva.ChildCount + reserva.InfantCount);

        Assert.NotEmpty(pdfBytes);
        // "%PDF-" son los primeros 5 bytes de cualquier PDF válido (firma del formato) — chequeo barato
        // de que QuestPDF de verdad devolvió un documento, no un array vacío o basura.
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 5));

        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
        File.WriteAllBytes(OutputPath, pdfBytes);
    }

    // ================================================================================
    // Casos borde del render (no cubiertos por la muestra "espejo perfecto" de arriba): la lógica de
    // CADA elemento omitido ya está probada como función pura en QuoteBudgetPdfRulesTests — esto
    // verifica que el RENDERER (QuestPDF, Svg/Row anidados) no explote con combinaciones degeneradas
    // reales: tramo solo con hora de salida (sin llegada ni aeropuertos), tramo que cruza medianoche
    // (badge "+1"), hotel sin estrellas cargadas, reserva sin ningún servicio.
    // ================================================================================

    [Fact]
    public void GenerateQuotePdf_MinimalFlightRow_OnlyDepartureTimeLoaded_DoesNotThrow()
    {
        var reserva = new Reserva
        {
            Id = 2,
            FlightSegments = new List<FlightSegment>
            {
                // Único dato garantizado: DepartureTime. Sin llegada, sin aeropuertos, sin chip Directo,
                // sin equipaje — la fila debe salir con SOLO la hora de salida, nada inventado.
                new() { Status = "NN", DepartureTime = new DateTime(2027, 3, 1, 9, 0, 0) },
            },
        };

        var pdfBytes = new QuotePdfService().GenerateQuotePdf(
            reserva, BuildSampleAgencySettings(), Array.Empty<BudgetConditionBlock>(), logoBytes: null,
            porPersona: false, cantidadPasajerosCargados: 0);

        Assert.NotEmpty(pdfBytes);
    }

    [Fact]
    public void GenerateQuotePdf_FlightCrossingMidnight_ShowsPlusOneBadge_DoesNotThrow()
    {
        var reserva = new Reserva
        {
            Id = 3,
            FlightSegments = new List<FlightSegment>
            {
                new()
                {
                    Status = "NN",
                    Origin = "EZE",
                    Destination = "MAD",
                    DepartureTime = new DateTime(2027, 3, 1, 23, 30, 0),
                    ArrivalTime = new DateTime(2027, 3, 2, 13, 10, 0), // llega al día siguiente -> "+1".
                    IsDirect = true,
                },
            },
        };

        var pdfBytes = new QuotePdfService().GenerateQuotePdf(
            reserva, BuildSampleAgencySettings(), Array.Empty<BudgetConditionBlock>(), logoBytes: null,
            porPersona: false, cantidadPasajerosCargados: 0);

        Assert.NotEmpty(pdfBytes);
    }

    [Fact]
    public void GenerateQuotePdf_HotelWithoutStarRating_DoesNotThrow()
    {
        var reserva = new Reserva
        {
            Id = 4,
            HotelBookings = new List<HotelBooking>
            {
                new() { Status = "Solicitado", HotelName = "Hostal sin categoría", City = "Bariloche", SalePrice = 500m },
            },
        };

        var pdfBytes = new QuotePdfService().GenerateQuotePdf(
            reserva, BuildSampleAgencySettings(), Array.Empty<BudgetConditionBlock>(), logoBytes: null,
            porPersona: false, cantidadPasajerosCargados: 0);

        Assert.NotEmpty(pdfBytes);
    }

    [Fact]
    public void GenerateQuotePdf_ReservaWithoutAnyService_DoesNotThrow()
    {
        // Caso degenerado: reserva recién creada, sin nada cargado todavía. Ningún bloque del PDF tiene
        // datos para mostrar (excepto banda/pie) — debe generar un PDF válido, no explotar.
        var reserva = new Reserva { Id = 5 };

        var pdfBytes = new QuotePdfService().GenerateQuotePdf(
            reserva, BuildSampleAgencySettings(), Array.Empty<BudgetConditionBlock>(), logoBytes: null,
            porPersona: false, cantidadPasajerosCargados: 0);

        Assert.NotEmpty(pdfBytes);
    }

    // ================================================================================
    // Barrido ronda 2 (2026-08-13): 4 escenarios que cubren TODOS los tipos de servicio + los 2 bugs
    // reportados por el dueño en PROD (margen inconsistente en la página 1/2, vuelo sin horarios
    // mostrando basura). Cada PDF se guarda en Path.GetTempPath() con nombre "pdf-check-<escenario>.pdf"
    // para inspección visual manual (Read tool sobre el PDF) — complementan, no reemplazan, los asserts
    // automáticos de arriba (que solo verifican "es un PDF válido", no pueden leer píxeles).
    // ================================================================================

    [Fact]
    public void GenerateQuotePdf_CompleteScenario_AllSixServiceTypes_SavedForVisualInspection()
    {
        var reserva = BuildCompleteScenarioReserva();

        var pdfBytes = new QuotePdfService().GenerateQuotePdf(
            reserva, BuildSampleAgencySettings(), BuildSampleConditions(), logoBytes: null,
            porPersona: true, // "TARIFA POR PERSONA:" -- contrastar con GenerateQuotePdf_TotalPricingScenario (mismos datos, TARIFA TOTAL).
            cantidadPasajerosCargados: reserva.AdultCount + reserva.ChildCount + reserva.InfantCount);

        AssertValidPdfAndSave(pdfBytes, "completo");
    }

    [Fact]
    public void GenerateQuotePdf_TotalPricingScenario_SameDataAsCompleto_ShowsTarifaTotal_SavedForVisualInspection()
    {
        var reserva = BuildCompleteScenarioReserva();

        var pdfBytes = new QuotePdfService().GenerateQuotePdf(
            reserva, BuildSampleAgencySettings(), BuildSampleConditions(), logoBytes: null,
            porPersona: false, // fuerza "TARIFA TOTAL:" en vez de "TARIFA POR PERSONA:", mismos datos que "completo".
            cantidadPasajerosCargados: reserva.AdultCount + reserva.ChildCount + reserva.InfantCount);

        AssertValidPdfAndSave(pdfBytes, "total");
    }

    [Fact]
    public void GenerateQuotePdf_EmptyDataScenario_NoInventedGarbage_SavedForVisualInspection()
    {
        var reserva = BuildEmptyDataScenarioReserva();

        var pdfBytes = new QuotePdfService().GenerateQuotePdf(
            reserva, BuildSampleAgencySettings(), Array.Empty<BudgetConditionBlock>(), logoBytes: null,
            porPersona: false, cantidadPasajerosCargados: 0);

        AssertValidPdfAndSave(pdfBytes, "vacios");
    }

    [Fact]
    public void GenerateQuotePdf_SingleServiceScenario_NoInventedGarbage_SavedForVisualInspection()
    {
        // Complementa el caso anterior con el otro extremo: UN solo servicio cargado en toda la reserva
        // (nada de vuelos, traslados ni opciones) -- ninguna sección vacía debe dejar un espacio raro ni
        // una etiqueta colgada sin su valor.
        var reserva = new Reserva
        {
            Id = 8,
            HotelBookings = new List<HotelBooking>
            {
                new()
                {
                    Status = "Solicitado",
                    HotelName = "Hotel Único",
                    City = "Mendoza",
                    SalePrice = 900m,
                    Currency = "USD",
                },
            },
        };

        var pdfBytes = new QuotePdfService().GenerateQuotePdf(
            reserva, BuildSampleAgencySettings(), Array.Empty<BudgetConditionBlock>(), logoBytes: null,
            porPersona: false, cantidadPasajerosCargados: 0);

        AssertValidPdfAndSave(pdfBytes, "vacios-un-servicio");
    }

    [Fact]
    public void GenerateQuotePdf_OptionsScenario_HotelsGroupedAsABC_SavedForVisualInspection()
    {
        var reserva = BuildOptionsScenarioReserva();

        var pdfBytes = new QuotePdfService().GenerateQuotePdf(
            reserva, BuildSampleAgencySettings(), Array.Empty<BudgetConditionBlock>(), logoBytes: null,
            porPersona: false, cantidadPasajerosCargados: 0);

        AssertValidPdfAndSave(pdfBytes, "opciones");
    }

    /// <summary>Assert común de "es un PDF válido" (firma "%PDF-") + lo graba en disco para inspección visual (Read tool).</summary>
    private static void AssertValidPdfAndSave(byte[] pdfBytes, string scenarioName)
    {
        Assert.NotEmpty(pdfBytes);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 5));

        var path = Path.Combine(Path.GetTempPath(), $"pdf-check-{scenarioName}.pdf");
        File.WriteAllBytes(path, pdfBytes);
    }

    /// <summary>
    /// Escenario (a) del barrido: los 6 tipos de servicio a la vez (vuelo, hotel, traslado, paquete,
    /// asistencia, genérico) + formas de pago + condiciones de página 2. El vuelo cruza medianoche de
    /// verdad (23:15 -&gt; 13:40 del día siguiente) para verificar que el "+1" REAL sigue funcionando
    /// después del fix de "medianoche de relleno" (no se rompió el caso positivo al arreglar el negativo).
    /// </summary>
    private static Reserva BuildCompleteScenarioReserva()
    {
        return new Reserva
        {
            Id = 6,
            StartDate = new DateTime(2027, 4, 10),
            EndDate = new DateTime(2027, 4, 17),
            AdultCount = 2,
            ChildCount = 1,
            InfantCount = 0,
            BudgetPaymentTermsText = "Seña del 30% al confirmar. Saldo 70% hasta 30 días antes de la salida.",
            FlightSegments = new List<FlightSegment>
            {
                new()
                {
                    Status = "NN",
                    Origin = "EZE",
                    OriginCity = "Buenos Aires",
                    Destination = "MAD",
                    DestinationCity = "Madrid",
                    DepartureTime = new DateTime(2027, 4, 10, 23, 15, 0),
                    ArrivalTime = new DateTime(2027, 4, 11, 13, 40, 0), // cruza medianoche DE VERDAD -> "+1" real, debe seguir apareciendo.
                    IsDirect = true,
                    IncludesBackpack = true,
                    IncludesCarryOn = true,
                    IncludesCheckedBag = true,
                },
            },
            HotelBookings = new List<HotelBooking>
            {
                new()
                {
                    Status = "Solicitado",
                    HotelName = "Hotel Palace Madrid",
                    City = "Madrid",
                    StarRating = 5,
                    RoomType = "Doble",
                    RoomCategory = "Superior",
                    MealPlan = "Desayuno",
                    Nights = 7,
                    SalePrice = 2100m,
                    Currency = "USD",
                },
            },
            TransferBookings = new List<TransferBooking>
            {
                new()
                {
                    Status = "Solicitado",
                    ProductName = "Traslado privado aeropuerto - hotel",
                    SalePrice = 60m,
                    Currency = "USD",
                },
            },
            PackageBookings = new List<PackageBooking>
            {
                new()
                {
                    Status = "Solicitado",
                    PackageName = "City tour Madrid + Toledo",
                    Destination = "Madrid",
                    SalePrice = 180m,
                    Currency = "USD",
                },
            },
            AssistanceBookings = new List<AssistanceBooking>
            {
                new()
                {
                    Status = "Solicitado",
                    PlanType = "Asistencia al viajero Full",
                    SalePrice = 90m,
                    Currency = "USD",
                },
            },
            // Servicio "genérico" (ServicioReserva, ServiceType=Otro): HOY NO tiene bloque propio en
            // QuotePdfService (gap real, reportado aparte -- ver el inventario del reporte de esta ronda,
            // fuera del alcance de los 3 fixes autorizados). Se carga ACÁ A PROPÓSITO para verificar que
            // una reserva completa con este tipo presente sigue generando un PDF válido (no explota),
            // aunque el papel no lo dibuje todavía.
            Servicios = new List<ServicioReserva>
            {
                new()
                {
                    Status = "Solicitado",
                    ServiceType = ServiceTypes.Other,
                    Description = "Alquiler de auto 7 días",
                    SalePrice = 350m,
                    Currency = "USD",
                },
            },
        };
    }

    /// <summary>
    /// Escenario (b) del barrido: vuelo sin horarios reales (reproduce el bug EXACTO de PROD: las dos
    /// puntas a medianoche en punto) + hotel sin estrellas ni detalle de habitación + servicio sin moneda
    /// cargada. Nada de esto debe imprimir basura ("00:00", "+1" inventado, "ARS" inventada, "Habitación:
    /// Doble" cuando nadie cargó el detalle).
    /// </summary>
    private static Reserva BuildEmptyDataScenarioReserva()
    {
        return new Reserva
        {
            Id = 7,
            FlightSegments = new List<FlightSegment>
            {
                new()
                {
                    Status = "NN",
                    DepartureTime = new DateTime(2027, 5, 1, 0, 0, 0),
                    ArrivalTime = new DateTime(2027, 5, 2, 0, 0, 0), // "medianoche de relleno" -- antes rendia "00:00 00:00 +1 · 24h".
                },
            },
            HotelBookings = new List<HotelBooking>
            {
                new()
                {
                    Status = "Solicitado",
                    HotelName = "Hostal sin categoría ni moneda",
                    City = "Córdoba",
                    StarRating = null,
                    RoomType = string.Empty, // sin detalle de habitación cargado -- el default de la entidad es "Doble".
                    RoomCategory = null,
                    SalePrice = 300m,
                    Currency = null, // servicio sin moneda cargada -- nunca debe inventar "ARS".
                },
            },
        };
    }

    /// <summary>
    /// Escenario (c) del barrido: 3 hoteles compitiendo por el mismo <c>OptionGroup</c> ("hoteles"),
    /// etiquetados A/B/C -- deben listarse bajo "OPCIONES – hoteles" con su tarifa cada uno, NO como
    /// bloques de hotel sueltos (esos ya se filtran del bloque Hoteles normal, ver ComposeHoteles).
    /// </summary>
    private static Reserva BuildOptionsScenarioReserva()
    {
        return new Reserva
        {
            Id = 9,
            HotelBookings = new List<HotelBooking>
            {
                new()
                {
                    Status = "Solicitado", HotelName = "Hotel Económico", City = "Bariloche", StarRating = 3,
                    OptionGroup = "hoteles", OptionLabel = "A", SalePrice = 800m, Currency = "USD",
                },
                new()
                {
                    Status = "Solicitado", HotelName = "Hotel Confort", City = "Bariloche", StarRating = 4,
                    OptionGroup = "hoteles", OptionLabel = "B", SalePrice = 1100m, Currency = "USD",
                },
                new()
                {
                    Status = "Solicitado", HotelName = "Hotel Premium", City = "Bariloche", StarRating = 5,
                    OptionGroup = "hoteles", OptionLabel = "C", SalePrice = 1600m, Currency = "USD",
                },
            },
        };
    }

    private static Reserva BuildSampleReserva()
    {
        return new Reserva
        {
            Id = 1,
            StartDate = new DateTime(2027, 2, 10),
            EndDate = new DateTime(2027, 2, 15),
            AdultCount = 2,
            ChildCount = 0,
            InfantCount = 0,
            // FORMAS DE PAGO con 2 párrafos (decisión #2 firmada): texto propio de ESTA reserva.
            BudgetPaymentTermsText =
                "Seña del 30% al momento de la confirmación, por transferencia bancaria o tarjeta de crédito." +
                "\n\nSaldo del 70% restante hasta 30 días antes de la fecha de salida. Consultar por planes en cuotas.",
            FlightSegments = new List<FlightSegment>
            {
                // Tramo 1 (ida): EZE -> PUJ, directo, con equipaje estructurado (alimenta la línea EQUIPAJE:).
                new()
                {
                    Status = "NN",
                    Origin = "EZE",
                    OriginCity = "Buenos Aires",
                    Destination = "PUJ",
                    DestinationCity = "Punta Cana",
                    DepartureTime = new DateTime(2027, 2, 10, 8, 30, 0),
                    ArrivalTime = new DateTime(2027, 2, 10, 12, 45, 0),
                    IsDirect = true,
                    IncludesBackpack = true,
                    IncludesCarryOn = true,
                    IncludesCheckedBag = false,
                },
                // Tramo 2 (vuelta): PUJ -> EZE, directo.
                new()
                {
                    Status = "NN",
                    Origin = "PUJ",
                    OriginCity = "Punta Cana",
                    Destination = "EZE",
                    DestinationCity = "Buenos Aires",
                    DepartureTime = new DateTime(2027, 2, 15, 14, 0, 0),
                    ArrivalTime = new DateTime(2027, 2, 15, 21, 20, 0),
                    IsDirect = true,
                },
            },
            HotelBookings = new List<HotelBooking>
            {
                new()
                {
                    Status = "Solicitado",
                    HotelName = "Iberostar Waves Punta Cana",
                    City = "Punta Cana",
                    StarRating = 4,
                    RoomType = "Doble",
                    RoomCategory = "Estándar",
                    MealPlan = "All Inclusive",
                    Nights = 5,
                    SalePrice = 1450m,
                    Currency = "USD",
                },
                new()
                {
                    Status = "Solicitado",
                    HotelName = "Dreams Dominicus La Romana",
                    City = "La Romana",
                    StarRating = 4,
                    RoomType = "Doble",
                    RoomCategory = "Deluxe",
                    MealPlan = "All Inclusive",
                    Nights = 5,
                    SalePrice = 1780m,
                    Currency = "USD",
                },
            },
            TransferBookings = new List<TransferBooking>
            {
                new()
                {
                    Status = "Solicitado",
                    ProductName = "Traslado privado aeropuerto - hotel",
                    SalePrice = 45m,
                    Currency = "USD",
                },
            },
        };
    }

    private static AgencySettings BuildSampleAgencySettings()
    {
        return new AgencySettings
        {
            AgencyName = "Magna Viajes y Turismo",
            AgencyLicenseNumber = "EVT 12345",
            Address = "Av. Libertador 1000, CABA",
            Phone = "011 4444-5555",
            // Banda: se deja null para verificar el DEFAULT de la maqueta (#0e3a4f) — el bug reportado
            // por el dueño era el default del SELECTOR del front (#1d4ed8), no este fallback del backend.
            PdfBandColorHex = null,
        };
    }

    private static IReadOnlyList<BudgetConditionBlock> BuildSampleConditions()
    {
        return new List<BudgetConditionBlock>
        {
            new()
            {
                Kind = BudgetConditionBlockKind.Flights,
                Text = "Tarifas aéreas sujetas a disponibilidad y cambio sin previo aviso hasta la emisión del ticket.",
            },
            new()
            {
                Kind = BudgetConditionBlockKind.Hotels,
                Text = "Check-in 15hs, check-out 11hs. Habitaciones sujetas a disponibilidad al momento de la confirmación.",
            },
            new()
            {
                Kind = BudgetConditionBlockKind.General,
                Text = "Presupuesto válido por 7 días. Los precios pueden variar por disponibilidad y tipo de cambio.",
            },
        };
    }
}
