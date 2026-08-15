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

    /// <summary>
    /// Maqueta "minimalista elegante" (spec 2026-08-14): mismo dataset de arriba, pero con un color de
    /// ACENTO fijo (#0e7c86, categoría "caribe" del set curado de <c>DestinationPaletteService</c>) para
    /// inspección visual de la paleta por destino sin depender de que haya IA configurada en la máquina
    /// que corre el test. El PDF queda en disco para revisar a ojo (Read tool).
    /// </summary>
    [Fact]
    public void GenerateQuotePdf_MaquetaSampleData_FixedCaribeAccent_SavedForVisualInspection()
    {
        var reserva = BuildSampleReserva();
        var agencySettings = BuildSampleAgencySettings();
        var conditions = BuildSampleConditions();

        var service = new QuotePdfService();

        var pdfBytes = service.GenerateQuotePdf(
            reserva: reserva,
            agencySettings: agencySettings,
            conditions: conditions,
            logoBytes: null,
            porPersona: true,
            cantidadPasajerosCargados: reserva.AdultCount + reserva.ChildCount + reserva.InfantCount,
            accentColorHex: "#0e7c86");

        Assert.NotEmpty(pdfBytes);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 5));

        var path = Path.Combine(Path.GetTempPath(), "pdf-maqueta-check-caribe.pdf");
        File.WriteAllBytes(path, pdfBytes);
    }

    // ================================================================================
    // Casos borde del render (no cubiertos por la muestra "espejo perfecto" de arriba): la lógica de
    // CADA elemento omitido ya está probada como función pura en QuoteBudgetPdfRulesTests — esto
    // verifica que el RENDERER (QuestPDF, Svg/Row anidados) no explote con combinaciones degeneradas
    // reales: tramo solo con hora de salida (sin llegada ni aeropuertos), tramo que cruza medianoche
    // (badge "+1"), hotel sin estrellas cargadas, reserva sin ningún servicio.
    // ================================================================================

    [Fact]
    public void GenerateQuotePdf_MinimalFlightRow_OnlyDepartureDateLoaded_DoesNotThrow()
    {
        var reserva = new Reserva
        {
            Id = 2,
            FlightSegments = new List<FlightSegment>
            {
                // Único dato garantizado: DepartureTime (fecha de ida). Sin ArrivalTime -> vuelo de ida
                // sola, no hay fila de vuelta. Sin OutboundDepartureTime/OutboundArrivalTime -> la fila
                // IDA cae al fallback de fecha corta del lado de salida, nada inventado.
                new() { Status = "NN", DepartureTime = new DateTime(2027, 3, 1) },
            },
        };

        var pdfBytes = new QuotePdfService().GenerateQuotePdf(
            reserva, BuildSampleAgencySettings(), Array.Empty<BudgetConditionBlock>(), logoBytes: null,
            porPersona: false, cantidadPasajerosCargados: 0);

        Assert.NotEmpty(pdfBytes);
    }

    [Fact]
    public void GenerateQuotePdf_FlightLegCrossingMidnight_ShowsPlusOneBadge_DoesNotThrow()
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
                    DepartureTime = new DateTime(2027, 3, 1), // fecha de ida (ventana del viaje, NO horario).
                    OutboundDepartureTime = new TimeOnly(23, 30),
                    OutboundArrivalTime = new TimeOnly(13, 10), // menor que la salida -> "+1" (cruza medianoche).
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
        // Corrección ronda 3 (2026-08-13): el vuelo de este escenario (fecha cargada, hora NO cargada)
        // ahora DEBE aparecer en el PDF con la fecha en el lugar de la hora -- ya no desaparece la fila
        // entera (eso fue lo que el dueño rechazó de la ronda anterior).
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
    /// asistencia, genérico "Otro") + formas de pago + condiciones de página 2.
    ///
    /// <para>Obra "PDF completo" (2026-08-13): el vuelo trae los 4 horarios por tramo COMPLETOS —
    /// <c>DepartureTime</c>/<c>ArrivalTime</c> son solo la VENTANA del viaje (fecha de ida/vuelta, igual
    /// que el resto de la reserva); los horarios de verdad viven en Outbound*/Return*. La ida cruza
    /// medianoche DE VERDAD (23:15 -&gt; 01:40) para verificar que el "+1" sigue funcionando con el
    /// cálculo nuevo (comparación directa de <c>TimeOnly</c>, sin la complejidad de fechas de la ronda
    /// anterior). El hotel suma el plan de cuotas ("6 CUOTAS 300 USD").</para>
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
            // Ronda 2 (2026-08-14): "Preparado para {cliente}" en la cabecera -- solo hace falta el
            // nombre para esta inspección visual, el resto de Customer no importa acá.
            Payer = new Customer { FullName = "Familia Rodríguez" },
            FlightSegments = new List<FlightSegment>
            {
                new()
                {
                    Status = "NN",
                    Origin = "EZE",
                    OriginCity = "Buenos Aires",
                    Destination = "MAD",
                    DestinationCity = "Madrid",
                    DepartureTime = new DateTime(2027, 4, 10), // fecha de ida -- ventana del viaje, NO horario.
                    ArrivalTime = new DateTime(2027, 4, 17), // fecha de vuelta -- habilita la fila VUELTA.
                    OutboundDepartureTime = new TimeOnly(23, 15),
                    OutboundArrivalTime = new TimeOnly(1, 40), // menor que la salida -> "+1" real (cruza medianoche).
                    ReturnDepartureTime = new TimeOnly(15, 0),
                    ReturnArrivalTime = new TimeOnly(19, 20),
                    IsDirect = true,
                    IncludesBackpack = true,
                    IncludesCarryOn = true,
                    IncludesCheckedBag = true,
                    // Ronda 2 (2026-08-14, spec §6): la VUELTA hace 1 escala en Lima -- el chip pasa de
                    // "Directo" a "1 escala · Lima (LIM)" en la fila de vuelta (la ida sigue directa).
                    ReturnStopsCount = 1,
                    ReturnStopPlace = "Lima (LIM)",
                    ReturnStopWait = "2h 10m",
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
                    InstallmentsCount = 6,
                    InstallmentAmount = 300m,
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
                    // Ronda 2 (2026-08-14): cuotas generalizadas a cualquier servicio (antes solo hotel).
                    InstallmentsCount = 3,
                    InstallmentAmount = 30m,
                },
            },
            // Servicio "genérico" (ServicioReserva, ServiceType=Otro): obra "PDF completo" (2026-08-13) le
            // agrega bloque propio en QuotePdfService (mismo molde que traslado/asistencia).
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
            // Ronda 2 (2026-08-14, spec §6): sección PASAJEROS -- 3 pasajeros, una menor (agrega "· N
            // años" apagado contra la fecha de SALIDA del viaje, 10/04/2027).
            Passengers = new List<Passenger>
            {
                new() { FullName = "Juan Rodríguez", BirthDate = new DateTime(1985, 6, 20) },
                new() { FullName = "María Gómez de Rodríguez", BirthDate = new DateTime(1988, 3, 15) },
                new() { FullName = "Sofía Rodríguez", BirthDate = new DateTime(2018, 1, 1) }, // 9 años a la fecha de salida.
            },
            // Ronda 2 (2026-08-14, spec §6): PLAN DE PAGOS del total, 3 filas ordenadas.
            PaymentPlanInstallments = new List<BudgetPaymentPlanInstallment>
            {
                new() { Position = 1, DueText = "Al confirmar la reserva", Amount = 1000m, Currency = "USD" },
                new() { Position = 2, DueText = "10 de enero de 2027", Amount = 1000m, Currency = "USD" },
                new() { Position = 3, DueText = "Saldo 30 días antes de la salida", Amount = 780m, Currency = "USD" },
            },
        };
    }

    /// <summary>
    /// Escenario (b) del barrido: vuelo con FECHA de ida/vuelta cargada (ventana del viaje) pero SIN
    /// ningún horario de tramo cargado (reproduce el caso real de PROD: el vendedor cargó la fecha, no
    /// la hora) + hotel sin estrellas ni detalle de habitación + servicio sin moneda cargada.
    ///
    /// <para>Obra "PDF completo" (2026-08-13): con el modelo nuevo esto ya no depende de una heurística
    /// ("¿las dos puntas caen justo en medianoche?") — simplemente no hay <c>TimeOnly</c> cargado en
    /// ningún campo Outbound*/Return*, así que las DOS filas (ida y vuelta) caen al fallback de fecha
    /// corta ("01/05/2027" / "02/05/2027"), sin "+1" ni duración inventados (esos solo salen cuando HAY
    /// dos horas reales cargadas). El resto del escenario sigue igual: nada de "ARS" inventada, nada de
    /// "Habitación: Doble" cuando nadie cargó el detalle.</para>
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
                    DepartureTime = new DateTime(2027, 5, 1), // fecha de ida -- ventana del viaje, NO horario.
                    ArrivalTime = new DateTime(2027, 5, 2), // fecha de vuelta -- habilita la fila VUELTA, sin horarios de tramo cargados.
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
            // Obra "PDF completo" (2026-08-13): UN vuelo (ida y vuelta como una sola línea de producto,
            // ADR-018 "producto-primero") imprime DOS FILAS en el PDF — ver ComposeVuelos. DepartureTime/
            // ArrivalTime son la VENTANA del viaje (fecha de ida/vuelta, igual que las de la reserva); los
            // horarios de verdad (los del papel) van por Outbound*/Return*.
            FlightSegments = new List<FlightSegment>
            {
                new()
                {
                    Status = "NN",
                    Origin = "EZE",
                    OriginCity = "Buenos Aires",
                    Destination = "PUJ",
                    DestinationCity = "Punta Cana",
                    DepartureTime = new DateTime(2027, 2, 10), // fecha de ida.
                    ArrivalTime = new DateTime(2027, 2, 15), // fecha de vuelta -- habilita la fila VUELTA.
                    OutboundDepartureTime = new TimeOnly(8, 30),
                    OutboundArrivalTime = new TimeOnly(12, 45),
                    ReturnDepartureTime = new TimeOnly(14, 0),
                    ReturnArrivalTime = new TimeOnly(21, 20),
                    IsDirect = true,
                    // Equipaje estructurado (alimenta la línea EQUIPAJE: de arriba, y los 3 íconos de la fila).
                    IncludesBackpack = true,
                    IncludesCarryOn = true,
                    IncludesCheckedBag = false,
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
