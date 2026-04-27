using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit;

public class VoucherDomainPolicyTests
{
    [Fact]
    public void Reservation_IsEconomicallySettled_WhenBalanceIsZeroOrNegative()
    {
        Assert.True(ReservationEconomicPolicy.IsEconomicallySettled(new Reserva { Balance = 0m }));
        Assert.True(ReservationEconomicPolicy.IsEconomicallySettled(new Reserva { Balance = -1m }));
    }

    [Fact]
    public void Reservation_HasOutstandingBalance_WhenBalanceIsPositive()
    {
        var reserva = new Reserva { Balance = 0.01m };

        Assert.True(ReservationEconomicPolicy.HasOutstandingBalance(reserva));
    }

    [Fact]
    public void Voucher_CanBeSent_OnlyWhenIssuedOrExternalAndEnabled()
    {
        Assert.False(new Voucher { Status = VoucherStatuses.Draft, IsEnabledForSending = false }.CanBeSent());
        Assert.True(new Voucher { Status = VoucherStatuses.Issued, IsEnabledForSending = true }.CanBeSent());
        Assert.True(new Voucher { Status = VoucherStatuses.UploadedExternal, IsEnabledForSending = true }.CanBeSent());
    }
}
