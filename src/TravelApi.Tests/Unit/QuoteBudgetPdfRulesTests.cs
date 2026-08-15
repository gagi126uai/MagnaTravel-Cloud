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
    // tiene moneda cargada. REHECHO a la maqueta v2 (2026-08-13): el monto va PRIMERO y la moneda
    // DESPUÉS ("1.450 USD", no "USD 1.450,00"), y los centavos ",00" se ocultan cuando el monto es
    // redondo (un centavo REAL, en cambio, nunca se esconde).
    // ================================================================================

    [Fact]
    public void BuildAmountLabel_WithCurrency_AmountFirstThenCurrency()
    {
        Assert.Equal("1.000 USD", QuoteBudgetPdfRules.BuildAmountLabel(1000m, "USD"));
    }

    [Fact]
    public void BuildAmountLabel_WithoutCurrency_PrintsAmountOnly_NeverInventsArs()
    {
        var label = QuoteBudgetPdfRules.BuildAmountLabel(1000m, null);

        Assert.Equal("1.000", label);
        Assert.DoesNotContain("ARS", label);
    }

    [Fact]
    public void BuildAmountLabel_RoundAmount_OmitsZeroCents()
    {
        Assert.Equal("1.450 USD", QuoteBudgetPdfRules.BuildAmountLabel(1450.00m, "USD"));
    }

    [Fact]
    public void BuildAmountLabel_WithRealCents_KeepsThem_NeverHidesRealMoney()
    {
        Assert.Equal("1.450,50 USD", QuoteBudgetPdfRules.BuildAmountLabel(1450.50m, "USD"));
    }

    [Fact]
    public void BuildAmountLabel_ThousandsUseDotSeparator()
    {
        Assert.Equal("12.345 ARS", QuoteBudgetPdfRules.BuildAmountLabel(12345m, "ARS"));
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

    // ================================================================================
    // Vuelos REHECHOS a la decisión firmada del dueño (2026-08-13, "PDF completo"): DOS FILAS por vuelo
    // (ida y vuelta), horarios TimeOnly? por tramo. Espejo: cada elemento se omite solo, nunca se inventa
    // un aeropuerto/hora/duración que no se cargó.
    // ================================================================================

    [Fact]
    public void BuildFlightAirportLabel_BothLoaded_JoinsCodeAndCity_Uppercase()
    {
        Assert.Equal("EZE · BUENOS AIRES", QuoteBudgetPdfRules.BuildFlightAirportLabel("eze", "Buenos Aires"));
    }

    [Fact]
    public void BuildFlightAirportLabel_OnlyOneLoaded_ShowsThatOne()
    {
        Assert.Equal("EZE", QuoteBudgetPdfRules.BuildFlightAirportLabel("eze", null));
        Assert.Equal("BUENOS AIRES", QuoteBudgetPdfRules.BuildFlightAirportLabel(null, "Buenos Aires"));
    }

    [Fact]
    public void BuildFlightAirportLabel_NeitherLoaded_ReturnsNull()
    {
        Assert.Null(QuoteBudgetPdfRules.BuildFlightAirportLabel(null, "   "));
    }

    [Fact]
    public void HasReturnLeg_NoReturnDateLoaded_ReturnsFalse()
    {
        var flight = new FlightSegment { DepartureTime = new DateTime(2027, 2, 10), ArrivalTime = null };
        Assert.False(QuoteBudgetPdfRules.HasReturnLeg(flight));
    }

    [Fact]
    public void HasReturnLeg_ReturnDateLoaded_ReturnsTrue()
    {
        // ArrivalTime, por el reparto firmado en la entidad, guarda la FECHA de vuelta (no una hora).
        var flight = new FlightSegment { DepartureTime = new DateTime(2027, 2, 10), ArrivalTime = new DateTime(2027, 2, 15) };
        Assert.True(QuoteBudgetPdfRules.HasReturnLeg(flight));
    }

    [Fact]
    public void BuildFlightLegDepartureText_StructuredTimeLoaded_ReturnsHour()
    {
        var fallbackDate = new DateTime(2027, 2, 10);
        Assert.Equal("08:30", QuoteBudgetPdfRules.BuildFlightLegDepartureText(new TimeOnly(8, 30), fallbackDate));
    }

    [Fact]
    public void BuildFlightLegDepartureText_NoStructuredTime_FallsBackToShortDate_NeverInvents0000()
    {
        // El vendedor cargó la FECHA del vuelo (ficha "producto-primero") pero todavía no anotó la hora
        // real de este tramo -- se imprime la fecha en el lugar de la hora, nunca "00:00" inventado.
        var fallbackDate = new DateTime(2027, 2, 10);
        Assert.Equal("10/02/2027", QuoteBudgetPdfRules.BuildFlightLegDepartureText(null, fallbackDate));
    }

    [Fact]
    public void BuildFlightLegArrivalText_StructuredTimeLoaded_ReturnsHour()
    {
        Assert.Equal("11:45", QuoteBudgetPdfRules.BuildFlightLegArrivalText(new TimeOnly(11, 45)));
    }

    [Fact]
    public void BuildFlightLegArrivalText_NoStructuredTime_ReturnsNull_NoDateFallbackOnArrivalSide()
    {
        // A diferencia de la salida, la llegada NO tiene fallback de fecha (repetir la misma fecha del
        // tramo del lado de la llegada no aporta nada) -- un tramo con SOLO hora de salida es válido.
        Assert.Null(QuoteBudgetPdfRules.BuildFlightLegArrivalText(null));
    }

    [Fact]
    public void IsFlightLegNextDay_ArrivalEarlierThanDeparture_ReturnsTrue_CrossesMidnight()
    {
        // Sale 23:15, llega 01:40 -- solo puede haber llegado al dia siguiente.
        Assert.True(QuoteBudgetPdfRules.IsFlightLegNextDay(new TimeOnly(23, 15), new TimeOnly(1, 40)));
    }

    [Fact]
    public void IsFlightLegNextDay_ArrivalLaterThanDeparture_ReturnsFalse_SameDay()
    {
        Assert.False(QuoteBudgetPdfRules.IsFlightLegNextDay(new TimeOnly(8, 30), new TimeOnly(12, 45)));
    }

    [Fact]
    public void IsFlightLegNextDay_MissingEitherTime_ReturnsFalse_NeverInventsTheBadge()
    {
        Assert.False(QuoteBudgetPdfRules.IsFlightLegNextDay(null, new TimeOnly(12, 45)));
        Assert.False(QuoteBudgetPdfRules.IsFlightLegNextDay(new TimeOnly(8, 30), null));
        Assert.False(QuoteBudgetPdfRules.IsFlightLegNextDay(null, null));
    }

    [Fact]
    public void BuildFlightLegDuration_SameDay_FormatsHoursAndMinutes()
    {
        Assert.Equal("4h 15m", QuoteBudgetPdfRules.BuildFlightLegDuration(new TimeOnly(8, 30), new TimeOnly(12, 45)));
    }

    [Fact]
    public void BuildFlightLegDuration_ExactHours_OmitsZeroMinutes()
    {
        Assert.Equal("3h", QuoteBudgetPdfRules.BuildFlightLegDuration(new TimeOnly(8, 0), new TimeOnly(11, 0)));
    }

    [Fact]
    public void BuildFlightLegDuration_CrossesMidnight_Adds24Hours_NeverNegative()
    {
        // Sale 23:15, llega 01:40 -- la resta directa daria negativo; sumando 24h da 2h 25m reales.
        Assert.Equal("2h 25m", QuoteBudgetPdfRules.BuildFlightLegDuration(new TimeOnly(23, 15), new TimeOnly(1, 40)));
    }

    [Fact]
    public void BuildFlightLegDuration_MissingEitherTime_ReturnsNull_NeverInventsDuration()
    {
        Assert.Null(QuoteBudgetPdfRules.BuildFlightLegDuration(null, new TimeOnly(12, 45)));
        Assert.Null(QuoteBudgetPdfRules.BuildFlightLegDuration(new TimeOnly(8, 30), null));
    }

    // ================================================================================
    // Hotel: línea de cuotas (decisión firmada del dueño, 2026-08-13, "PDF completo").
    // ================================================================================

    [Fact]
    public void BuildInstallmentsLine_BothFieldsLoaded_FormatsLikeTheMockup()
    {
        Assert.Equal("6 CUOTAS 280 USD", QuoteBudgetPdfRules.BuildInstallmentsLine(6, 280m, "USD"));
    }

    [Fact]
    public void BuildInstallmentsLine_MissingCount_ReturnsNull_NeverGuessesTheOther()
    {
        Assert.Null(QuoteBudgetPdfRules.BuildInstallmentsLine(null, 280m, "USD"));
    }

    [Fact]
    public void BuildInstallmentsLine_MissingAmount_ReturnsNull_NeverGuessesTheOther()
    {
        Assert.Null(QuoteBudgetPdfRules.BuildInstallmentsLine(6, null, "USD"));
    }

    [Fact]
    public void BuildInstallmentsLine_ZeroOrNegativeCount_ReturnsNull()
    {
        Assert.Null(QuoteBudgetPdfRules.BuildInstallmentsLine(0, 280m, "USD"));
        Assert.Null(QuoteBudgetPdfRules.BuildInstallmentsLine(-1, 280m, "USD"));
    }

    // ================================================================================
    // Servicio "Otro" (ServicioReserva, decisión firmada del dueño, 2026-08-13, "PDF completo").
    // ================================================================================

    [Fact]
    public void BuildOtroServiceDisplayName_DescriptionLoaded_ReturnsItTrimmed()
    {
        var servicio = new ServicioReserva { Description = "  Alquiler de auto 7 días  " };
        Assert.Equal("Alquiler de auto 7 días", QuoteBudgetPdfRules.BuildOtroServiceDisplayName(servicio));
    }

    [Fact]
    public void BuildOtroServiceDisplayName_NoDescription_FallsBackToOtro()
    {
        var servicio = new ServicioReserva { Description = null };
        Assert.Equal("Otro", QuoteBudgetPdfRules.BuildOtroServiceDisplayName(servicio));
    }

    // ================================================================================
    // Ronda 2 (decisión firmada del dueño, 2026-08-14, spec §6): chip de escala por tramo -- pisa al
    // "Directo", nunca conviven. Fix post-inspección visual (2026-08-15): el chip NUNCA lleva el lugar
    // (se partía en dos renglones dentro de la píldora de ancho fijo) -- el lugar vive en el renglón de
    // detalle debajo de las filas (ver BuildFlightStopDetailLines).
    // ================================================================================

    [Fact]
    public void ResolveFlightLegChipText_OneStop_ShowsCountOnly_NeverThePlace()
    {
        Assert.Equal("1 escala", QuoteBudgetPdfRules.ResolveFlightLegChipText(isDirect: true, stopsCount: 1));
    }

    [Fact]
    public void ResolveFlightLegChipText_TwoOrMoreStops_ShowsCountOnly()
    {
        Assert.Equal("2 escalas", QuoteBudgetPdfRules.ResolveFlightLegChipText(isDirect: false, stopsCount: 2));
    }

    [Fact]
    public void ResolveFlightLegChipText_StopsOverridesDirect_ChipsNeverCoexist()
    {
        // El dato de escalas PISA al de "Directo" cargado -- son mutuamente excluyentes en el chip.
        Assert.Equal("1 escala", QuoteBudgetPdfRules.ResolveFlightLegChipText(isDirect: true, stopsCount: 1));
    }

    [Fact]
    public void ResolveFlightLegChipText_NoStops_FallsBackToDirectoAsBefore()
    {
        Assert.Equal("Directo", QuoteBudgetPdfRules.ResolveFlightLegChipText(isDirect: true, stopsCount: null));
        Assert.Equal("Directo", QuoteBudgetPdfRules.ResolveFlightLegChipText(isDirect: true, stopsCount: 0));
    }

    [Fact]
    public void ResolveFlightLegChipText_NoStopsAndNotDirect_ReturnsNull_NoChipAtAll()
    {
        Assert.Null(QuoteBudgetPdfRules.ResolveFlightLegChipText(isDirect: null, stopsCount: null));
        Assert.Null(QuoteBudgetPdfRules.ResolveFlightLegChipText(isDirect: false, stopsCount: 0));
    }

    // ================================================================================
    // Ronda 2: renglón de detalle de escala por tramo, con/sin lugar, con/sin espera, ida+vuelta.
    // ================================================================================

    [Fact]
    public void BuildFlightStopDetailLines_OnlyOutboundStop_WithPlaceAndWait_NoPrefix()
    {
        var flight = new FlightSegment
        {
            OutboundStopsCount = 1, OutboundStopPlace = "Lima (LIM)", OutboundStopWait = "2h 10m",
        };

        var lines = QuoteBudgetPdfRules.BuildFlightStopDetailLines(flight);

        Assert.Single(lines);
        Assert.Equal("Escala en Lima (LIM) · espera 2h 10m", lines[0]);
    }

    [Fact]
    public void BuildFlightStopDetailLines_OnlyPlace_NoWaitLoaded_OmitsWaitPart()
    {
        var flight = new FlightSegment { OutboundStopsCount = 1, OutboundStopPlace = "Lima (LIM)", OutboundStopWait = null };

        var lines = QuoteBudgetPdfRules.BuildFlightStopDetailLines(flight);

        Assert.Single(lines);
        Assert.Equal("Escala en Lima (LIM)", lines[0]);
    }

    [Fact]
    public void BuildFlightStopDetailLines_OnlyWait_NoPlaceLoaded_OmitsPlacePart()
    {
        var flight = new FlightSegment { OutboundStopsCount = 1, OutboundStopPlace = null, OutboundStopWait = "2h 10m" };

        var lines = QuoteBudgetPdfRules.BuildFlightStopDetailLines(flight);

        Assert.Single(lines);
        Assert.Equal("espera 2h 10m", lines[0]);
    }

    [Fact]
    public void BuildFlightStopDetailLines_StopsCountOnly_NoPlaceNoWait_OmitsLineEntirely()
    {
        // El chip ya avisó "N escalas"; sin lugar NI espera no hay nada más que agregar en el detalle.
        var flight = new FlightSegment { OutboundStopsCount = 1, OutboundStopPlace = null, OutboundStopWait = null };

        Assert.Empty(QuoteBudgetPdfRules.BuildFlightStopDetailLines(flight));
    }

    [Fact]
    public void BuildFlightStopDetailLines_BothLegsHaveStops_PrefixesIdaVuelta()
    {
        var flight = new FlightSegment
        {
            OutboundStopsCount = 1, OutboundStopPlace = "Lima (LIM)",
            ReturnStopsCount = 1, ReturnStopPlace = "Panamá (PTY)",
        };

        var lines = QuoteBudgetPdfRules.BuildFlightStopDetailLines(flight);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Ida: Escala en Lima (LIM)", lines[0]);
        Assert.Equal("Vuelta: Escala en Panamá (PTY)", lines[1]);
    }

    [Fact]
    public void BuildFlightStopDetailLines_NoStopsAtAll_ReturnsEmptyList()
    {
        var flight = new FlightSegment();
        Assert.Empty(QuoteBudgetPdfRules.BuildFlightStopDetailLines(flight));
    }

    // ================================================================================
    // Ronda 2: edad de pasajero contra la fecha de salida (o hoy si no hay fecha de salida).
    // ================================================================================

    [Fact]
    public void ResolvePassengerAgeReferenceDate_UsesTripStartDate_WhenLoaded()
    {
        var tripStart = new DateTime(2027, 4, 10);
        Assert.Equal(tripStart, QuoteBudgetPdfRules.ResolvePassengerAgeReferenceDate(tripStart));
    }

    [Fact]
    public void ResolvePassengerAgeReferenceDate_FallsBackToToday_WhenTripStartMissing()
    {
        Assert.Equal(DateTime.UtcNow.Date, QuoteBudgetPdfRules.ResolvePassengerAgeReferenceDate(null));
    }

    [Fact]
    public void ComputePassengerAge_BirthdayAlreadyPassedThisYear()
    {
        var reference = new DateTime(2027, 4, 10);
        var birth = new DateTime(2015, 1, 1); // cumplió años en enero, la referencia es en abril.
        Assert.Equal(12, QuoteBudgetPdfRules.ComputePassengerAge(birth, reference));
    }

    [Fact]
    public void ComputePassengerAge_BirthdayNotYetReachedThisYear_SubtractsOne()
    {
        var reference = new DateTime(2027, 4, 10);
        var birth = new DateTime(2015, 12, 25); // el cumpleaños de este año todavía no llegó.
        Assert.Equal(11, QuoteBudgetPdfRules.ComputePassengerAge(birth, reference));
    }

    [Fact]
    public void ComputePassengerAge_NoBirthDate_ReturnsNull()
    {
        Assert.Null(QuoteBudgetPdfRules.ComputePassengerAge(null, DateTime.UtcNow));
    }

    [Fact]
    public void BuildPassengerDisplayLine_Minor_AppendsAge()
    {
        var reference = new DateTime(2027, 4, 10);
        var birth = new DateTime(2015, 1, 1); // 12 años a la fecha de referencia.
        Assert.Equal("Sofía Pérez · 12 años", QuoteBudgetPdfRules.BuildPassengerDisplayLine("Sofía Pérez", birth, reference));
    }

    [Fact]
    public void BuildPassengerDisplayLine_Adult_ShowsNameOnly_NoAgeSuffix()
    {
        var reference = new DateTime(2027, 4, 10);
        var birth = new DateTime(1990, 1, 1); // adulto.
        Assert.Equal("Juan Pérez", QuoteBudgetPdfRules.BuildPassengerDisplayLine("Juan Pérez", birth, reference));
    }

    [Fact]
    public void BuildPassengerDisplayLine_NoBirthDateLoaded_ShowsNameOnly()
    {
        var reference = new DateTime(2027, 4, 10);
        Assert.Equal("Juan Pérez", QuoteBudgetPdfRules.BuildPassengerDisplayLine("Juan Pérez", null, reference));
    }

    // ================================================================================
    // Ronda 2: cabecera "Preparado para {cliente}" + etiqueta de tipo por ítem en OTROS.
    // ================================================================================

    [Fact]
    public void BuildPreparedForLine_PayerLoaded_ReturnsLine()
    {
        Assert.Equal("Preparado para Juan Pérez", QuoteBudgetPdfRules.BuildPreparedForLine("Juan Pérez"));
    }

    [Fact]
    public void BuildPreparedForLine_NoPayer_ReturnsNull()
    {
        Assert.Null(QuoteBudgetPdfRules.BuildPreparedForLine(null));
        Assert.Null(QuoteBudgetPdfRules.BuildPreparedForLine("   "));
    }

    [Theory]
    [InlineData(ServiceTypes.Insurance, "ASISTENCIA AL VIAJERO")]
    [InlineData(ServiceTypes.Package, "PAQUETE")]
    [InlineData(ServiceTypes.Excursion, "EXCURSIÓN")]
    [InlineData(ServiceTypes.Transfer, "TRASLADO")]
    [InlineData(ServiceTypes.Hotel, "HOTEL")]
    [InlineData(ServiceTypes.Flight, "AÉREO")]
    [InlineData(ServiceTypes.Other, "SERVICIO")]
    [InlineData(null, "SERVICIO")]
    [InlineData("un-tipo-que-no-existe", "SERVICIO")]
    public void ResolveGenericServiceTypeLabel_MapsBusinessVocabulary_NeverAClassName(string? serviceType, string expectedLabel)
    {
        Assert.Equal(expectedLabel, QuoteBudgetPdfRules.ResolveGenericServiceTypeLabel(serviceType));
    }
}
