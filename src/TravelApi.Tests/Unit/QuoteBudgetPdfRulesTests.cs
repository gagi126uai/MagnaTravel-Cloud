using System;
using System.Collections.Generic;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "PDF de presupuesto" (maqueta v2 firmada, 2026-08-11/12), TANDA 3: tests puros de
/// <see cref="QuoteBudgetPdfRules"/> — la lógica de "qué se dibuja / qué se omite / con qué número" del
/// PDF, separada del renderer QuestPDF para poder testearla sin generar un solo byte de PDF.
/// </summary>
public class QuoteBudgetPdfRulesTests
{
    // ================================================================================
    // ResolvePaymentTermsText: texto propio gana, después la plantilla, después nada.
    // ================================================================================

    [Fact]
    public void ResolvePaymentTermsText_ReservaTextWins_OverTemplate()
    {
        var resolved = QuoteBudgetPdfRules.ResolvePaymentTermsText("3 cuotas sin interés", "Plantilla general");
        Assert.Equal("3 cuotas sin interés", resolved);
    }

    [Fact]
    public void ResolvePaymentTermsText_FallsBackToTemplate_WhenReservaTextIsEmpty()
    {
        Assert.Equal("Plantilla general", QuoteBudgetPdfRules.ResolvePaymentTermsText(null, "Plantilla general"));
        Assert.Equal("Plantilla general", QuoteBudgetPdfRules.ResolvePaymentTermsText("   ", "Plantilla general"));
    }

    [Fact]
    public void ResolvePaymentTermsText_NullWhenNeitherIsLoaded()
    {
        Assert.Null(QuoteBudgetPdfRules.ResolvePaymentTermsText(null, null));
        Assert.Null(QuoteBudgetPdfRules.ResolvePaymentTermsText("", "   "));
    }

    // ================================================================================
    // ResolveDisplayPrice: por persona (con redondeo) vs total, y el caso 0 pasajeros.
    // ================================================================================

    [Fact]
    public void ResolveDisplayPrice_PorPersona_DividesByLoadedPassengers()
    {
        var display = QuoteBudgetPdfRules.ResolveDisplayPrice(salePrice: 1000m, cantidadPasajerosCargados: 4, porPersona: true);

        Assert.True(display.IsPerPerson);
        Assert.Equal(250m, display.Amount);
    }

    [Fact]
    public void ResolveDisplayPrice_PorPersona_RoundsCommercially()
    {
        // 1000 / 3 = 333.333... -> redondeo comercial a 2 decimales, AwayFromZero.
        var display = QuoteBudgetPdfRules.ResolveDisplayPrice(1000m, cantidadPasajerosCargados: 3, porPersona: true);

        Assert.True(display.IsPerPerson);
        Assert.Equal(333.33m, display.Amount);
    }

    [Fact]
    public void ResolveDisplayPrice_ZeroPassengersLoaded_FallsBackToTotal()
    {
        // Nunca se divide por cero: con 0 pasajeros cargados, aunque el vendedor pidio "por persona",
        // se cae a TOTAL (y el monto es el precio de venta entero, sin dividir).
        var display = QuoteBudgetPdfRules.ResolveDisplayPrice(1500m, cantidadPasajerosCargados: 0, porPersona: true);

        Assert.False(display.IsPerPerson);
        Assert.Equal(1500m, display.Amount);
    }

    [Fact]
    public void ResolveDisplayPrice_Total_NeverDivides_EvenWithPassengersLoaded()
    {
        var display = QuoteBudgetPdfRules.ResolveDisplayPrice(1500m, cantidadPasajerosCargados: 3, porPersona: false);

        Assert.False(display.IsPerPerson);
        Assert.Equal(1500m, display.Amount);
    }

    // ================================================================================
    // BuildAmountLabel: fix post-review (2026-08-12) — nunca inventar "ARS" cuando el servicio no
    // tiene moneda cargada.
    // ================================================================================

    [Fact]
    public void BuildAmountLabel_WithCurrency_PrefixesIt()
    {
        Assert.Equal("USD 1.000,00", QuoteBudgetPdfRules.BuildAmountLabel(1000m, "USD"));
    }

    [Fact]
    public void BuildAmountLabel_WithoutCurrency_PrintsAmountOnly_NeverInventsArs()
    {
        var label = QuoteBudgetPdfRules.BuildAmountLabel(1000m, null);

        Assert.Equal("1.000,00", label);
        Assert.DoesNotContain("ARS", label);
    }

    // ================================================================================
    // Espejo: línea EQUIPAJE ausente sin dato, presente con dato (flags o texto libre).
    // ================================================================================

    [Fact]
    public void BuildEquipajeLine_NullFlight_ReturnsNull()
    {
        Assert.Null(QuoteBudgetPdfRules.BuildEquipajeLine(null));
    }

    [Fact]
    public void BuildEquipajeLine_NoFlagsAndNoBaggageText_ReturnsNull()
    {
        var flight = new FlightSegment { Status = "NN" };
        Assert.Null(QuoteBudgetPdfRules.BuildEquipajeLine(flight));
    }

    [Fact]
    public void BuildEquipajeLine_UsesBaggageFreeText_WhenNoStructuredFlagsLoaded()
    {
        var flight = new FlightSegment { Status = "NN", Baggage = "23kg" };
        Assert.Equal("23kg", QuoteBudgetPdfRules.BuildEquipajeLine(flight));
    }

    [Fact]
    public void BuildEquipajeLine_BuildsHumanPhrase_FromStructuredFlags()
    {
        var flight = new FlightSegment
        {
            Status = "NN",
            IncludesBackpack = true,
            IncludesCarryOn = true,
            IncludesCheckedBag = false,
        };

        var line = QuoteBudgetPdfRules.BuildEquipajeLine(flight);

        Assert.Contains("mochila", line);
        Assert.Contains("equipaje de mano", line);
        Assert.DoesNotContain("valija despachada", line);
    }

    [Fact]
    public void BuildEquipajeLine_AllFlagsFalse_StillPrintsInformativeLine_NotOmitted()
    {
        // Los 3 flags SI estan cargados (en false): es un dato real que el vendedor informo, no
        // "sin informar" — la linea se imprime igual (espejo de lo cargado, no de lo "positivo").
        var flight = new FlightSegment
        {
            Status = "NN",
            IncludesBackpack = false,
            IncludesCarryOn = false,
            IncludesCheckedBag = false,
        };

        var line = QuoteBudgetPdfRules.BuildEquipajeLine(flight);

        Assert.NotNull(line);
    }

    // ================================================================================
    // Espejo: SALIDA / TRASLADO / destino.
    // ================================================================================

    [Fact]
    public void BuildSalidaLine_NullWithoutBothDates()
    {
        Assert.Null(QuoteBudgetPdfRules.BuildSalidaLine(null, new DateTime(2027, 2, 15), hotelNights: 5));
        Assert.Null(QuoteBudgetPdfRules.BuildSalidaLine(new DateTime(2027, 2, 10), null, hotelNights: 5));
    }

    [Fact]
    public void BuildSalidaLine_UsesHotelNights_WhenProvided()
    {
        var line = QuoteBudgetPdfRules.BuildSalidaLine(new DateTime(2027, 2, 10), new DateTime(2027, 2, 15), hotelNights: 5);
        Assert.Equal("10/02/2027 al 15/02/2027 – 5 noches.", line);
    }

    [Fact]
    public void BuildTrasladoLine_NullWithoutData()
    {
        Assert.Null(QuoteBudgetPdfRules.BuildTrasladoLine(null));
        Assert.Null(QuoteBudgetPdfRules.BuildTrasladoLine(new TransferBooking { Status = "NN" }));
    }

    [Fact]
    public void BuildTrasladoLine_PrefersProductName_OverPickupDropoff()
    {
        var transfer = new TransferBooking
        {
            Status = "NN",
            ProductName = "Traslado privado EZE-Hotel",
            PickupLocation = "EZE",
            DropoffLocation = "Hotel",
        };

        Assert.Equal("Traslado privado EZE-Hotel", QuoteBudgetPdfRules.BuildTrasladoLine(transfer));
    }

    [Fact]
    public void ResolveDestinationTitle_NullWhenNoHotelOrPackage()
    {
        Assert.Null(QuoteBudgetPdfRules.ResolveDestinationTitle(new List<HotelBooking>(), new List<PackageBooking>()));
    }

    [Fact]
    public void ResolveDestinationTitle_UsesFirstLiveHotelCity_BeforePackage()
    {
        var hotels = new List<HotelBooking>
        {
            new() { Status = "Cancelado", City = "Bariloche" },
            new() { Status = "Solicitado", City = "Ushuaia" },
        };
        var packages = new List<PackageBooking> { new() { Status = "Solicitado", Destination = "Cancún" } };

        Assert.Equal("Ushuaia", QuoteBudgetPdfRules.ResolveDestinationTitle(hotels, packages));
    }

    // ================================================================================
    // Opciones A/B/C: grupos ambiguos listados con sus candidatos, excluidos del total general
    // (contrastado contra ReservaMoneyCalculator, la misma fuente).
    // ================================================================================

    [Fact]
    public void BuildAmbiguousOptionGroups_TwoHotelsSameGroup_ListsBothAsCandidates()
    {
        var reserva = new Reserva
        {
            Id = 1,
            HotelBookings = new List<HotelBooking>
            {
                new() { Status = "Solicitado", HotelName = "Hotel A", OptionGroup = "hoteles", OptionLabel = "A", SalePrice = 1000m, Currency = "USD" },
                new() { Status = "Solicitado", HotelName = "Hotel B", OptionGroup = "Hoteles", OptionLabel = "B", SalePrice = 1200m, Currency = "USD" },
            },
        };

        var groups = QuoteBudgetPdfRules.BuildAmbiguousOptionGroups(reserva);

        Assert.True(groups.ContainsKey("hoteles"));
        Assert.Equal(2, groups["hoteles"].Count);
    }

    [Fact]
    public void BuildAmbiguousOptionGroups_ResolvedGroup_DoesNotAppear()
    {
        // Un solo hotel vivo en el grupo (el otro se cancelo al resolver) ya NO es ambiguo: no debe
        // listarse como "opción" — es un servicio normal.
        var reserva = new Reserva
        {
            Id = 1,
            HotelBookings = new List<HotelBooking>
            {
                new() { Status = "Confirmado", HotelName = "Hotel A", OptionGroup = "hoteles", SalePrice = 1000m },
                new() { Status = "Cancelado", HotelName = "Hotel B", OptionGroup = "hoteles", SalePrice = 1200m },
            },
        };

        var groups = QuoteBudgetPdfRules.BuildAmbiguousOptionGroups(reserva);

        Assert.Empty(groups);
    }

    [Fact]
    public void BuildAmbiguousOptionGroups_ExcludedFromReservaMoneyCalculatorTotal_SameSourceOfTruth()
    {
        // Mismo escenario que arriba visto desde el calculo de plata: los 2 hoteles ambiguos NO suman
        // al TotalSale (fuente unica compartida entre PDF y el motor de plata).
        var reserva = new Reserva
        {
            Id = 1,
            HotelBookings = new List<HotelBooking>
            {
                new() { Status = "Solicitado", HotelName = "Hotel A", OptionGroup = "hoteles", SalePrice = 1000m },
                new() { Status = "Solicitado", HotelName = "Hotel B", OptionGroup = "hoteles", SalePrice = 1200m },
            },
        };

        var summary = ReservaMoneyCalculator.Calculate(reserva);
        var groups = QuoteBudgetPdfRules.BuildAmbiguousOptionGroups(reserva);

        Assert.Equal(0m, summary.TotalSale);
        Assert.True(groups.ContainsKey("hoteles"));
    }

    // ================================================================================
    // Página 2 "INFORMACIÓN IMPORTANTE": solo las categorías presentes en la reserva + Generales;
    // vacía cuando no hay nada cargado (la página entera se omite).
    // ================================================================================

    [Fact]
    public void SelectRelevantConditions_EmptyWhenNoConditionsLoaded()
    {
        var reserva = new Reserva { Id = 1 };
        var result = QuoteBudgetPdfRules.SelectRelevantConditions(reserva, new List<BudgetConditionBlock>());

        Assert.Empty(result);
    }

    [Fact]
    public void SelectRelevantConditions_OnlyIncludesKindsPresentInReserva_PlusGeneral()
    {
        var reserva = new Reserva
        {
            Id = 1,
            HotelBookings = new List<HotelBooking> { new() { Status = "Solicitado", HotelName = "Hotel A" } },
        };
        var conditions = new List<BudgetConditionBlock>
        {
            new() { Kind = BudgetConditionBlockKind.Hotels, Text = "Condiciones de hotel" },
            new() { Kind = BudgetConditionBlockKind.Flights, Text = "Condiciones de vuelo" }, // no hay vuelos en la reserva
            new() { Kind = BudgetConditionBlockKind.General, Text = "Condiciones generales" },
        };

        var result = QuoteBudgetPdfRules.SelectRelevantConditions(reserva, conditions);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, c => c.Kind == BudgetConditionBlockKind.Hotels);
        Assert.Contains(result, c => c.Kind == BudgetConditionBlockKind.General);
        Assert.DoesNotContain(result, c => c.Kind == BudgetConditionBlockKind.Flights);
    }
}
