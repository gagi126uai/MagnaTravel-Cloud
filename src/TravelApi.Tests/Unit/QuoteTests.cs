using TravelApi.Models;
using Xunit;

namespace TravelApi.Tests.Unit;

public class QuoteTests
{
    [Fact]
    public void IsValid_ReturnsTrueForDraft()
    {
        Assert.True(QuoteStatuses.IsValid(QuoteStatuses.Draft));
    }

    [Fact]
    public void IsValid_ReturnsTrueForSent()
    {
        Assert.True(QuoteStatuses.IsValid(QuoteStatuses.Sent));
    }

    [Fact]
    public void IsValid_ReturnsFalseForUnknownStatus()
    {
        Assert.False(QuoteStatuses.IsValid("Archived"));
    }

    [Fact]
    public void HasValidTotal_ReturnsFalseForNegativeTotal()
    {
        var version = new QuoteVersion { TotalAmount = -1m };

        Assert.False(version.HasValidTotal());
    }

    [Fact]
    public void HasValidTotal_ReturnsTrueForPositiveTotal()
    {
        var version = new QuoteVersion { TotalAmount = 1200m };

        Assert.True(version.HasValidTotal());
    }
}
