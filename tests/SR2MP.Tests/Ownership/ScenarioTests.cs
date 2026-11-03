using SR2MP.Shared.Ownership;
using Xunit;

namespace SR2MP.Tests.Ownership;

public sealed class ScenarioTests
{
    [Theory]
    [InlineData(1UL, true)]
    [InlineData(2UL, true)]
    [InlineData(3UL, true)]
    [InlineData(1UL, false)]
    [InlineData(2UL, false)]
    [InlineData(3UL, false)]
    public void RandomChurnKeepsOneOwnerPerEpochAndConverges(ulong seed, bool preferHost)
    {
        var churn = ChurnScenario.Run(seed, 400, new OwnershipConfig { PreferHost = preferHost });
        Assert.Empty(churn.Problems);
        Assert.True(churn.Stats.Transfers > 0);
        Assert.True(churn.Stats.Commits > 0);
    }

    [Theory]
    [InlineData(4UL)]
    [InlineData(5UL)]
    public void LongChurnExercisesEveryHandoffPath(ulong seed)
    {
        var churn = ChurnScenario.Run(seed, 3000, new OwnershipConfig());
        Assert.Empty(churn.Problems);
        Assert.True(churn.Stats.ForcedCommits > 0);
        Assert.True(churn.Stats.Releases > 0);
        Assert.True(churn.Stats.Grants > 0);
    }

    [Fact]
    public void StaleEchoInjectionIsRejected() => Assert.Empty(Scenarios.StaleEchoRejected());

    [Fact]
    public void PlayerLeaveAndRejoinKeepsEpochsMonotonic() => Assert.Empty(Scenarios.RejoinEpochsMonotonic());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EquallyNearPlayersDoNotMakeOwnershipFlap(bool preferHost)
    {
        Assert.InRange(Scenarios.FlapTransfers(new OwnershipConfig { PreferHost = preferHost }), 0, 2);
    }

    [Fact]
    public void HeldActorStaysWithItsHolder() => Assert.Empty(Scenarios.HolderKeepsTheActor());

    [Fact]
    public void OldOwnerStaysAuthoritativeUntilAckAndCommit() => Assert.Empty(Scenarios.HandoffKeepsLastState());
}
