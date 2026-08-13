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
    // Vuelos REHECHOS a la maqueta v2 (2026-08-13): fila por tramo con
    // BuildFlightAirportLabel/BuildFlightDuration/IsNextDayArrival. Espejo: cada elemento se omite
    // solo, nunca se inventa un aeropuerto/duración que no se cargó.
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
    public void BuildFlightDuration_NoArrivalLoaded_ReturnsNull_NeverInventsDuration()
    {
        // BUG 2 (2026-06-08): tramos solo de ida existen de verdad, no es un dato faltante por error.
        Assert.Null(QuoteBudgetPdfRules.BuildFlightDuration(new DateTime(2027, 2, 10, 8, 30, 0), null));
    }

    [Fact]
    public void BuildFlightDuration_SameDay_FormatsHoursAndMinutes()
    {
        var departure = new DateTime(2027, 2, 10, 8, 30, 0);
        var arrival = new DateTime(2027, 2, 10, 12, 45, 0);

        Assert.Equal("4h 15m", QuoteBudgetPdfRules.BuildFlightDuration(departure, arrival));
    }

    [Fact]
    public void BuildFlightDuration_ExactHours_OmitsZeroMinutes()
    {
        var departure = new DateTime(2027, 2, 10, 8, 0, 0);
        var arrival = new DateTime(2027, 2, 10, 11, 0, 0);

        Assert.Equal("3h", QuoteBudgetPdfRules.BuildFlightDuration(departure, arrival));
    }

    [Fact]
    public void BuildFlightDuration_ArrivalBeforeDeparture_ReturnsNull_NeverNegative()
    {
        var departure = new DateTime(2027, 2, 10, 22, 0, 0);
        var arrival = new DateTime(2027, 2, 10, 8, 0, 0);

        Assert.Null(QuoteBudgetPdfRules.BuildFlightDuration(departure, arrival));
    }

    [Fact]
    public void IsNextDayArrival_ArrivalOnLaterCalendarDate_ReturnsTrue()
    {
        var departure = new DateTime(2027, 2, 10, 23, 30, 0);
        var arrival = new DateTime(2027, 2, 11, 6, 15, 0);

        Assert.True(QuoteBudgetPdfRules.IsNextDayArrival(departure, arrival));
    }

    [Fact]
    public void IsNextDayArrival_SameCalendarDate_ReturnsFalse()
    {
        var departure = new DateTime(2027, 2, 10, 8, 30, 0);
        var arrival = new DateTime(2027, 2, 10, 12, 45, 0);

        Assert.False(QuoteBudgetPdfRules.IsNextDayArrival(departure, arrival));
    }

    [Fact]
    public void IsNextDayArrival_NoArrivalLoaded_ReturnsFalse()
    {
        Assert.False(QuoteBudgetPdfRules.IsNextDayArrival(new DateTime(2027, 2, 10, 8, 30, 0), null));
    }

    // ================================================================================
    // Fix ronda 2 (2026-08-13): vuelo sin datos reales mostraba "00:00 [Directo] 00:00 +1 · 24h" en PROD.
    // IsSameInstantArrival / LooksLikeMissingSchedule / ShouldShowDepartureTime / ShouldShowArrivalTime /
    // HasAnyVisibleFlightRowData son las reglas espejo que evitan inventar horas, "+1" y duración.
    // ================================================================================

    [Fact]
    public void IsSameInstantArrival_SameDateTime_ReturnsTrue()
    {
        var moment = new DateTime(2027, 3, 1, 9, 0, 0);
        Assert.True(QuoteBudgetPdfRules.IsSameInstantArrival(moment, moment));
    }

    [Fact]
    public void IsSameInstantArrival_DifferentTime_ReturnsFalse()
    {
        var departure = new DateTime(2027, 3, 1, 9, 0, 0);
        var arrival = new DateTime(2027, 3, 1, 9, 0, 1);
        Assert.False(QuoteBudgetPdfRules.IsSameInstantArrival(departure, arrival));
    }

    [Fact]
    public void LooksLikeMissingSchedule_BothExactMidnight_ReturnsTrue_EvenOnDifferentCalendarDates()
    {
        // Caso EXACTO del bug reportado: salida 00:00 del dia 1, "llegada" 00:00 del dia 2 (formulario
        // completo con el default) -> las fechas SI son distintas, pero ninguna hora es real.
        var departure = new DateTime(2027, 3, 1, 0, 0, 0);
        var arrival = new DateTime(2027, 3, 2, 0, 0, 0);

        Assert.True(QuoteBudgetPdfRules.LooksLikeMissingSchedule(departure, arrival));
    }

    [Fact]
    public void LooksLikeMissingSchedule_OnlyOneMidnight_ReturnsFalse_RealSingleMidnightIsRespected()
    {
        // Solo la salida es medianoche EXACTA; la llegada tiene una hora real -> es un vuelo real que
        // salio justo a las 00:00, no "sin horario cargado". No se debe suprimir.
        var departure = new DateTime(2027, 3, 1, 0, 0, 0);
        var arrival = new DateTime(2027, 3, 1, 6, 15, 0);

        Assert.False(QuoteBudgetPdfRules.LooksLikeMissingSchedule(departure, arrival));
    }

    [Fact]
    public void LooksLikeMissingSchedule_NeitherMidnight_ReturnsFalse()
    {
        var departure = new DateTime(2027, 3, 1, 8, 30, 0);
        var arrival = new DateTime(2027, 3, 1, 12, 45, 0);

        Assert.False(QuoteBudgetPdfRules.LooksLikeMissingSchedule(departure, arrival));
    }

    // ================================================================================
    // Corrección ronda 3 (2026-08-13, pedido directo del dueño): un tramo CARGADO se dibuja SIEMPRE — la
    // ronda anterior escondía el tramo entero cuando parecía "sin horario cargado" y el dueño lo
    // rechazó. BuildFlightDepartureTimeText/BuildFlightArrivalTimeText reemplazan a los booleanos
    // ShouldShowDepartureTime/ShouldShowArrivalTime: ahora en vez de "mostrar sí/no", deciden "mostrar la
    // hora o la fecha corta" — el lado de salida ya NUNCA devuelve null.
    // ================================================================================

    [Fact]
    public void BuildFlightDepartureTimeText_RealSchedule_ReturnsHour()
    {
        var departure = new DateTime(2027, 3, 1, 8, 30, 0);
        var arrival = new DateTime(2027, 3, 1, 12, 45, 0);

        Assert.Equal("08:30", QuoteBudgetPdfRules.BuildFlightDepartureTimeText(departure, arrival));
    }

    [Fact]
    public void BuildFlightDepartureTimeText_NoArrivalLoaded_ReturnsHour_DepartureAloneIsAlwaysReal()
    {
        // Sin llegada cargada no hay forma de sospechar "sin horario" -- DepartureTime es obligatorio
        // desde que el tramo existe y se respeta como hora real siempre que no haya otro dato con el que
        // compararlo.
        var departure = new DateTime(2027, 3, 1, 0, 0, 0);

        Assert.Equal("00:00", QuoteBudgetPdfRules.BuildFlightDepartureTimeText(departure, null));
    }

    [Fact]
    public void BuildFlightDepartureTimeText_BothExactMidnight_ReturnsDate_NeverHidesTheSegment()
    {
        // El caso real de negocio: el vendedor cargó la FECHA del vuelo sin la hora. Antes esto hacía
        // desaparecer la fila entera (rechazado por el dueño) -- ahora se imprime la fecha en el lugar de
        // la hora, el tramo sigue apareciendo siempre.
        var departure = new DateTime(2027, 3, 1, 0, 0, 0);
        var arrival = new DateTime(2027, 3, 2, 0, 0, 0);

        Assert.Equal("01/03/2027", QuoteBudgetPdfRules.BuildFlightDepartureTimeText(departure, arrival));
    }

    [Fact]
    public void BuildFlightArrivalTimeText_RealSchedule_ReturnsHour()
    {
        var departure = new DateTime(2027, 3, 1, 8, 30, 0);
        var arrival = new DateTime(2027, 3, 1, 12, 45, 0);

        Assert.Equal("12:45", QuoteBudgetPdfRules.BuildFlightArrivalTimeText(departure, arrival));
    }

    [Fact]
    public void BuildFlightArrivalTimeText_NoArrivalLoaded_ReturnsNull()
    {
        Assert.Null(QuoteBudgetPdfRules.BuildFlightArrivalTimeText(new DateTime(2027, 3, 1, 8, 30, 0), null));
    }

    [Fact]
    public void BuildFlightArrivalTimeText_SameInstant_ReturnsNull()
    {
        var moment = new DateTime(2027, 3, 1, 9, 0, 0);
        Assert.Null(QuoteBudgetPdfRules.BuildFlightArrivalTimeText(moment, moment));
    }

    [Fact]
    public void BuildFlightArrivalTimeText_BothExactMidnight_DifferentDates_ReturnsArrivalDate()
    {
        // El vendedor cargó un RANGO de fechas sin hora (10/02 -> 11/02): el lado de la llegada muestra
        // su propia fecha, distinta de la de salida.
        var departure = new DateTime(2027, 2, 10, 0, 0, 0);
        var arrival = new DateTime(2027, 2, 11, 0, 0, 0);

        Assert.Equal("11/02/2027", QuoteBudgetPdfRules.BuildFlightArrivalTimeText(departure, arrival));
    }

    [Fact]
    public void BuildFlightArrivalTimeText_BothExactMidnight_SameDate_ReturnsNull_DepartureSideAlreadyShowsIt()
    {
        // Mismo día en las dos puntas, ambas a medianoche exacta -- no tiene sentido repetir la misma
        // fecha del lado de la llegada, ya alcanza con la del lado de salida.
        var departure = new DateTime(2027, 3, 1, 0, 0, 0);
        var arrival = new DateTime(2027, 3, 1, 0, 0, 0); // mismo dia Y mismo instante (tambien cubierto por IsSameInstantArrival).

        Assert.Null(QuoteBudgetPdfRules.BuildFlightArrivalTimeText(departure, arrival));
    }

    [Fact]
    public void BuildFlightDuration_SameInstantArrival_ReturnsNull_NeverZeroDuration()
    {
        var moment = new DateTime(2027, 3, 1, 9, 0, 0);
        Assert.Null(QuoteBudgetPdfRules.BuildFlightDuration(moment, moment));
    }

    [Fact]
    public void BuildFlightDuration_BothExactMidnight_ReturnsNull_NeverInvents24h()
    {
        // Exactamente el bug reportado: sin este guard, esto calculaba "24h" (un dia completo de
        // diferencia entre las dos medianoches "de relleno").
        var departure = new DateTime(2027, 3, 1, 0, 0, 0);
        var arrival = new DateTime(2027, 3, 2, 0, 0, 0);

        Assert.Null(QuoteBudgetPdfRules.BuildFlightDuration(departure, arrival));
    }

    [Fact]
    public void IsNextDayArrival_BothExactMidnight_ReturnsFalse_NeverInventsPlusOneBadge()
    {
        // Exactamente el bug reportado: sin este guard, esto marcaba "+1" (la fecha de llegada SI es un
        // dia despues, pero ninguna de las dos horas es un dato real).
        var departure = new DateTime(2027, 3, 1, 0, 0, 0);
        var arrival = new DateTime(2027, 3, 2, 0, 0, 0);

        Assert.False(QuoteBudgetPdfRules.IsNextDayArrival(departure, arrival));
    }

    [Fact]
    public void IsNextDayArrival_SameInstantArrival_ReturnsFalse()
    {
        var moment = new DateTime(2027, 3, 1, 9, 0, 0);
        Assert.False(QuoteBudgetPdfRules.IsNextDayArrival(moment, moment));
    }

}
