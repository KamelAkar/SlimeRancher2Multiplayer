using SR2MP.Shared.Ownership;
using static SR2MP.Tests.Ownership.TestSupport;

namespace SR2MP.Tests.Ownership;

/// <summary>
/// Random presence churn without a network: players leave and rejoin, holders change, 15 percent of acks are lost,
/// messages are delayed, reordered and duplicated. Every step checks the invariants; at the end every view must
/// converge to the host's.
/// </summary>
internal sealed class ChurnScenario
{
    private const int TrackedCount = 6;
    private const int SpawnedCount = 2;
    private const int ActorCount = TrackedCount + SpawnedCount;
    private static readonly string[] Players = { Host, PlayerA, PlayerB };

    private readonly SplitMix rng;
    private readonly OwnershipService service;
    private readonly OwnershipView[] views;
    private readonly bool[] connected = { true, true, true };
    private readonly List<Delivery> queue = new();
    private readonly List<PendingAck> acks = new();
    private readonly Dictionary<(long Actor, uint Epoch), string?> history = new();
    private readonly Dictionary<(long Actor, uint Epoch), string> publishers = new();
    private readonly Dictionary<(int View, long Actor), uint> viewEpochs = new();
    private readonly Dictionary<long, uint> serviceEpochs = new();
    private int step;

    private ChurnScenario(ulong seed, OwnershipConfig config)
    {
        rng = new SplitMix((seed * 977) + 13);
        service = new OwnershipService(Host, config);
        service.PlayerJoined(PlayerA);
        service.PlayerJoined(PlayerB);
        views = Players.Select(player => new OwnershipView(player)).ToArray();
    }

    public List<string> Problems { get; } = new();

    public OwnershipStats Stats => service.Stats;

    public static ChurnScenario Run(ulong seed, int steps, OwnershipConfig config)
    {
        var scenario = new ChurnScenario(seed, config);
        scenario.Execute(steps, config.HandoffTimeoutMs);
        return scenario;
    }

    private void Execute(int steps, long handoffTimeoutMs)
    {
        Start();
        for (; step < steps && Problems.Count == 0; step++)
        {
            var now = step * 100L;
            MaybeChurnPlayer(now);
            var output = new List<OwnershipMessage>();
            service.Tick(now, output);
            for (long actor = 1; actor <= ActorCount; actor++)
            {
                var who = RandomPresence();
                if (rng.Chance(0.02))
                    service.SetHolder(actor, RandomHolder(), who, now, output);
                else
                    service.Evaluate(actor, who, now, output);
            }

            Emit(output);
            DeliverDue(step);
            DeliverAcks(now);
            CheckHost();
            CheckPublishers();
            CheckUpdatesAtHost();
        }

        if (Problems.Count == 0)
            Finish((step * 100L) + handoffTimeoutMs);
    }

    private void Start()
    {
        var output = new List<OwnershipMessage>();
        for (long actor = 1; actor <= TrackedCount; actor++)
        {
            PlayerPresence[] who =
            {
                Near(Host, 100 + (50 * actor)), Near(PlayerA, 90 + (10 * actor)), Near(PlayerB, 500),
            };
            service.Track(actor, who, 0, output);
        }

        Emit(output);
        for (var i = 0; i < SpawnedCount; i++)
        {
            var actor = TrackedCount + 1L + i;
            var spawner = Players[1 + i];
            service.TrackOwned(actor, spawner, 0);
            history[(actor, 1)] = spawner;
            foreach (var view in views)
                view.AdoptSpawn(actor, spawner);
        }
    }

    private void MaybeChurnPlayer(long now)
    {
        if (!rng.Chance(0.04))
            return;

        var p = rng.Range(1, 2);
        if (connected[p])
        {
            connected[p] = false;
            var output = new List<OwnershipMessage>();
            service.PlayerLeft(Players[p], now, output);
            Emit(output);
            return;
        }

        connected[p] = true;
        views[p].Clear();
        foreach (var key in viewEpochs.Keys.Where(key => key.View == p).ToList())
            viewEpochs.Remove(key);
        service.PlayerJoined(Players[p]);
        var snapshot = new List<OwnershipMessage>();
        service.Snapshot(snapshot);
        queue.AddRange(snapshot.Select(message => new Delivery(p, message, step + 1)));
    }

    private PlayerPresence[] RandomPresence() =>
        Enumerable.Range(0, Players.Length)
            .Where(p => connected[p])
            .Select(p => new PlayerPresence(Players[p], true, rng.Chance(0.8), rng.Range(10, 3000)))
            .ToArray();

    private string? RandomHolder()
    {
        var index = rng.Range(0, Players.Length);
        return index < Players.Length ? Players[index] : null;
    }

    private void Emit(List<OwnershipMessage> output)
    {
        foreach (var message in output)
        {
            ApplyChecked(0, message);
            if (message.Kind != OwnershipMessageKind.Transfer)
            {
                var owner = message.Kind == OwnershipMessageKind.Release ? null : message.Owner;
                var key = (message.ActorId, message.Epoch);
                if (history.TryGetValue(key, out var known) && known != owner)
                    Problems.Add($"two owners at epoch {message.Epoch} for actor {message.ActorId}");
                history[key] = owner;
            }

            for (var p = 1; p < Players.Length; p++)
            {
                queue.Add(new Delivery(p, message, step + rng.Range(0, 4)));
                if (rng.Chance(0.2))
                    queue.Add(new Delivery(p, message, step + rng.Range(0, 9)));
            }

            if (message.Kind == OwnershipMessageKind.Transfer && message.Owner == Host)
                acks.Add(new PendingAck(message, Host, step));
        }
    }

    private void ApplyChecked(int viewIndex, OwnershipMessage message)
    {
        var view = views[viewIndex];
        var knownBefore = view.IsKnown(message.ActorId);
        var epochBefore = view.EpochOf(message.ActorId);
        if (view.Apply(message) == ApplyResult.Applied && knownBefore && message.Epoch < epochBefore)
            Problems.Add($"view {viewIndex} applied epoch {message.Epoch} over {epochBefore}");

        var key = (viewIndex, message.ActorId);
        var epoch = view.EpochOf(message.ActorId);
        if (viewEpochs.TryGetValue(key, out var last) && epoch < last)
            Problems.Add($"epoch went backwards in view {viewIndex} for actor {message.ActorId}");
        if (view.IsKnown(message.ActorId))
            viewEpochs[key] = epoch;
    }

    private void DeliverDue(int currentStep)
    {
        var due = queue.Where(delivery => delivery.At <= currentStep).ToList();
        queue.RemoveAll(delivery => delivery.At <= currentStep);
        rng.Shuffle(due);
        foreach (var delivery in due.Where(delivery => connected[delivery.To]))
        {
            ApplyChecked(delivery.To, delivery.Message);
            var message = delivery.Message;
            if (message.Kind == OwnershipMessageKind.Transfer && message.Owner == Players[delivery.To] && !rng.Chance(0.15))
                acks.Add(new PendingAck(message, Players[delivery.To], currentStep + rng.Range(0, 3)));
        }
    }

    private void DeliverAcks(long now)
    {
        var due = acks.Where(ack => ack.At <= step).ToList();
        acks.RemoveAll(ack => ack.At <= step);
        var output = new List<OwnershipMessage>();
        foreach (var ack in due)
            service.OnAck(AckFor(ack.Transfer), ack.From, now, output);
        Emit(output);
    }

    private void CheckHost()
    {
        for (long actor = 1; actor <= ActorCount; actor++)
        {
            var epoch = service.EpochOf(actor);
            if (serviceEpochs.TryGetValue(actor, out var last) && epoch < last)
                Problems.Add($"service epoch went backwards for actor {actor}");
            serviceEpochs[actor] = epoch;
            if (views[0].OwnerOf(actor) != service.OwnerOf(actor) || views[0].EpochOf(actor) != epoch)
                Problems.Add($"host view differs from the service for actor {actor}");
        }
    }

    /// <summary>No two machines may ever publish one actor at the same epoch, and only its committed owner may.</summary>
    private void CheckPublishers()
    {
        for (var p = 0; p < Players.Length; p++)
        {
            if (!connected[p])
                continue;

            for (long actor = 1; actor <= ActorCount; actor++)
            {
                if (!views[p].MayPublish(actor, out var epoch))
                    continue;

                if (publishers.TryGetValue((actor, epoch), out var other) && other != Players[p])
                    Problems.Add($"{other} and {Players[p]} both published actor {actor} at epoch {epoch}");
                publishers[(actor, epoch)] = Players[p];
                if (!history.TryGetValue((actor, epoch), out var owner) || owner != Players[p])
                    Problems.Add($"{Players[p]} published actor {actor} at epoch {epoch} without owning it");
            }
        }
    }

    private void CheckUpdatesAtHost()
    {
        for (var k = 0; k < 4; k++)
        {
            var actor = (long)rng.Range(1, ActorCount);
            var from = rng.Chance(0.25) ? null : Players[rng.Range(0, Players.Length - 1)];
            var epoch = service.EpochOf(actor);
            if (rng.Chance(0.5))
                epoch = (uint)rng.Range(0, (int)epoch + 1);
            var owner = service.OwnerOf(actor);
            var valid = epoch == service.EpochOf(actor) && owner != null && (from == null || from == owner);
            if (views[0].Check(actor, from, epoch) == UpdateVerdict.Accept && !valid)
                Problems.Add($"accepted a stale or non-owner update for actor {actor}");
        }
    }

    /// <summary>Force every pending handoff to an end, deliver everything still queued, then compare all views.</summary>
    private void Finish(long late)
    {
        var output = new List<OwnershipMessage>();
        service.Tick(late, output);
        Emit(output);
        var rest = queue.ToList();
        queue.Clear();
        rng.Shuffle(rest);
        foreach (var delivery in rest.Where(delivery => connected[delivery.To]))
            ApplyChecked(delivery.To, delivery.Message);

        var expected = views[0].Digest();
        for (long actor = 1; actor <= ActorCount; actor++)
        {
            if (service.IsHandoffPending(actor))
                Problems.Add($"handoff still pending for actor {actor} after the timeout");
        }

        for (var p = 1; p < Players.Length; p++)
        {
            if (!connected[p])
                continue;

            if (views[p].Digest() != expected)
                Problems.Add($"digest of {Players[p]} differs from the host");
            for (long actor = 1; actor <= ActorCount; actor++)
            {
                if (views[p].OwnerOf(actor) != service.OwnerOf(actor) || views[p].EpochOf(actor) != service.EpochOf(actor))
                    Problems.Add($"view of {Players[p]} differs from the host for actor {actor}");
            }
        }
    }

    private readonly record struct Delivery(int To, OwnershipMessage Message, int At);

    private readonly record struct PendingAck(OwnershipMessage Transfer, string From, int At);
}
