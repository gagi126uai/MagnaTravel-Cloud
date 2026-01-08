using TravelApi.Models;
using Xunit;

namespace TravelApi.Tests.Unit;

public class TreasuryTests
{
    [Fact]
    public void HasValidAmount_ReturnsFalseForNegativeReceipt()
    {
        var receipt = new TreasuryReceipt { Amount = -5m };

        Assert.False(receipt.HasValidAmount());
    }

    [Fact]
    public void HasValidAmount_ReturnsTrueForZeroReceipt()
    {
        var receipt = new TreasuryReceipt { Amount = 0m };

        Assert.True(receipt.HasValidAmount());
    }

    [Fact]
    public void RemainingAmount_SubtractsApplications()
    {
        var receipt = new TreasuryReceipt { Amount = 100m };
        receipt.Applications.Add(new TreasuryApplication { AmountApplied = 30m });
        receipt.Applications.Add(new TreasuryApplication { AmountApplied = 20m });

        Assert.Equal(50m, receipt.RemainingAmount);
    }

    [Fact]
    public void HasValidAmount_ReturnsFalseForZeroApplication()
    {
        var application = new TreasuryApplication { AmountApplied = 0m };

        Assert.False(application.HasValidAmount());
    }

    [Fact]
    public void HasValidAmount_ReturnsTrueForPositiveApplication()
    {
        var application = new TreasuryApplication { AmountApplied = 10m };

        Assert.True(application.HasValidAmount());
    }
}
