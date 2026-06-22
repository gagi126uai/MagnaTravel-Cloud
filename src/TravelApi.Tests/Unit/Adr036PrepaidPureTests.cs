using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-036 (2026-06-21, "prepago puro"): cobertura PURA de dominio de las reglas nuevas:
///   - ToSettle desaparecio de la matriz de transiciones (Forward/Revert).
///   - El candado de pago del cliente para "En viaje" (IsClientFullyPaid) es incondicional.
///   - "En viaje" (Traveling) es solo lectura / no cobrable / no facturable (via la politica de capacidades).
/// </summary>
public class Adr036PrepaidPureTests
{
    // ===================== Matriz de transiciones: ToSettle murio =====================

    [Fact]
    public void Transitions_NoStateHasToSettleAsTarget()
    {
        // Ningun estado puede ir a "ToSettle" (forward ni revert): el estado se elimino.
        foreach (var targets in ReservaStatusTransitions.Forward.Values)
            Assert.DoesNotContain("ToSettle", targets);
        foreach (var targets in ReservaStatusTransitions.Revert.Values)
            Assert.DoesNotContain("ToSettle", targets);
    }

    [Fact]
    public void Transitions_NoToSettleSourceRow()
    {
        Assert.False(ReservaStatusTransitions.Forward.ContainsKey("ToSettle"));
        Assert.False(ReservaStatusTransitions.Revert.ContainsKey("ToSettle"));
    }

    [Fact]
    public void Forward_Traveling_OnlyClosed()
    {
        Assert.True(ReservaStatusTransitions.Forward.TryGetValue(EstadoReserva.Traveling, out var targets));
        Assert.Equal(new[] { EstadoReserva.Closed }, targets);
    }

    [Fact]
    public void Revert_Closed_OnlyTraveling()
    {
        Assert.True(ReservaStatusTransitions.Revert.TryGetValue(EstadoReserva.Closed, out var targets));
        Assert.Equal(new[] { EstadoReserva.Traveling }, targets);
    }

    // ===================== Candado de pago del cliente para "En viaje" =====================

    [Theory]
    [InlineData(0)]      // saldado exacto
    [InlineData(-100)]   // saldo a favor
    [InlineData(-0.004)] // resto de centavo negativo (redondea a 0)
    public void IsClientFullyPaid_True_WhenBalanceLeqZeroWithRounding(double balance)
        => Assert.True(ReservationEconomicPolicy.IsClientFullyPaid((decimal)balance));

    [Theory]
    [InlineData(0.01)]   // un centavo de deuda
    [InlineData(500)]
    public void IsClientFullyPaid_False_WhenBalancePositive(double balance)
        => Assert.False(ReservationEconomicPolicy.IsClientFullyPaid((decimal)balance));

    // ===================== "En viaje" via la politica de capacidades =====================

    private static ReservaCapabilityContext Traveling(decimal balance)
        => new(EstadoReserva.Traveling, balance, false, false, false, false);

    [Fact]
    public void Traveling_IsReadOnly_CannotEditServicesPassengersData()
    {
        var caps = ReservaCapabilityPolicy.For(Traveling(0m));
        Assert.False(caps.CanEditServices.Allowed);
        Assert.False(caps.CanEditPassengers.Allowed);
        Assert.False(caps.CanEditReservaData.Allowed);
    }

    [Fact]
    public void Traveling_CannotRegisterPaymentNorInvoice()
    {
        var caps = ReservaCapabilityPolicy.For(Traveling(100m));
        Assert.False(caps.CanRegisterPayment.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.TravelingNotChargeableReason, caps.CanRegisterPayment.Reason);
        Assert.False(caps.CanInvoiceSale.Allowed);
    }

    [Fact]
    public void Traveling_VoucherStillAllowed()
    {
        // El voucher se necesita para viajar: sigue habilitado en viaje.
        var caps = ReservaCapabilityPolicy.For(Traveling(0m));
        Assert.True(caps.CanEmitVoucher.Allowed);
    }
}
