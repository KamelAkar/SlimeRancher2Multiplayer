using SR2MP.Shared.Ownership;
using Xunit;
using static SR2MP.Tests.Ownership.TestSupport;

namespace SR2MP.Tests.Ownership;

public sealed class OwnershipServiceTests
{
    private readonly List<OwnershipMessage> output = new();

    [Fact]
    public void TrackGrantsTheFirstOwnerWithEpochOne()
    {
        var service = Service(HostFirst, PlayerA);
        PlayerPresence[] who = { Asleep(Host, 9000), Near(PlayerA, 50) };
        service.Track(7, who, 0, output);

        var grant = Assert.Single(output);
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Grant, 7, 1, PlayerA, null), grant);
        Assert.Equal(PlayerA, service.OwnerOf(7));
        Assert.True(service.IsTracked(7));

        output.Clear();
        service.Evaluate(7, who, 100, output);
        Assert.Empty(output);
    }

    [Fact]
    public void HandoffIsAHandshakeWithEpochsAndCommit()
    {
        var service = StartOwnedByA();
        service.Evaluate(7, new[] { Near(Host, 10), Near(PlayerA, 50) }, 2000, output);

        var transfer = Assert.Single(output);
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Transfer, 7, 2, Host, PlayerA), transfer);
        Assert.Equal(PlayerA, service.OwnerOf(7));
        Assert.True(service.IsHandoffPending(7));

        output.Clear();
        Assert.False(service.OnAck(AckFor(transfer), PlayerA, 2100, output));
        Assert.True(service.OnAck(AckFor(transfer), Host, 2100, output));
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Commit, 7, 2, Host, PlayerA), Assert.Single(output));
        Assert.Equal(Host, service.OwnerOf(7));
        Assert.Equal(2u, service.EpochOf(7));

        output.Clear();
        Assert.False(service.OnAck(AckFor(transfer), Host, 2200, output));
        Assert.Empty(output);
    }

    [Fact]
    public void AckWithTheWrongKindOrEpochIsIgnored()
    {
        var service = StartOwnedByA();
        service.Evaluate(7, new[] { Near(Host, 10), Near(PlayerA, 50) }, 2000, output);
        var transfer = output[0];
        output.Clear();

        Assert.False(service.OnAck(transfer, Host, 2100, output));
        Assert.False(service.OnAck(AckFor(transfer) with { Epoch = 1 }, Host, 2100, output));
        Assert.False(service.OnAck(AckFor(transfer) with { ActorId = 8 }, Host, 2100, output));
        Assert.Empty(output);
        Assert.True(service.IsHandoffPending(7));
    }

    [Fact]
    public void EvaluateDoesNothingWhileAHandoffIsPending()
    {
        var service = Service(HostFirst, PlayerA, PlayerB);
        service.Track(7, new[] { Near(PlayerA, 50) }, 0, output);
        service.Evaluate(7, new[] { Near(Host, 10), Near(PlayerA, 50) }, 2000, output);
        output.Clear();

        service.Evaluate(7, new[] { Asleep(Host, 10), Asleep(PlayerA, 50), Near(PlayerB, 1) }, 2100, output);
        Assert.Empty(output);
    }

    [Fact]
    public void HandoffTimeoutForcesTheCommit()
    {
        var service = StartOwnedByA();
        service.Evaluate(7, new[] { Near(Host, 10), Near(PlayerA, 50) }, 2000, output);
        output.Clear();

        service.Tick(2500, output);
        Assert.Empty(output);
        service.Tick(3100, output);
        Assert.Equal(OwnershipMessageKind.Commit, Assert.Single(output).Kind);
        Assert.Equal(1, service.Stats.ForcedCommits);
    }

    [Fact]
    public void HandoffTimeoutGrantsBackWhenTheNewOwnerVanished()
    {
        // PlayerB shows up in the presence data but the service never saw it join (or its leave got lost).
        var service = StartOwnedByA();
        service.Evaluate(7, new[] { Near(PlayerA, 1000), Near(PlayerB, 10) }, 2000, output);
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Transfer, 7, 2, PlayerB, PlayerA), Assert.Single(output));
        output.Clear();

        service.Tick(3000, output);
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Grant, 7, 3, PlayerA, PlayerA), Assert.Single(output));
        Assert.Equal(PlayerA, service.OwnerOf(7));
        Assert.False(service.IsHandoffPending(7));
        Assert.Equal(1, service.Stats.AbortedHandoffs);
    }

    [Fact]
    public void NewOwnerLeavingMidHandoffCancelsIt()
    {
        var service = StartOwnedByA();
        service.Evaluate(7, new[] { Near(Host, 10), Near(PlayerA, 50) }, 2000, output);
        output.Clear();

        service.PlayerLeft(Host, 2100, output);
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Grant, 7, 3, PlayerA, PlayerA), Assert.Single(output));
        Assert.False(service.IsHandoffPending(7));
    }

    [Fact]
    public void OldOwnerLeavingMidHandoffCommitsToTheNewOwner()
    {
        var service = StartOwnedByA();
        service.Evaluate(7, new[] { Near(Host, 10), Near(PlayerA, 50) }, 2000, output);
        output.Clear();

        service.PlayerLeft(PlayerA, 2100, output);
        Assert.Equal(OwnershipMessageKind.Commit, Assert.Single(output).Kind);
        Assert.Equal(Host, service.OwnerOf(7));
    }

    [Fact]
    public void OwnerLeavingReleasesAndTheActorIsGrantedAgain()
    {
        var service = StartOwnedByA();
        var orphans = service.PlayerLeft(PlayerA, 100, output);
        Assert.Equal(new[] { 7L }, orphans);
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Release, 7, 2, null, PlayerA), Assert.Single(output));

        output.Clear();
        service.Evaluate(7, new[] { Near(Host, 10) }, 200, output);
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Grant, 7, 3, Host, null), Assert.Single(output));
    }

    [Fact]
    public void NobodyEligibleWithTheOwnerGoneReleases()
    {
        var service = StartOwnedByA();
        service.Evaluate(7, new[] { Gone(PlayerA, 50), Asleep(Host, 10) }, 100, output);
        Assert.Equal(OwnershipMessageKind.Release, Assert.Single(output).Kind);
        Assert.Null(service.OwnerOf(7));
    }

    [Fact]
    public void UntrackReleasesAnOwnedActor()
    {
        var service = StartOwnedByA();
        service.Untrack(7, output);
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Release, 7, 2, null, PlayerA), Assert.Single(output));
        Assert.False(service.IsTracked(7));
        Assert.Empty(service.TrackedActors);

        output.Clear();
        service.Track(8, new[] { Asleep(Host, 10) }, 0, output);
        service.Untrack(8, output);
        service.Untrack(9, output);
        Assert.Empty(output);
    }

    [Fact]
    public void SnapshotReportsEpochsForJoiners()
    {
        var service = Service(HostFirst, PlayerA);
        service.Track(7, new[] { Near(PlayerA, 50) }, 0, output);
        service.Track(8, new[] { Near(Host, 50) }, 0, output);
        service.Track(9, new[] { Asleep(Host, 50) }, 0, output);
        service.PlayerLeft(PlayerA, 10, output);

        var snapshot = new List<OwnershipMessage>();
        service.Snapshot(snapshot);
        Assert.Equal(2, snapshot.Count);

        var view = new OwnershipView(PlayerB);
        Broadcast(snapshot, view);
        Assert.Equal(Host, view.OwnerOf(8));
        Assert.Null(view.OwnerOf(7));
        Assert.Equal(2u, view.EpochOf(7));
        Assert.False(view.IsKnown(9));
    }

    [Fact]
    public void TryGetCurrentAnswersOneActor()
    {
        var service = StartOwnedByA();
        Assert.True(service.TryGetCurrent(7, out var current));
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Grant, 7, 1, PlayerA, null), current);
        Assert.False(service.TryGetCurrent(99, out _));

        service.PlayerLeft(PlayerA, 10, output);
        Assert.True(service.TryGetCurrent(7, out current));
        Assert.Equal(OwnershipMessageKind.Release, current.Kind);
        Assert.Equal(2u, current.Epoch);
    }

    [Fact]
    public void StatsCountEveryKindOfMessage()
    {
        var service = StartOwnedByA();
        service.Evaluate(7, new[] { Near(Host, 10), Near(PlayerA, 50) }, 2000, output);
        service.OnAck(AckFor(output[^1]), Host, 2100, output);
        service.Untrack(7, output);

        Assert.Equal(1, service.Stats.Grants);
        Assert.Equal(1, service.Stats.Transfers);
        Assert.Equal(1, service.Stats.Commits);
        Assert.Equal(1, service.Stats.Releases);
        Assert.Equal(0, service.Stats.ForcedCommits);
    }

    private OwnershipService StartOwnedByA()
    {
        var service = Service(HostFirst, PlayerA);
        service.Track(7, new[] { Asleep(Host, 9000), Near(PlayerA, 50) }, 0, output);
        output.Clear();
        return service;
    }
}
