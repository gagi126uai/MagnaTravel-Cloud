using TravelApi.Models;
using Xunit;

namespace TravelApi.Tests.Unit;

public class ReservationStatusTests
{
    [Fact]
    public void IsValid_ReturnsTrueForDraft()
    {
        Assert.True(ReservationStatuses.IsValid(ReservationStatuses.Draft));
    }

    [Fact]
    public void IsValid_ReturnsFalseForUnknownStatus()
    {
        Assert.False(ReservationStatuses.IsValid("Unknown"));
    }

    [Fact]
    public void CanTransition_AllowsDraftToConfirmed()
    {
        Assert.True(ReservationStatuses.CanTransition(ReservationStatuses.Draft, ReservationStatuses.Confirmed));
    }

    [Fact]
    public void CanTransition_AllowsConfirmedToCancelled()
    {
        Assert.True(ReservationStatuses.CanTransition(ReservationStatuses.Confirmed, ReservationStatuses.Cancelled));
    }

    [Fact]
    public void CanTransition_DisallowsDraftToCancelled()
    {
        Assert.False(ReservationStatuses.CanTransition(ReservationStatuses.Draft, ReservationStatuses.Cancelled));
    }
}
