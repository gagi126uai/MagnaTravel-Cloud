using TravelApi.Models;
using Xunit;

namespace TravelApi.Tests.Unit;

public class TariffValidityTests
{
    [Fact]
    public void HasValidRange_ReturnsFalseWhenEndDateIsBeforeStartDate()
    {
        var validity = new TariffValidity
        {
            StartDate = new DateTime(2024, 8, 10),
            EndDate = new DateTime(2024, 8, 1)
        };

        Assert.False(validity.HasValidRange());
    }

    [Fact]
    public void HasValidRange_ReturnsTrueWhenRangeIsValid()
    {
        var validity = new TariffValidity
        {
            StartDate = new DateTime(2024, 8, 1),
            EndDate = new DateTime(2024, 8, 10)
        };

        Assert.True(validity.HasValidRange());
    }
}
