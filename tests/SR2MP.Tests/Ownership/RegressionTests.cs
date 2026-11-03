using SR2MP.Shared.Ownership;
using Xunit;
using static SR2MP.Tests.Ownership.TestSupport;

namespace SR2MP.Tests.Ownership;

/// <summary>
/// One test per known bug class of the original library's mutation twins. Each would fail if that bug came back.
/// </summary>
public sealed class RegressionTests
{
    private readonly List<OwnershipMessage> output = new();

    [Fact]
    public void StaleEpochIsNeverAccepted()
    {
        var view = new OwnershipView(PlayerC);
        Broadcast(new[] { Grant(9, 1, PlayerA), Commit(9, 2, PlayerB, PlayerA) }, view);

        Assert.Equal(UpdateVerdict.StaleEpoch, view.Check(9, PlayerA, 1));
        Assert.Equal(UpdateVerdict.StaleEpoch, view.Check(9, PlayerB, 1));
        Assert.Equal(UpdateVerdict.StaleEpoch, view.Check(9, null, 1));
        Assert.Equal(ApplyResult.Stale, view.Apply(Grant(9, 1, PlayerA)));
        Assert.Equal(ApplyResult.Stale, view.Apply(Release(9, 1)));
        Assert.Equal(PlayerB, view.OwnerOf(9));
    }

    [Fact]
    public void ReplayedOlderAssignmentNeverOverwritesANewerOne()
    {
        var view = new OwnershipView(PlayerC);
        view.Apply(Grant(9, 3, PlayerB));
        Assert.Equal(ApplyResult.Stale, view.Apply(Release(9, 2)));
        Assert.Equal(ApplyResult.Stale, view.Apply(Commit(9, 2, PlayerA, PlayerB)));
        Assert.Equal(ApplyResult.Stale, view.Apply(Grant(9, 1, PlayerA)));
        Assert.Equal(ApplyResult.Stale, view.Apply(Transfer(9, 2, PlayerA, PlayerB)));
        Assert.Equal(PlayerB, view.OwnerOf(9));
        Assert.Equal(3u, view.EpochOf(9));

        // A late Commit of an older handoff must not cancel the newer handoff already announced.
        var pending = new OwnershipView(PlayerC);
        pending.Apply(Grant(9, 1, PlayerA));
        pending.Apply(Transfer(9, 3, PlayerC, PlayerB));
        Assert.Equal(ApplyResult.Stale, pending.Apply(Commit(9, 2, PlayerB, PlayerA)));
        Assert.True(pending.IsHandoffIncoming(9, out var epoch));
        Assert.Equal(3u, epoch);
        Assert.Equal(ApplyResult.Stale, pending.Apply(Transfer(9, 2, PlayerB, PlayerA)));
    }

    [Fact]
    public void EveryOwnershipChangeBumpsTheEpoch()
    {
        PlayerPresence[] nearB = { Near(PlayerA, 1000), Near(PlayerB, 10) };
        var service = Service(null, PlayerA, PlayerB);
        service.Track(7, new[] { Near(PlayerA, 50) }, 0, output);
        service.Evaluate(7, nearB, 2000, output);
        service.OnAck(AckFor(output[^1]), PlayerB, 2100, output);
        service.PlayerLeft(PlayerB, 2200, output);
        service.Evaluate(7, new[] { Near(PlayerA, 50) }, 2300, output);
        service.PlayerJoined(PlayerB);
        service.Evaluate(7, nearB, 5000, output);
        service.PlayerLeft(PlayerB, 5100, output);
        service.Evaluate(7, nearB, 9000, output);
        service.Tick(10000, output);
        service.Untrack(7, output);

        var kinds = output.Select(message => message.Kind).ToArray();
        OwnershipMessageKind[] expectedKinds =
        {
            OwnershipMessageKind.Grant, OwnershipMessageKind.Transfer, OwnershipMessageKind.Commit,
            OwnershipMessageKind.Release, OwnershipMessageKind.Grant, OwnershipMessageKind.Transfer,
            OwnershipMessageKind.Grant, OwnershipMessageKind.Transfer, OwnershipMessageKind.Grant,
            OwnershipMessageKind.Release,
        };
        Assert.Equal(expectedKinds, kinds);
        Assert.Equal(new uint[] { 1, 2, 2, 3, 4, 5, 6, 7, 8, 9 }, output.Select(message => message.Epoch).ToArray());

        // A released actor regranted to its old owner gets a new epoch, so the old owner's late updates stay stale.
        var view = new OwnershipView(PlayerC);
        service = Service(HostFirst, PlayerA);
        output.Clear();
        service.Track(8, new[] { Near(PlayerA, 50) }, 0, output);
        service.PlayerLeft(PlayerA, 10, output);
        service.PlayerJoined(PlayerA);
        service.Evaluate(8, new[] { Near(PlayerA, 50) }, 20, output);
        Broadcast(output, view);
        Assert.Equal(new uint[] { 1, 2, 3 }, output.Select(message => message.Epoch).ToArray());
        Assert.Equal(UpdateVerdict.StaleEpoch, view.Check(8, PlayerA, 1));
        Assert.Equal(UpdateVerdict.Accept, view.Check(8, PlayerA, 3));
    }

    [Fact]
    public void HandoffNeverCommitsWithoutTheAck()
    {
        var service = Service(HostFirst, PlayerA);
        service.Track(7, new[] { Near(PlayerA, 50) }, 0, output);
        output.Clear();
        service.Evaluate(7, new[] { Near(Host, 10), Near(PlayerA, 50) }, 2000, output);

        Assert.Equal(OwnershipMessageKind.Transfer, Assert.Single(output).Kind);
        Assert.Equal(PlayerA, service.OwnerOf(7));
        Assert.Equal(1u, service.EpochOf(7));
        Assert.True(service.IsHandoffPending(7));

        output.Clear();
        service.Tick(2999, output);
        Assert.Empty(output);
        Assert.Equal(0, service.Stats.Commits);
        Assert.Empty(Scenarios.HandoffKeepsLastState());
    }

    [Fact]
    public void HysteresisPreventsFlapping()
    {
        Assert.InRange(Scenarios.FlapTransfers(new OwnershipConfig()), 0, 2);

        // The same scenario without hold time and margin flaps, so it really detects the bug.
        var noHysteresis = new OwnershipConfig { MinHoldMs = 0, SwitchMargin = 0 };
        Assert.True(Scenarios.FlapTransfers(noHysteresis) > 10);
    }

    [Fact]
    public void HolderIsNeverIgnored()
    {
        Assert.Empty(Scenarios.HolderKeepsTheActor());

        var service = Service(HostFirst, PlayerA, PlayerB);
        service.Track(7, new[] { Near(PlayerB, 10) }, 0, output);
        service.SetHolder(7, PlayerA, new[] { Near(PlayerB, 10) }, 1, output);
        service.OnAck(AckFor(output[^1]), PlayerA, 2, output);
        output.Clear();
        service.Evaluate(7, new[] { Near(Host, 1), Near(PlayerB, 1), Gone(PlayerA, 9000) }, 99999, output);
        Assert.Empty(output);
        Assert.Equal(PlayerA, service.OwnerOf(7));
    }

    [Fact]
    public void NonOwnerIsNeverAccepted()
    {
        var view = new OwnershipView(Host);
        view.Apply(Grant(9, 4, PlayerA));
        Assert.Equal(UpdateVerdict.NotOwner, view.Check(9, PlayerB, 4));
        Assert.Equal(UpdateVerdict.NotOwner, view.Check(9, PlayerC, 4));

        view.Apply(Transfer(9, 5, PlayerB, PlayerA));
        Assert.Equal(UpdateVerdict.NotOwner, view.Check(9, PlayerB, 4));
        Assert.Equal(UpdateVerdict.FutureEpoch, view.Check(9, PlayerC, 5));

        view.Apply(Release(9, 6));
        Assert.Equal(UpdateVerdict.NotOwner, view.Check(9, PlayerA, 6));
        Assert.Equal(UpdateVerdict.NotOwner, view.Check(9, null, 6));
        Assert.Equal(0, view.Stats.Accepted);
    }
}
