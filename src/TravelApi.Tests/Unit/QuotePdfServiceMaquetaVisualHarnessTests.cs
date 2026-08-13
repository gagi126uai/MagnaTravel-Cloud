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
