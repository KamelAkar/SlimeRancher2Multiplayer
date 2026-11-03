using SR2MP.Shared.Ownership;
using Xunit;
using static SR2MP.Tests.Ownership.TestSupport;

namespace SR2MP.Tests.Ownership;

public sealed class HolderAndSpawnTests
{
    private readonly List<OwnershipMessage> output = new();

    [Fact]
    public void HolderOwnsTheActorWhereverItGoes()
    {
        var service = Service(null, PlayerA);
        PlayerPresence[] farA = { Near(Host, 10), Asleep(PlayerA, 9000) };
        service.Track(7, farA, 0, output);
        Assert.Equal(Host, service.OwnerOf(7));
        output.Clear();

        // No hold time and no eligibility test: right after the grant, with PlayerA far away.
        service.SetHolder(7, PlayerA, farA, 5, output);
        var transfer = Assert.Single(output);
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Transfer, 7, 2, PlayerA, Host), transfer);
        Assert.Equal(PlayerA, service.HolderOf(7));

        output.Clear();
        Assert.True(service.OnAck(AckFor(transfer), PlayerA, 10, output));
        output.Clear();
        for (long now = 10000; now < 60000; now += 5000)
            service.Evaluate(7, farA, now, output);
        Assert.Empty(output);
        Assert.Equal(PlayerA, service.OwnerOf(7));

        service.SetHolder(7, null, farA, 60001, output);
        Assert.Null(service.HolderOf(7));
        Assert.Equal(OwnershipMessageKind.Transfer, Assert.Single(output).Kind);
        Assert.Equal(Host, output[0].Owner);
    }

    [Fact]
    public void HolderLeavingReturnsTheActorToTheNormalRules()
    {
        var service = Service(HostFirst, PlayerA);
        service.Track(7, new[] { Near(Host, 10) }, 0, output);
        service.SetHolder(7, PlayerA, new[] { Near(Host, 10) }, 5, output);
        service.OnAck(AckFor(output[^1]), PlayerA, 10, output);
        output.Clear();

        service.PlayerLeft(PlayerA, 100000, output);
        Assert.Null(service.HolderOf(7));
        service.Evaluate(7, new[] { Near(Host, 10) }, 100001, output);
        Assert.Equal(Host, service.OwnerOf(7));
    }

    [Fact]
    public void HolderThatIsNotConnectedIsIgnored()
    {
        var service = Service(null, PlayerA);
        service.Track(7, new[] { Near(PlayerA, 10) }, 0, output);
        output.Clear();
        service.SetHolder(7, PlayerB, new[] { Near(PlayerA, 10) }, 5, output);
        Assert.Empty(output);
        Assert.Equal(PlayerA, service.OwnerOf(7));
    }

    [Fact]
    public void HolderOfAnUnownedActorGetsAGrant()
    {
        var service = Service(null, PlayerA);
        service.Track(7, new[] { Asleep(Host, 10) }, 0, output);
        Assert.Empty(output);
        service.SetHolder(7, PlayerA, new[] { Asleep(Host, 10) }, 0, output);
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Grant, 7, 1, PlayerA, null), Assert.Single(output));
        service.SetHolder(99, PlayerA, new[] { Asleep(Host, 10) }, 0, output);
        Assert.Single(output);
    }

    [Fact]
    public void TrackOwnedStartsAtEpochOneWithoutAMessage()
    {
        var service = Service(null, PlayerA);
        service.TrackOwned(7, PlayerA, 0);
        Assert.True(service.IsTracked(7));
        Assert.Equal(PlayerA, service.OwnerOf(7));
        Assert.Equal(1u, service.EpochOf(7));
        Assert.Equal(0, service.Stats.Grants);

        var view = new OwnershipView(PlayerB);
        Assert.Equal(ApplyResult.Applied, view.AdoptSpawn(7, PlayerA));
        Assert.Equal(1u, view.EpochOf(7));
        Assert.Equal(UpdateVerdict.Accept, view.Check(7, PlayerA, 1));

        var spawner = new OwnershipView(PlayerA);
        spawner.AdoptSpawn(7, PlayerA);
        Assert.True(spawner.MayPublish(7, out var epoch));
        Assert.Equal(1u, epoch);
    }

    [Fact]
    public void TrackOwnedIsHeldAndHandsOffLikeAnyOtherActor()
    {
        var service = Service(null, PlayerA);
        service.TrackOwned(7, PlayerA, 1000);
        PlayerPresence[] hostClose = { Near(Host, 10), Near(PlayerA, 500) };

        service.Evaluate(7, hostClose, 2000, output);
        Assert.Empty(output);
        service.Evaluate(7, hostClose, 2500, output);
        Assert.Equal(new OwnershipMessage(OwnershipMessageKind.Transfer, 7, 2, Host, PlayerA), Assert.Single(output));

        var view = new OwnershipView(PlayerB);
        view.AdoptSpawn(7, PlayerA);
        Broadcast(output, view);
        Assert.Equal(1u, view.EpochOf(7));
        Assert.Equal(UpdateVerdict.Accept, view.Check(7, PlayerA, 1));
        Assert.Equal(UpdateVerdict.NotYetActive, view.Check(7, Host, 2));
    }

    [Fact]
    public void TrackOwnedAndAdoptSpawnDoNothingForKnownActors()
    {
        var service = Service(null, PlayerA);
        service.Track(7, new[] { Near(Host, 10) }, 0, output);
        service.TrackOwned(7, PlayerA, 0);
        Assert.Equal(Host, service.OwnerOf(7));

        var view = new OwnershipView(PlayerB);
        Broadcast(output, view);
        Assert.Equal(ApplyResult.Duplicate, view.AdoptSpawn(7, PlayerA));
        Assert.Equal(Host, view.OwnerOf(7));

        var late = new OwnershipView(PlayerB);
        late.AdoptSpawn(8, PlayerA);
        Assert.Equal(ApplyResult.Applied, late.Apply(Grant(8, 2, Host)));
        Assert.Equal(ApplyResult.Stale, late.Apply(Grant(8, 1, PlayerB)));
    }

    [Fact]
    public void ServiceAndViewsAgreeAfterASpawn()
    {
        var service = Service(null, PlayerA, PlayerB);
        service.TrackOwned(7, PlayerB, 0);
        var views = new[] { new OwnershipView(Host), new OwnershipView(PlayerA), new OwnershipView(PlayerB) };
        foreach (var view in views)
            view.AdoptSpawn(7, PlayerB);

        var snapshot = new List<OwnershipMessage>();
        service.Snapshot(snapshot);
        var joiner = new OwnershipView(PlayerC);
        Broadcast(snapshot, joiner);
        foreach (var view in views)
            Assert.Equal(joiner.Digest(), view.Digest());
    }

    [Fact]
    public void ServiceRejectsANullHostId()
    {
        Assert.Throws<ArgumentNullException>(() => new OwnershipService(null!));
    }

    [Fact]
    public void HostIsConnectedFromTheStart()
    {
        var service = new OwnershipService(Host);
        service.Track(7, new[] { Near(Host, 10) }, 0, output);
        service.SetHolder(7, Host, Array.Empty<PlayerPresence>(), 0, output);
        Assert.Equal(Host, service.OwnerOf(7));
        Assert.Equal(Host, service.HolderOf(7));
    }
}
