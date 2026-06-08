using System;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-020: tabla de verdad de ServiceResolutionRules. Confirma la separacion confirmacion vs
/// resolucion, sobre todo en el aereo (un PNR HK confirma pero NO resuelve sin ticket).
/// </summary>
public class ServiceResolutionRulesTests
{
    // ===================== Aereo: confirmacion != resolucion (B4) =====================

    [Fact]
    public void Flight_HK_WithoutTicket_IsConfirmedButNotResolved()
    {
        var flight = new FlightSegment { Status = "HK", TicketIssuedAt = null };

        Assert.True(ServiceResolutionRules.IsOperatorConfirmed(flight));
        Assert.False(ServiceResolutionRules.IsResolved(flight));
        Assert.False(ServiceResolutionRules.IsCancelled(flight));
    }

    [Fact]
    public void Flight_HK_WithTicket_IsResolved()
    {
        var flight = new FlightSegment { Status = "HK", TicketIssuedAt = DateTime.UtcNow };

        Assert.True(ServiceResolutionRules.IsOperatorConfirmed(flight));
        Assert.True(ServiceResolutionRules.IsResolved(flight));
    }

    [Fact]
    public void Flight_NN_Default_IsNotConfirmedNotResolved()
    {
        // El default nuevo del aereo (B4): "NN" mapea a Solicitado.
        var flight = new FlightSegment { Status = "NN" };

        Assert.False(ServiceResolutionRules.IsOperatorConfirmed(flight));
        Assert.False(ServiceResolutionRules.IsResolved(flight));
    }

    [Theory]
    [InlineData("UN")]
    [InlineData("HX")]
    public void Flight_CancelCodes_AreCancelled(string status)
    {
        var flight = new FlightSegment { Status = status };
        Assert.True(ServiceResolutionRules.IsCancelled(flight));
        Assert.False(ServiceResolutionRules.IsResolved(flight));
    }

    [Fact]
    public void Flight_UnknownCode_TreatedAsRequested()
    {
        var flight = new FlightSegment { Status = "ZZ", TicketIssuedAt = null };
        Assert.False(ServiceResolutionRules.IsOperatorConfirmed(flight));
        Assert.False(ServiceResolutionRules.IsResolved(flight));
    }

    [Theory]
    [InlineData("UN")]
    [InlineData("UC")]
    [InlineData("HX")]
    [InlineData("NO")]
    public void Flight_IssuedThenCancelled_IsNotResolved(string cancelStatus)
    {
        // BLOQUEANTE 1 (plata): un vuelo emitido (TicketIssuedAt != null) y DESPUES cancelado
        // por el operador NO resuelve -> su SalePrice no debe seguir sumando a ConfirmedSale.
        var flight = new FlightSegment { Status = cancelStatus, TicketIssuedAt = DateTime.UtcNow };

        Assert.True(ServiceResolutionRules.IsCancelled(flight));
        Assert.False(ServiceResolutionRules.IsResolved(flight));
    }

    [Fact]
    public void Transfer_NoConfirmationRequired_ButCancelled_IsNotResolved()
    {
        // Mismo criterio que el aereo: un traslado cancelado NO resuelve aunque tenga la marca
        // "no requiere confirmacion" puesta.
        var transfer = new TransferBooking { Status = "Cancelado", NoConfirmationRequired = true };

        Assert.True(ServiceResolutionRules.IsCancelled(transfer));
        Assert.False(ServiceResolutionRules.IsResolved(transfer));
    }

    // ===================== Genericos: confirmacion == resolucion =====================

    [Theory]
    [InlineData("Confirmado", true)]
    [InlineData("confirmado", true)]   // case-insensitive
    [InlineData("Emitido", true)]      // emit* tambien cuenta como confirmado
    [InlineData("Solicitado", false)]
    [InlineData("Borrador", false)]
    [InlineData("Cancelado", false)]
    public void Hotel_ResolutionFollowsGenericStatus(string status, bool resolved)
    {
        var hotel = new HotelBooking { Status = status };
        Assert.Equal(resolved, ServiceResolutionRules.IsResolved(hotel));
        Assert.Equal(resolved, ServiceResolutionRules.IsOperatorConfirmed(hotel));
    }

    [Fact]
    public void Hotel_Cancelado_IsCancelled()
    {
        var hotel = new HotelBooking { Status = "Cancelado" };
        Assert.True(ServiceResolutionRules.IsCancelled(hotel));
        Assert.False(ServiceResolutionRules.IsResolved(hotel));
    }

    // ===================== Traslado: marca "no requiere confirmacion" resuelve =====================

    [Fact]
    public void Transfer_Solicitado_NotResolved()
    {
        var transfer = new TransferBooking { Status = "Solicitado", NoConfirmationRequired = false };
        Assert.False(ServiceResolutionRules.IsResolved(transfer));
    }

    [Fact]
    public void Transfer_NoConfirmationRequired_IsResolvedEvenIfNotConfirmedByOperator()
    {
        var transfer = new TransferBooking { Status = "Solicitado", NoConfirmationRequired = true };
        // No esta confirmado por el operador, pero SI esta resuelto (marca manual).
        Assert.False(ServiceResolutionRules.IsOperatorConfirmed(transfer));
        Assert.True(ServiceResolutionRules.IsResolved(transfer));
    }

    [Fact]
    public void Transfer_Confirmado_IsResolved()
    {
        var transfer = new TransferBooking { Status = "Confirmado", NoConfirmationRequired = false };
        Assert.True(ServiceResolutionRules.IsOperatorConfirmed(transfer));
        Assert.True(ServiceResolutionRules.IsResolved(transfer));
    }
}
