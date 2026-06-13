using System;
using System.Linq;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Hallazgo auditoria ERP #9 (2026-06-13): tests del armador de items de factura sugeridos desde los
/// servicios CONFIRMADOS de una reserva. Pruebas PURAS (sin base de datos): arman una <see cref="Reserva"/>
/// en memoria y verifican que cada servicio confirmado produce una linea con su venta, que los no
/// confirmados/cancelados no entran y que la multimoneda se separa por moneda.
/// </summary>
public class InvoiceSuggestedItemsBuilderTests
{
    // ===================== Una linea por servicio confirmado, con su venta =====================

    [Fact]
    public void Build_ConfirmedHotel_ProducesOneLineWithSalePrice()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking
        {
            Status = "Confirmado",
            HotelName = "Sheraton",
            Nights = 3,
            SalePrice = 200m
        });

        var groups = InvoiceSuggestedItemsBuilder.Build(reserva);

        var group = Assert.Single(groups);
        Assert.Equal(Monedas.ARS, group.Currency);
        var item = Assert.Single(group.Items);
        Assert.Equal(200m, item.Total);
        Assert.Equal(200m, item.UnitPrice);
        Assert.Equal(1m, item.Quantity);
        Assert.Equal(InvoiceSuggestedItemsBuilder.DefaultAlicuotaIvaId, item.AlicuotaIvaId);
        Assert.Contains("Sheraton", item.Description);
        Assert.Contains("3 noches", item.Description);
        Assert.Equal(200m, group.SuggestedTotal);
    }

    [Fact]
    public void Build_ConfirmedFlightWithTicket_ProducesLine()
    {
        var reserva = new Reserva();
        // El aereo resuelve recien con el ticket emitido (no alcanza el PNR confirmado).
        reserva.FlightSegments.Add(new FlightSegment
        {
            Status = "HK",
            TicketIssuedAt = DateTime.UtcNow,
            OriginCity = "Miami",
            DestinationCity = "Buenos Aires",
            SalePrice = 500m
        });

        var groups = InvoiceSuggestedItemsBuilder.Build(reserva);

        var item = Assert.Single(Assert.Single(groups).Items);
        Assert.Equal(500m, item.Total);
        Assert.Contains("Miami", item.Description);
        Assert.Contains("Buenos Aires", item.Description);
    }

    [Fact]
    public void Build_MultipleConfirmedServices_OneLineEach()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", HotelName = "A", SalePrice = 100m });
        reserva.TransferBookings.Add(new TransferBooking { Status = "Confirmado", PickupLocation = "EZE", DropoffLocation = "Centro", SalePrice = 50m });
        reserva.AssistanceBookings.Add(new AssistanceBooking { Status = "Confirmado", PlanType = "Premium", SalePrice = 30m });

        var groups = InvoiceSuggestedItemsBuilder.Build(reserva);

        var group = Assert.Single(groups);
        Assert.Equal(3, group.Items.Count);
        Assert.Equal(180m, group.SuggestedTotal);
    }

    // ===================== Servicio no confirmado / cancelado NO entra =====================

    [Fact]
    public void Build_RequestedHotel_DoesNotEnter()
    {
        var reserva = new Reserva();
        // "Solicitado" no mapea a Confirmado -> no resuelto -> no entra.
        reserva.HotelBookings.Add(new HotelBooking { Status = "Solicitado", HotelName = "A", SalePrice = 100m });

        var groups = InvoiceSuggestedItemsBuilder.Build(reserva);

        Assert.Empty(groups);
    }

    [Fact]
    public void Build_CancelledHotel_DoesNotEnter()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Cancelado", HotelName = "A", SalePrice = 100m });

        var groups = InvoiceSuggestedItemsBuilder.Build(reserva);

        Assert.Empty(groups);
    }

    [Fact]
    public void Build_CancelledFlightWithTicket_DoesNotEnter()
    {
        var reserva = new Reserva();
        // Un vuelo emitido y DESPUES cancelado NO resuelve (ADR-020): no debe sugerirse para facturar.
        reserva.FlightSegments.Add(new FlightSegment
        {
            Status = "UN", // cancelado
            TicketIssuedAt = DateTime.UtcNow,
            SalePrice = 500m
        });

        var groups = InvoiceSuggestedItemsBuilder.Build(reserva);

        Assert.Empty(groups);
    }

    [Fact]
    public void Build_FlightWithoutTicket_DoesNotEnter()
    {
        var reserva = new Reserva();
        // PNR confirmado pero sin ticket -> confirmado pero NO resuelto -> no se factura todavia.
        reserva.FlightSegments.Add(new FlightSegment { Status = "HK", TicketIssuedAt = null, SalePrice = 500m });

        var groups = InvoiceSuggestedItemsBuilder.Build(reserva);

        Assert.Empty(groups);
    }

    [Fact]
    public void Build_MixOfConfirmedAndNot_OnlyConfirmedEnter()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", HotelName = "Confirmado", SalePrice = 100m });
        reserva.HotelBookings.Add(new HotelBooking { Status = "Solicitado", HotelName = "Pendiente", SalePrice = 999m });
        reserva.HotelBookings.Add(new HotelBooking { Status = "Cancelado", HotelName = "Cancelado", SalePrice = 999m });

        var group = Assert.Single(InvoiceSuggestedItemsBuilder.Build(reserva));

        var item = Assert.Single(group.Items);
        Assert.Equal(100m, item.Total);
    }

    // ===================== Multimoneda: sugerencias separadas por moneda =====================

    [Fact]
    public void Build_MultiCurrency_SeparatesGroupsPerCurrency()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", HotelName = "Pesos", Currency = "ARS", SalePrice = 100m });
        reserva.FlightSegments.Add(new FlightSegment { Status = "HK", TicketIssuedAt = DateTime.UtcNow, Currency = "USD", SalePrice = 500m });

        var groups = InvoiceSuggestedItemsBuilder.Build(reserva);

        Assert.Equal(2, groups.Count);

        var ars = groups.Single(g => g.Currency == Monedas.ARS);
        Assert.Equal(100m, ars.SuggestedTotal);
        Assert.Single(ars.Items);

        var usd = groups.Single(g => g.Currency == Monedas.USD);
        Assert.Equal(500m, usd.SuggestedTotal);
        Assert.Single(usd.Items);
    }

    [Fact]
    public void Build_NullCurrency_FallsBackToArs()
    {
        var reserva = new Reserva();
        // Servicio legacy sin moneda -> se lee como ARS.
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", HotelName = "Legacy", Currency = null, SalePrice = 100m });

        var group = Assert.Single(InvoiceSuggestedItemsBuilder.Build(reserva));
        Assert.Equal(Monedas.ARS, group.Currency);
    }

    [Fact]
    public void Build_NoServices_ReturnsEmpty()
    {
        var groups = InvoiceSuggestedItemsBuilder.Build(new Reserva());
        Assert.Empty(groups);
    }

    // ===================== Descripcion: cae a etiqueta generica si faltan datos =====================

    [Fact]
    public void Build_GenericServiceWithDescription_UsesIt()
    {
        var reserva = new Reserva();
        reserva.Servicios.Add(new ServicioReserva { Status = "Confirmado", Description = "Excursion city tour", SalePrice = 40m });

        var item = Assert.Single(Assert.Single(InvoiceSuggestedItemsBuilder.Build(reserva)).Items);
        Assert.Equal("Excursion city tour", item.Description);
    }

    [Fact]
    public void Build_HotelWithoutName_StillHasNonEmptyDescription()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", HotelName = "", City = "", SalePrice = 100m });

        var item = Assert.Single(Assert.Single(InvoiceSuggestedItemsBuilder.Build(reserva)).Items);
        // ARCA exige descripcion no vacia por item: nunca debe salir en blanco.
        Assert.False(string.IsNullOrWhiteSpace(item.Description));
    }
}
