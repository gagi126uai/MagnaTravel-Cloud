using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Opciones A/B/C (decision #1 firmada del dueño, 2026-08-11/12): tests puros de
/// <see cref="OptionGroupRules"/> — la regla PURA que comparten <c>ReservaMoneyCalculator</c> (no
/// duplicar totales) y <c>ReservaService</c> (rechazar "el cliente aceptó" con grupos sin resolver).
/// </summary>
public class OptionGroupRulesTests
{
    [Fact]
    public void Normalize_TrimsAndTreatsBlankAsNull()
    {
        Assert.Equal("hoteles", OptionGroupRules.Normalize("  hoteles  "));
        Assert.Null(OptionGroupRules.Normalize(null));
        Assert.Null(OptionGroupRules.Normalize(""));
        Assert.Null(OptionGroupRules.Normalize("   "));
    }

    [Fact]
    public void FindAmbiguousGroups_TwoLiveInSameGroup_IsAmbiguous()
    {
        var infos = new[]
        {
            new OptionGroupRules.OptionGroupServiceInfo("hoteles", IsLive: true),
            new OptionGroupRules.OptionGroupServiceInfo("Hoteles", IsLive: true), // misma mayuscula distinta
        };

        var ambiguous = OptionGroupRules.FindAmbiguousGroups(infos);

        Assert.Contains("hoteles", ambiguous);
        Assert.Single(ambiguous);
    }

    [Fact]
    public void FindAmbiguousGroups_OneLiveOneCancelled_NotAmbiguous()
    {
        var infos = new[]
        {
            new OptionGroupRules.OptionGroupServiceInfo("hoteles", IsLive: true),
            new OptionGroupRules.OptionGroupServiceInfo("hoteles", IsLive: false), // cancelado: no compite mas
        };

        var ambiguous = OptionGroupRules.FindAmbiguousGroups(infos);

        Assert.Empty(ambiguous);
    }

    [Fact]
    public void FindAmbiguousGroups_ServicesWithoutGroup_AreIgnored()
    {
        var infos = new[]
        {
            new OptionGroupRules.OptionGroupServiceInfo(null, IsLive: true),
            new OptionGroupRules.OptionGroupServiceInfo("", IsLive: true),
        };

        var ambiguous = OptionGroupRules.FindAmbiguousGroups(infos);

        Assert.Empty(ambiguous);
    }

    [Fact]
    public void FindAmbiguousGroups_DifferentGroups_EachEvaluatedIndependently()
    {
        var infos = new[]
        {
            new OptionGroupRules.OptionGroupServiceInfo("hoteles", IsLive: true),
            new OptionGroupRules.OptionGroupServiceInfo("hoteles", IsLive: true), // ambiguo
            new OptionGroupRules.OptionGroupServiceInfo("aereo-tramo1", IsLive: true), // solo, no ambiguo
        };

        var ambiguous = OptionGroupRules.FindAmbiguousGroups(infos);

        Assert.Contains("hoteles", ambiguous);
        Assert.DoesNotContain("aereo-tramo1", ambiguous);
    }

    [Fact]
    public void BelongsToAmbiguousGroup_CaseInsensitiveMatch()
    {
        var ambiguousGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hoteles" };

        Assert.True(OptionGroupRules.BelongsToAmbiguousGroup("Hoteles", ambiguousGroups));
        Assert.True(OptionGroupRules.BelongsToAmbiguousGroup("  hoteles  ", ambiguousGroups));
        Assert.False(OptionGroupRules.BelongsToAmbiguousGroup("traslados", ambiguousGroups));
        Assert.False(OptionGroupRules.BelongsToAmbiguousGroup(null, ambiguousGroups));
    }
}
