using SR2MP.Shared.Ownership;
using Xunit;
using static SR2MP.Tests.Ownership.TestSupport;

namespace SR2MP.Tests.Ownership;

public sealed class OwnerPickerTests
{
    private static readonly OwnershipConfig Nearest = new();

    private static string? Pick(OwnershipConfig config, string? current, bool held, params PlayerPresence[] who) =>
        OwnerPicker.Pick(who, current, held, Host, config);

    [Fact]
    public void PreferHostOnGivesTheActorToTheEligibleHost()
    {
        Assert.Equal(Host, Pick(HostFirst, null, false, Near(Host, 5000), Near(PlayerA, 10)));
    }

    [Fact]
    public void PreferHostOffGivesTheActorToTheNearestPlayer()
    {
        Assert.Equal(PlayerA, Pick(Nearest, null, false, Near(Host, 5000), Near(PlayerA, 10)));
        Assert.Equal(Host, Pick(Nearest, null, false, Near(Host, 5), Near(PlayerA, 10)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NearestWinsWhenTheHostIsAsleep(bool preferHost)
    {
        var config = new OwnershipConfig { PreferHost = preferHost };
        var winner = Pick(config, null, false, Asleep(Host, 1), Near(PlayerB, 400), Near(PlayerA, 100));
        Assert.Equal(PlayerA, winner);
    }

    [Fact]
    public void NobodyEligibleKeepsADormantOwnerOrNobody()
    {
        Assert.Equal(PlayerA, Pick(HostFirst, PlayerA, false, Asleep(Host, 9000), Asleep(PlayerA, 9000)));
        Assert.Null(Pick(HostFirst, PlayerA, false, Asleep(Host, 9000), new PlayerPresence(PlayerA, false, false, 9000)));
        Assert.Null(Pick(HostFirst, null, false, Asleep(Host, 9000)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TiesGoToTheOrdinalLowestPlayerId(bool preferHost)
    {
        var config = new OwnershipConfig { PreferHost = preferHost };
        Assert.Equal(PlayerB, Pick(config, null, false, Near(PlayerC, 100), Near(PlayerB, 100)));
        Assert.Equal("PLAYER_Z", Pick(config, null, false, Near("player_a", 100), Near("PLAYER_Z", 100)));
    }

    [Fact]
    public void TieBetweenHostAndPlayerWithoutPreferenceUsesTheId()
    {
        Assert.Equal(PlayerA, Pick(Nearest, null, false, Near(Host, 100), Near(PlayerA, 100)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HysteresisHoldsAndNeedsAClearMargin(bool preferHost)
    {
        var c = new OwnershipConfig { PreferHost = preferHost };
        Assert.Equal(PlayerA, Pick(c, PlayerA, false, Near(PlayerA, 1000), Near(PlayerB, 900)));
        Assert.Equal(PlayerA, Pick(c, PlayerA, true, Near(PlayerA, 1000), Near(PlayerB, 100)));
        Assert.Equal(PlayerB, Pick(c, PlayerA, false, Near(PlayerA, 1000), Near(PlayerB, 100)));
        Assert.Equal(PlayerB, Pick(c, PlayerA, true, Asleep(PlayerA, 9000), Near(PlayerB, 100)));
        Assert.Equal(PlayerB, Pick(c, PlayerA, true, Gone(PlayerA, 10), Near(PlayerB, 100)));
    }

    [Fact]
    public void CurrentOwnerMissingFromThePresenceListIsReplaced()
    {
        Assert.Equal(PlayerB, Pick(Nearest, PlayerA, true, Near(PlayerB, 100)));
    }

    [Fact]
    public void PreferHostTakesTheActorAsSoonAsTheHoldEnds()
    {
        Assert.Equal(PlayerA, Pick(HostFirst, PlayerA, true, Near(Host, 900), Near(PlayerA, 1000)));
        Assert.Equal(Host, Pick(HostFirst, PlayerA, false, Near(Host, 900), Near(PlayerA, 1000)));
    }

    [Fact]
    public void WithoutPreferenceTheHostNeedsTheMarginLikeAnyoneElse()
    {
        Assert.Equal(PlayerA, Pick(Nearest, PlayerA, false, Near(Host, 900), Near(PlayerA, 1000)));
        Assert.Equal(Host, Pick(Nearest, PlayerA, false, Near(Host, 700), Near(PlayerA, 1000)));
        Assert.Equal(PlayerA, Pick(Nearest, Host, false, Near(Host, 1000), Near(PlayerA, 700)));
    }

    [Fact]
    public void SwitchMarginIsConfigurable()
    {
        var wide = new OwnershipConfig { SwitchMargin = 0.5 };
        Assert.Equal(PlayerA, Pick(wide, PlayerA, false, Near(PlayerA, 1000), Near(PlayerB, 600)));
        Assert.Equal(PlayerB, Pick(wide, PlayerA, false, Near(PlayerA, 1000), Near(PlayerB, 400)));
    }
}
