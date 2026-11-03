using SR2MP.Shared.Ownership;
using Xunit;
using static SR2MP.Tests.Ownership.TestSupport;

namespace SR2MP.Tests.Ownership;

public sealed class OwnershipViewTests
{
    [Fact]
    public void ApplyIsIdempotentAndOrderIndependent()
    {
        var g1 = Grant(5, 1, PlayerA);
        var t2 = Transfer(5, 2, PlayerB, PlayerA);
        var c2 = Commit(5, 2, PlayerB, PlayerA);
        var view = new OwnershipView(Host);

        Assert.Equal(ApplyResult.Applied, view.Apply(g1));
        Assert.Equal(ApplyResult.Duplicate, view.Apply(g1));
        Assert.Equal(ApplyResult.Applied, view.Apply(t2));
        Assert.Equal(ApplyResult.Duplicate, view.Apply(t2));
        Assert.False(view.IsHandoffIncoming(5, out _));
        Assert.Equal(PlayerA, view.OwnerOf(5));
        Assert.Equal(ApplyResult.Applied, view.Apply(c2));
        Assert.Equal(ApplyResult.Duplicate, view.Apply(c2));
        Assert.Equal(ApplyResult.Stale, view.Apply(g1));
        Assert.NotEqual(ApplyResult.Applied, view.Apply(t2));
        Assert.Equal(PlayerB, view.OwnerOf(5));
        Assert.Equal(2u, view.EpochOf(5));

        var other = new OwnershipView(Host);
        other.Apply(g1);
        Assert.Equal(ApplyResult.Applied, other.Apply(c2));
        Assert.Equal(PlayerB, other.OwnerOf(5));
    }

    [Fact]
    public void TransferForAnUnknownActorRecordsThePreviousOwner()
    {
        var view = new OwnershipView(PlayerB);
        Assert.Equal(ApplyResult.Applied, view.Apply(Transfer(5, 4, PlayerB, PlayerA)));
        Assert.Equal(PlayerA, view.OwnerOf(5));
        Assert.Equal(3u, view.EpochOf(5));
        Assert.True(view.IsHandoffIncoming(5, out var epoch));
        Assert.Equal(4u, epoch);
    }

    [Fact]
    public void AckIsIgnoredByViews()
    {
        var view = new OwnershipView(Host);
        Assert.Equal(ApplyResult.Duplicate, view.Apply(AckFor(Transfer(5, 2, PlayerB, PlayerA))));
        Assert.False(view.IsKnown(5));
    }

    [Fact]
    public void SameEpochWithAnotherOwnerIsStale()
    {
        var view = new OwnershipView(Host);
        view.Apply(Grant(5, 3, PlayerA));
        Assert.Equal(ApplyResult.Stale, view.Apply(Grant(5, 3, PlayerB)));
        Assert.Equal(ApplyResult.Stale, view.Apply(Release(5, 3)));
        Assert.Equal(PlayerA, view.OwnerOf(5));
        Assert.Equal(1, view.Stats.Applied);
        Assert.Equal(2, view.Stats.StaleMessages);
    }

    [Fact]
    public void CheckVerdicts()
    {
        var view = new OwnershipView(PlayerC);
        view.Apply(Grant(5, 4, PlayerA));
        Assert.Equal(UpdateVerdict.Accept, view.Check(5, PlayerA, 4));
        Assert.Equal(UpdateVerdict.NotOwner, view.Check(5, PlayerB, 4));
        Assert.Equal(UpdateVerdict.StaleEpoch, view.Check(5, PlayerA, 3));
        Assert.Equal(UpdateVerdict.FutureEpoch, view.Check(5, PlayerA, 5));
        Assert.Equal(UpdateVerdict.EchoOfSelf, view.Check(5, PlayerC, 4));
        Assert.Equal(UpdateVerdict.UnknownActor, view.Check(99, PlayerA, 1));

        view.Apply(Transfer(5, 5, PlayerB, PlayerA));
        Assert.Equal(UpdateVerdict.Accept, view.Check(5, PlayerA, 4));
        Assert.Equal(UpdateVerdict.NotYetActive, view.Check(5, PlayerB, 5));
        Assert.Equal(UpdateVerdict.FutureEpoch, view.Check(5, PlayerA, 5));
        Assert.Equal(1, view.Stats.Echoes);
        Assert.Equal(1, view.Stats.Unknown);
        Assert.Equal(1, view.Stats.NotYetActive);

        Assert.False(view.MayPublish(5, out _));
        var owner = new OwnershipView(PlayerA);
        owner.Apply(Grant(5, 4, PlayerA));
        Assert.True(owner.MayPublish(5, out var epoch));
        Assert.Equal(4u, epoch);
    }

    [Fact]
    public void RelayedUpdateWithoutSenderIsJudgedByEpoch()
    {
        var view = new OwnershipView(PlayerC);
        view.Apply(Grant(5, 4, PlayerA));
        Assert.Equal(UpdateVerdict.Accept, view.Check(5, null, 4));
        Assert.Equal(UpdateVerdict.StaleEpoch, view.Check(5, null, 3));
        Assert.Equal(UpdateVerdict.FutureEpoch, view.Check(5, null, 5));
        Assert.Equal(UpdateVerdict.UnknownActor, view.Check(6, null, 1));

        view.Apply(Transfer(5, 5, PlayerB, PlayerA));
        Assert.Equal(UpdateVerdict.Accept, view.Check(5, null, 4));
        Assert.Equal(UpdateVerdict.NotYetActive, view.Check(5, null, 5));
        Assert.Equal(UpdateVerdict.FutureEpoch, view.Check(5, null, 6));
    }

    [Fact]
    public void RelayedUpdateOfAnUnownedActorIsNotOwner()
    {
        var view = new OwnershipView(PlayerC);
        view.Apply(Release(5, 2));
        Assert.Equal(UpdateVerdict.NotOwner, view.Check(5, null, 2));
        Assert.Equal(UpdateVerdict.StaleEpoch, view.Check(5, null, 1));
    }

    [Fact]
    public void RelayedUpdateOfOurOwnActorIsAnEcho()
    {
        var view = new OwnershipView(PlayerA);
        view.Apply(Grant(5, 4, PlayerA));
        Assert.Equal(UpdateVerdict.EchoOfSelf, view.Check(5, null, 4));
        Assert.Equal(UpdateVerdict.StaleEpoch, view.Check(5, null, 3));

        var unnamed = new OwnershipView(null);
        unnamed.Apply(Grant(5, 4, PlayerA));
        Assert.Equal(UpdateVerdict.Accept, unnamed.Check(5, null, 4));
    }

    [Fact]
    public void HandoffIncomingForTheNewOwner()
    {
        var view = new OwnershipView(PlayerB);
        view.Apply(Grant(5, 1, PlayerA));
        view.Apply(Transfer(5, 2, PlayerB, PlayerA));
        Assert.True(view.IsHandoffIncoming(5, out var epoch));
        Assert.Equal(2u, epoch);
        Assert.False(view.IsHandoffOutgoing(5));
        Assert.False(view.MayPublish(5, out _));
    }

    [Fact]
    public void HandoffOutgoingForTheOldOwnerUntilTheCommit()
    {
        var view = new OwnershipView(PlayerA);
        view.Apply(Grant(5, 1, PlayerA));
        Assert.False(view.IsHandoffOutgoing(5));

        view.Apply(Transfer(5, 2, PlayerB, PlayerA));
        Assert.True(view.IsHandoffOutgoing(5));
        Assert.True(view.MayPublish(5, out var epoch));
        Assert.Equal(1u, epoch);
        Assert.False(view.IsHandoffIncoming(5, out _));

        view.Apply(Commit(5, 2, PlayerB, PlayerA));
        Assert.False(view.IsHandoffOutgoing(5));
        Assert.False(view.MayPublish(5, out _));
        Assert.False(view.IsHandoffOutgoing(99));
    }

    [Fact]
    public void HandoffOutgoingIsFalseForBystandersAndCancelledHandoffs()
    {
        var bystander = new OwnershipView(PlayerC);
        bystander.Apply(Grant(5, 1, PlayerA));
        bystander.Apply(Transfer(5, 2, PlayerB, PlayerA));
        Assert.False(bystander.IsHandoffOutgoing(5));

        var owner = new OwnershipView(PlayerA);
        owner.Apply(Grant(5, 1, PlayerA));
        owner.Apply(Transfer(5, 2, PlayerB, PlayerA));
        owner.Apply(Grant(5, 3, PlayerA));
        Assert.False(owner.IsHandoffOutgoing(5));
        Assert.True(owner.MayPublish(5, out var epoch));
        Assert.Equal(3u, epoch);
    }

    [Fact]
    public void SelfCanBeSetLater()
    {
        var view = new OwnershipView(null);
        view.Apply(Grant(5, 1, PlayerA));
        Assert.False(view.MayPublish(5, out _));
        view.Self = PlayerA;
        Assert.True(view.MayPublish(5, out _));
        Assert.Equal(UpdateVerdict.EchoOfSelf, view.Check(5, PlayerA, 1));
    }

    [Fact]
    public void ForgetAndClearDropRecords()
    {
        var view = new OwnershipView(Host);
        view.Apply(Grant(5, 1, PlayerA));
        view.Apply(Grant(6, 1, PlayerB));
        view.Forget(5);
        Assert.False(view.IsKnown(5));
        Assert.True(view.IsKnown(6));
        view.Clear();
        Assert.False(view.IsKnown(6));
        Assert.Equal(0u, view.EpochOf(6));
        Assert.Null(view.OwnerOf(6));
    }

    [Fact]
    public void DigestDependsOnOwnedRecordsOnlyAndNotOnOrder()
    {
        var first = new OwnershipView(Host);
        var second = new OwnershipView(PlayerA);
        Assert.Equal(new OwnershipView(null).Digest(), first.Digest());

        Broadcast(new[] { Grant(5, 1, PlayerA), Grant(9, 2, PlayerB), Release(3, 4) }, first);
        Broadcast(new[] { Grant(9, 2, PlayerB), Grant(5, 1, PlayerA) }, second);
        Assert.Equal(first.Digest(), second.Digest());

        second.Apply(Grant(5, 2, PlayerA));
        Assert.NotEqual(first.Digest(), second.Digest());
        first.Apply(Grant(5, 2, PlayerB));
        Assert.NotEqual(first.Digest(), second.Digest());
    }

    [Fact]
    public void DigestIsFnv1aOverIdOwnerAndEpoch()
    {
        var view = new OwnershipView(null);
        view.Apply(Grant(1, 2, "A"));

        var hash = 14695981039346656037UL;
        byte[] bytes = { 1, 0, 0, 0, 0, 0, 0, 0, (byte)'A', 2, 0, 0, 0 };
        foreach (var value in bytes)
            hash = (hash ^ value) * 1099511628211UL;
        Assert.Equal(hash, view.Digest());
    }
}
