using System;
using AutoMapper;
using TravelApi.Application.DTOs;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-017 F1.1 (catalogo find-or-create + fechas limite, 2026-06-05): contrato de los maps de UPDATE
/// para los campos nuevos.
///
/// <para><b>Anti-clobber del deadline (§2.2, R12)</b>: los maps de update hacen <c>Ignore()</c> de los
/// deadlines. En F1.1 el handler todavia NO los asigna (eso es F1.4, gobernado por DeadlinesSpecified),
/// asi que mapear un update — venga o no el deadline, con DeadlinesSpecified true o false — NUNCA pisa
/// el valor persistido. Esto garantiza que agregar los campos no rompe nada: una edicion desde el modal
/// viejo (que no manda el bloque) deja el deadline intacto.</para>
///
/// <para><b>La marca "costo a confirmar" sobrevive a un update normal</b>: el update request no tiene el
/// campo, asi que AutoMapper no lo toca. Solo el boton "Confirmar costo" (F1.3) limpia la marca; un
/// guardado comun no debe borrarla.</para>
/// </summary>
public class CatalogDeadlineMappingTests
{
    private static IMapper CreateMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    private static readonly DateTime PersistedDeadline = new(2026, 7, 10);
    private static readonly DateTime RequestDeadline = new(2026, 8, 20);

    [Fact]
    public void UpdateHotel_MapDoesNotTouchDeadlineOrCostToConfirmMarker()
    {
        var mapper = CreateMapper();
        var entity = new HotelBooking
        {
            HotelName = "Nombre Viejo",
            OperatorPaymentDeadline = PersistedDeadline,
            CostToConfirm = true,
            CostToConfirmReason = "NoKnownCost",
        };

        var request = new UpdateHotelRequest(
            SupplierId: "sup-1", HotelName: "Nombre Nuevo", StarRating: 4, City: "Posadas", Country: "AR",
            CheckIn: new DateTime(2026, 9, 1), CheckOut: new DateTime(2026, 9, 5),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1,
            ConfirmationNumber: null, NetCost: 100m, SalePrice: 200m, Commission: 50m,
            Status: "Confirmado", Notes: null,
            // El request manda un deadline distinto Y DeadlinesSpecified=true: aun asi, en F1.1 el map
            // lo ignora (la asignacion gobernada por el discriminador es F1.4).
            OperatorPaymentDeadline: RequestDeadline,
            DeadlinesSpecified: true);

        mapper.Map(request, entity);

        // El campo mapeado por convencion SI cambia (el map sigue funcionando).
        Assert.Equal("Nombre Nuevo", entity.HotelName);
        // El deadline persistido queda INTACTO (Ignore()).
        Assert.Equal(PersistedDeadline, entity.OperatorPaymentDeadline);
        // La marca "costo a confirmar" sobrevive a un guardado comun.
        Assert.True(entity.CostToConfirm);
        Assert.Equal("NoKnownCost", entity.CostToConfirmReason);
    }

    [Fact]
    public void UpdatePackage_MapDoesNotTouchDeadlineOrCostToConfirmMarker()
    {
        var mapper = CreateMapper();
        var entity = new PackageBooking
        {
            PackageName = "Paquete Viejo",
            OperatorPaymentDeadline = PersistedDeadline,
            CostToConfirm = true,
            CostToConfirmReason = "StaleReference",
        };

        var request = new UpdatePackageRequest(
            SupplierId: "sup-1", PackageName: "Paquete Nuevo", Destination: "Brasil",
            StartDate: new DateTime(2026, 9, 1), EndDate: new DateTime(2026, 9, 7),
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false,
            IncludesExcursions: false, IncludesMeals: false,
            Adults: 2, Children: 1, Itinerary: null, ConfirmationNumber: null,
            NetCost: 300m, SalePrice: 500m, Commission: 200m, Status: "Confirmado", Notes: null,
            OperatorPaymentDeadline: RequestDeadline,
            DeadlinesSpecified: true);

        mapper.Map(request, entity);

        Assert.Equal("Paquete Nuevo", entity.PackageName);
        Assert.Equal(PersistedDeadline, entity.OperatorPaymentDeadline);
        Assert.True(entity.CostToConfirm);
        Assert.Equal("StaleReference", entity.CostToConfirmReason);
    }

    [Fact]
    public void UpdateFlight_MapDoesNotTouchTicketingDeadlineOrCostToConfirmMarker()
    {
        var mapper = CreateMapper();
        var entity = new FlightSegment
        {
            FlightNumber = "100",
            TicketingDeadline = PersistedDeadline,
            CostToConfirm = true,
            CostToConfirmReason = "NoKnownCost",
        };

        var request = new UpdateFlightRequest(
            SupplierId: "sup-1", AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "200",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "MIA", DestinationCity: "Miami",
            DepartureTime: new DateTime(2026, 9, 1, 10, 0, 0), ArrivalTime: new DateTime(2026, 9, 1, 18, 0, 0),
            CabinClass: "Economy", Baggage: "23kg", TicketNumber: null, PNR: "ABC123",
            NetCost: 100m, SalePrice: 200m, Commission: 50m, Tax: 10m, Status: "HK", Notes: null,
            TicketingDeadline: RequestDeadline,
            DeadlinesSpecified: true);

        mapper.Map(request, entity);

        // FlightNumber se mapea por convencion (el map funciona).
        Assert.Equal("200", entity.FlightNumber);
        // El deadline de emision persistido queda INTACTO (Ignore()).
        Assert.Equal(PersistedDeadline, entity.TicketingDeadline);
        Assert.True(entity.CostToConfirm);
        Assert.Equal("NoKnownCost", entity.CostToConfirmReason);
    }

    /// <summary>
    /// Update SIN el bloque de deadline (estilo modal viejo: DeadlinesSpecified=false, deadline null):
    /// el deadline persistido NO cambia. Es el caso de regresion que mas importa — el modal viejo
    /// convive hasta F4 y no manda el campo.
    /// </summary>
    [Fact]
    public void UpdateHotel_LegacyStyleWithoutDeadlineBlock_DoesNotClearPersistedDeadline()
    {
        var mapper = CreateMapper();
        var entity = new HotelBooking { OperatorPaymentDeadline = PersistedDeadline };

        var request = new UpdateHotelRequest(
            SupplierId: "sup-1", HotelName: "Hotel", StarRating: null, City: "Posadas", Country: null,
            CheckIn: new DateTime(2026, 9, 1), CheckOut: new DateTime(2026, 9, 5),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1,
            ConfirmationNumber: null, NetCost: 100m, SalePrice: 200m, Commission: 50m,
            Status: "Confirmado", Notes: null);
        // No se pasan OperatorPaymentDeadline ni DeadlinesSpecified: quedan en null / false (default).
        Assert.Null(request.OperatorPaymentDeadline);
        Assert.False(request.DeadlinesSpecified);

        mapper.Map(request, entity);

        Assert.Equal(PersistedDeadline, entity.OperatorPaymentDeadline);
    }
}
