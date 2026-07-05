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

    // ===================== Tanda 6: diagnostico de servicios excluidos (bug "$0 mudo") =====================

    [Fact]
    public void BuildWithDiagnostics_RequestedHotel_ReportedAsNotResolved()
    {
        var reserva = new Reserva();
        // "Solicitado" no mapea a Confirmado -> vivo pero no resuelto -> NoResuelto (causa del "$0 mudo").
        reserva.HotelBookings.Add(new HotelBooking { Status = "Solicitado", HotelName = "Sheraton", SalePrice = 100m });

        var result = InvoiceSuggestedItemsBuilder.BuildWithDiagnostics(reserva);

        Assert.Empty(result.Groups);
        var excluded = Assert.Single(result.ExcludedServices);
        Assert.Equal(SuggestedServiceExclusionReasons.NotResolved, excluded.Reason);
        Assert.Contains("Sheraton", excluded.Description);
        Assert.Equal(Monedas.ARS, excluded.Currency);
    }

    [Fact]
    public void BuildWithDiagnostics_CancelledHotel_ReportedAsCancelled()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Cancelado", HotelName = "Sheraton", SalePrice = 100m });

        var result = InvoiceSuggestedItemsBuilder.BuildWithDiagnostics(reserva);

        Assert.Empty(result.Groups);
        var excluded = Assert.Single(result.ExcludedServices);
        // Cancelado gana sobre no-resuelto: un cancelado tambien es "no resuelto", pero reportamos Cancelado.
        Assert.Equal(SuggestedServiceExclusionReasons.Cancelled, excluded.Reason);
    }

    [Fact]
    public void BuildWithDiagnostics_ResolvedZeroPrice_ReportedAsZeroPriceAndStaysInGroup()
    {
        var reserva = new Reserva();
        // Resuelto (Confirmado) pero con venta 0: SIGUE entrando al grupo como linea $0 (no cambiamos la
        // inclusion) y ADEMAS se marca PrecioCero para que el modal explique ese $0.
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", HotelName = "Cortesia", SalePrice = 0m });

        var result = InvoiceSuggestedItemsBuilder.BuildWithDiagnostics(reserva);

        var group = Assert.Single(result.Groups);
        var line = Assert.Single(group.Items);
        Assert.Equal(0m, line.Total);

        var excluded = Assert.Single(result.ExcludedServices);
        Assert.Equal(SuggestedServiceExclusionReasons.ZeroPrice, excluded.Reason);
        Assert.Contains("Cortesia", excluded.Description);
    }

    [Fact]
    public void BuildWithDiagnostics_ResolvedNormalService_NotExcludedAndStaysInGroup()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", HotelName = "Sheraton", SalePrice = 200m });

        var result = InvoiceSuggestedItemsBuilder.BuildWithDiagnostics(reserva);

        // Un servicio resuelto y con venta > 0 NO aparece en excluidos y SI en los grupos.
        Assert.Empty(result.ExcludedServices);
        var group = Assert.Single(result.Groups);
        Assert.Equal(200m, group.SuggestedTotal);
    }

    [Fact]
    public void BuildWithDiagnostics_GroupsAreIdenticalToBuild()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", HotelName = "Confirmado", Currency = "ARS", SalePrice = 100m });
        reserva.HotelBookings.Add(new HotelBooking { Status = "Solicitado", HotelName = "Pendiente", SalePrice = 999m });
        reserva.HotelBookings.Add(new HotelBooking { Status = "Cancelado", HotelName = "Cancelado", SalePrice = 999m });
        reserva.FlightSegments.Add(new FlightSegment { Status = "HK", TicketIssuedAt = DateTime.UtcNow, Currency = "USD", SalePrice = 500m });

        var build = InvoiceSuggestedItemsBuilder.Build(reserva);
        var diagnostics = InvoiceSuggestedItemsBuilder.BuildWithDiagnostics(reserva);

        // Equivalencia: los grupos de BuildWithDiagnostics son exactamente los de Build (mismas monedas,
        // mismos totales), porque Build delega en BuildWithDiagnostics.
        Assert.Equal(build.Count, diagnostics.Groups.Count);
        foreach (var expected in build)
        {
            var actual = diagnostics.Groups.Single(g => g.Currency == expected.Currency);
            Assert.Equal(expected.SuggestedTotal, actual.SuggestedTotal);
            Assert.Equal(expected.Items.Count, actual.Items.Count);
        }

        // Y el diagnostico ademas nombra los dos que no entraron: el Solicitado y el Cancelado.
        Assert.Equal(2, diagnostics.ExcludedServices.Count);
        Assert.Contains(diagnostics.ExcludedServices, e => e.Reason == SuggestedServiceExclusionReasons.NotResolved);
        Assert.Contains(diagnostics.ExcludedServices, e => e.Reason == SuggestedServiceExclusionReasons.Cancelled);
    }
}
