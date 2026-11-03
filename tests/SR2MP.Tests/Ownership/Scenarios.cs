using SR2MP.Shared.Ownership;
using static SR2MP.Tests.Ownership.TestSupport;

namespace SR2MP.Tests.Ownership;

/// <summary>Hand driven scenarios. Each returns the problems it found, empty when every invariant held.</summary>
internal static class Scenarios
{
    /// <summary>An old assignment replayed later must not win, and a past owner's updates must be refused.</summary>
    public static List<string> StaleEchoRejected()
    {
        var problems = new List<string>();
        var host = new OwnershipView(Host);
        var a = new OwnershipView(PlayerA);
        var b = new OwnershipView(PlayerB);
        var grant = Grant(9, 1, PlayerA);
        OwnershipMessage[] handoff = { grant, Transfer(9, 2, PlayerB, PlayerA), Commit(9, 2, PlayerB, PlayerA) };
        Broadcast(handoff, host, a, b);

        foreach (var view in new[] { host, a, b })
        {
            view.Apply(grant);
            if (view.OwnerOf(9) != PlayerB || view.EpochOf(9) != 2)
                problems.Add("an echoed old Grant overwrote the new owner");
        }

        if (host.Check(9, PlayerA, 1) == UpdateVerdict.Accept)
            problems.Add("update from the previous owner at the old epoch was accepted");
        if (host.Check(9, PlayerA, 2) == UpdateVerdict.Accept)
            problems.Add("update from the previous owner at the new epoch was accepted");
        if (host.Check(9, PlayerB, 2) != UpdateVerdict.Accept)
            problems.Add("the real owner was rejected");
        if (b.Check(9, PlayerB, 2) == UpdateVerdict.Accept)
            problems.Add("own echo accepted");
        return problems;
    }

    /// <summary>Epochs keep rising across leave and rejoin, and replays of earlier rounds never apply.</summary>
    public static List<string> RejoinEpochsMonotonic()
    {
        var problems = new List<string>();
        var service = Service(HostFirst, PlayerA);
        uint last = 0;
        var seen = new List<OwnershipMessage>();
        for (var round = 0L; round < 4; round++)
        {
            var output = new List<OwnershipMessage>();
            service.PlayerJoined(PlayerA);
            service.Track(5, new[] { Near(PlayerA, 10) }, round * 1000, output);
            service.Evaluate(5, new[] { Near(PlayerA, 10) }, (round * 1000) + 1, output);
            service.PlayerLeft(PlayerA, (round * 1000) + 500, output);
            foreach (var message in output)
            {
                if (message.Epoch <= last)
                    problems.Add("epoch did not rise across leave and rejoin");
                last = message.Epoch;
                seen.Add(message);
            }
        }

        var view = new OwnershipView(Host);
        Broadcast(seen, view);
        if (seen.Count(message => view.Apply(message) == ApplyResult.Applied) > 0)
            problems.Add("a replayed message from an earlier round applied");
        return problems;
    }

    /// <summary>
    /// Two players about equally near swap which one is slightly closer every 300 ms, the host is far away.
    /// Returns the number of transfers.
    /// </summary>
    public static int FlapTransfers(OwnershipConfig config)
    {
        var service = Service(config, PlayerA, PlayerB);
        var output = new List<OwnershipMessage>();
        service.Track(7, new[] { Asleep(Host, 9000), Near(PlayerA, 500), Near(PlayerB, 545) }, 0, output);
        var transfers = 0;
        for (var i = 1; i < 28; i++)
        {
            var flip = (i & 1) != 0;
            var now = i * 300L;
            output.Clear();
            PlayerPresence[] who =
            {
                Asleep(Host, 9000), Near(PlayerA, flip ? 545 : 500), Near(PlayerB, flip ? 500 : 545),
            };
            service.Evaluate(7, who, now, output);
            foreach (var message in output.Where(message => message.Kind == OwnershipMessageKind.Transfer))
            {
                transfers++;
                service.OnAck(AckFor(message), message.Owner!, now, new List<OwnershipMessage>());
            }
        }

        return transfers;
    }

    /// <summary>The host owns an actor, PlayerA vacuums it up and walks far away while the host stays next to it.</summary>
    public static List<string> HolderKeepsTheActor()
    {
        var problems = new List<string>();
        var service = Service(HostFirst, PlayerA);
        var output = new List<OwnershipMessage>();
        PlayerPresence[] farA = { Near(Host, 10), Asleep(PlayerA, 9000) };
        service.Track(7, farA, 0, output);
        if (service.OwnerOf(7) != Host)
            problems.Add("the host should own it first");

        output.Clear();
        service.SetHolder(7, PlayerA, farA, 5, output);
        if (output.Count != 1 || output[0].Kind != OwnershipMessageKind.Transfer || output[0].Owner != PlayerA)
            problems.Add("holding did not start a handoff to the holder");
        else
            service.OnAck(AckFor(output[0]), PlayerA, 10, new List<OwnershipMessage>());

        for (long now = 10000; now < 60000; now += 5000)
            service.Evaluate(7, farA, now, output);
        if (service.OwnerOf(7) != PlayerA)
            problems.Add("the actor left its holder");

        service.PlayerLeft(PlayerA, 70000, output);
        service.Evaluate(7, new[] { Near(Host, 10) }, 70001, output);
        if (service.OwnerOf(7) != Host)
            problems.Add("the actor did not return to the normal rules");
        return problems;
    }

    /// <summary>While a handoff is pending the old owner stays the authority. Only the Ack, then the Commit, moves it.</summary>
    public static List<string> HandoffKeepsLastState()
    {
        var problems = new List<string>();
        var service = Service(HostFirst, PlayerA);
        var host = new OwnershipView(Host);
        var oldOwner = new OwnershipView(PlayerA);
        var output = new List<OwnershipMessage>();
        service.Track(7, new[] { Near(PlayerA, 50) }, 0, output);
        Broadcast(output, host, oldOwner);

        output.Clear();
        service.Evaluate(7, new[] { Near(Host, 10), Near(PlayerA, 50) }, 2000, output);
        Broadcast(output, host, oldOwner);
        if (output.Exists(message => message.Kind == OwnershipMessageKind.Commit))
            problems.Add("the handoff committed before the new owner acknowledged");
        if (service.OwnerOf(7) != PlayerA)
            problems.Add("the service moved the owner before the Ack");
        if (host.Check(7, PlayerA, 1) != UpdateVerdict.Accept)
            problems.Add("the old owner's last state was refused during the handoff");
        if (!host.IsHandoffIncoming(7, out var pending) || pending != 2)
            problems.Add("the new owner does not see the handoff as incoming");
        if (!oldOwner.IsHandoffOutgoing(7))
            problems.Add("the old owner does not see the handoff as outgoing");

        output.Clear();
        var ack = new OwnershipMessage(OwnershipMessageKind.Ack, 7, 2, Host, null);
        if (!service.OnAck(ack, Host, 2100, output))
            problems.Add("a valid Ack was refused");
        Broadcast(output, host, oldOwner);
        if (host.OwnerOf(7) != Host || oldOwner.OwnerOf(7) != Host)
            problems.Add("the Commit did not move the owner");
        if (oldOwner.Check(7, PlayerA, 1) == UpdateVerdict.Accept)
            problems.Add("the old owner is still accepted after the Commit");
        if (oldOwner.MayPublish(7, out _))
            problems.Add("the old owner may still publish after the Commit");
        return problems;
    }
}
