using System.Collections;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Ownership;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

internal static partial class ActorOwnershipManager
{
    private const float HostTickInterval = 0.2f;
    private const int FullPassEveryTicks = 5;

    // For each actor, the players on whose machine it is loaded and not hibernating.
    private static readonly Dictionary<long, HashSet<string>> awakeBy = new();
    private static readonly HashSet<long> dirtyActors = new();
    private static readonly List<OwnershipMessage> outgoing = new();

    /// <summary>Host side: a player reported actors that woke up or fell asleep on their machine.</summary>
    internal static void OnInterest(string playerId, IEnumerable<long> awake, IEnumerable<long> asleep)
    {
        if (service == null)
            return;

        foreach (var id in awake)
        {
            if (ActorExists(id))
                MarkAwake(playerId, id);
        }

        foreach (var id in asleep)
        {
            if (awakeBy.TryGetValue(id, out var players) && players.Remove(playerId) && players.Count == 0)
                awakeBy.Remove(id);

            dirtyActors.Add(id);
        }
    }

    private static void MarkAwake(string playerId, long actorId)
    {
        if (!awakeBy.TryGetValue(actorId, out var players))
        {
            players = new HashSet<string>();
            awakeBy[actorId] = players;
        }

        players.Add(playerId);
        dirtyActors.Add(actorId);
    }

    // Ids come from clients: only actors that exist in the host's world are tracked.
    private static bool ActorExists(long actorId) => GameState.TryGetIdentifiableModel(new ActorId(actorId), out _);

    /// <summary>Host side: a request from a player (the host's own requests come here directly).</summary>
    internal static void HandleRequest(string playerId, ActorOwnershipRequestKind kind, long actorId, uint epoch)
    {
        if (service == null)
            return;

        var now = NowMs;
        outgoing.Clear();

        switch (kind)
        {
            case ActorOwnershipRequestKind.Ack:
                service.OnAck(new OwnershipMessage(OwnershipMessageKind.Ack, actorId, epoch, playerId, null), playerId, now, outgoing);
                break;

            case ActorOwnershipRequestKind.Hold:
                HandleHold(playerId, actorId, now);
                break;

            case ActorOwnershipRequestKind.ReleaseHold:
                if (service.HolderOf(actorId) == playerId)
                    service.SetHolder(actorId, null, BuildPresence(actorId), now, outgoing);
                break;
        }

        Broadcast();
    }

    private static void HandleHold(string playerId, long actorId, long now)
    {
        if (!ActorExists(actorId))
            return;

        if (!service!.IsTracked(actorId))
            TrackAsHostOwned(actorId, now);

        var holder = service.HolderOf(actorId);
        if (holder != null && holder != playerId && IsConnected(holder))
        {
            SendCurrentAssignment(playerId, actorId);
            return;
        }

        // The holder has the actor in hand, so it is loaded on their machine.
        MarkAwake(playerId, actorId);
        service.SetHolder(actorId, playerId, BuildPresence(actorId), now, outgoing);
    }

    private static IEnumerator HostLoop()
    {
        var tick = 0;

        while (true)
        {
            yield return new WaitForSeconds(HostTickInterval);

            if (service == null || !IsHost)
                continue;

            try
            {
                RunHostTick(++tick % FullPassEveryTicks == 0);
            }
            catch (Exception ex)
            {
                SrLogger.LogWarning($"Ownership host tick failed: {ex.Message}");
            }
        }
    }

    private static void RunHostTick(bool fullPass)
    {
        var now = NowMs;
        outgoing.Clear();
        CachePlayerPositions();
        service!.Tick(now, outgoing);

        // Distance only matters when several players could own the same actor.
        if (fullPass)
        {
            foreach (var (id, players) in awakeBy)
            {
                if (players.Count > 1)
                    dirtyActors.Add(id);
            }
        }

        foreach (var id in dirtyActors)
            EvaluateActor(id, now);

        dirtyActors.Clear();
        Broadcast();
    }

    private static void EvaluateActor(long actorId, long now)
    {
        var who = BuildPresence(actorId);

        if (service!.IsTracked(actorId))
        {
            service.Evaluate(actorId, who, now, outgoing);
            return;
        }

        if (!who.Exists(player => player.Eligible))
            return;

        // The host has been simulating this actor so far: hand it over with its state instead of a cold grant.
        if (liveActors.ContainsKey(actorId))
        {
            TrackAsHostOwned(actorId, now);
            service.Evaluate(actorId, who, now, outgoing);
            return;
        }

        service.Track(actorId, who, now, outgoing);
    }

    /// <summary>
    /// Starts tracking an actor the host has been simulating on its own. The grant tells clients about it, otherwise
    /// they would drop the host's movement for it as coming from an unknown actor.
    /// </summary>
    private static void TrackAsHostOwned(long actorId, long now)
    {
        service!.TrackOwned(actorId, LocalID, now);
        outgoing.Add(new OwnershipMessage(OwnershipMessageKind.Grant, actorId, service.EpochOf(actorId), LocalID, null));
    }

    private static void ForgetOnHost(long actorId)
    {
        awakeBy.Remove(actorId);
        dirtyActors.Remove(actorId);
        service?.Untrack(actorId, new List<OwnershipMessage>());
    }

    /// <summary>
    /// Sends the service's new decisions to every client, then applies them on the host. Sending first matters: when
    /// the host is the old owner of a transfer, applying it sends the last state, which must not reach a client
    /// before the transfer it belongs to.
    /// </summary>
    private static void Broadcast()
    {
        if (outgoing.Count == 0)
            return;

        var messages = outgoing.ToList();
        outgoing.Clear();

        foreach (var chunk in messages.Chunk(MaxMessagesPerPacket))
            Main.Server.SendToAll(new ActorOwnershipPacket { Messages = chunk.ToList() });

        ApplyMessages(messages);

        // An actor that changed hands while someone holds it must go to the holder as soon as the handoff ends.
        foreach (var message in messages)
        {
            var holder = service?.HolderOf(message.ActorId);
            if (holder != null && message.Owner != holder && message.Kind != OwnershipMessageKind.Transfer)
                dirtyActors.Add(message.ActorId);
        }
    }
}
