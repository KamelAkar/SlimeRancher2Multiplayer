// Actor ownership with epochs.
// Ported from coop-anything tools/ownership (MIT, Copyright (c) 2026 cranebeaker and contributors),
// https://github.com/cranebeaker/coop-anything
//
// Every live actor is simulated by exactly one machine, its owner. Ownership carries an epoch that rises on every
// change, and only the owner at the current epoch may publish the actor's state.
// OwnershipService runs on the host only: it decides who owns what and emits messages for the caller to broadcast.
// OwnershipView runs on every machine (the host too): it applies those messages and judges incoming updates.
// Nothing here sends, reads a clock or is thread safe: time is passed in as nowMs.

using System.Collections.Generic;

namespace SR2MP.Shared.Ownership;

/// <summary>Host side ownership arbiter.</summary>
internal sealed partial class OwnershipService
{
    private readonly string hostId;
    private readonly OwnershipConfig config;
    private readonly HashSet<string> connected = new(System.StringComparer.Ordinal);
    private readonly SortedDictionary<long, ActorRecord> actors = new();

    /// <summary>Creates the service. The host counts as connected from the start.</summary>
    public OwnershipService(string hostId, OwnershipConfig? config = null)
    {
        System.ArgumentNullException.ThrowIfNull(hostId);
        this.hostId = hostId;
        this.config = config ?? new OwnershipConfig();
        connected.Add(hostId);
    }

    public OwnershipStats Stats { get; } = new();

    public IReadOnlyCollection<long> TrackedActors => actors.Keys;

    public void PlayerJoined(string playerId) => connected.Add(playerId);

    /// <summary>
    /// Marks the player gone. Actors they owned are released and returned so the caller can evaluate them again.
    /// A handoff to them is cancelled, a handoff from them is committed, and holder links to them are cleared.
    /// </summary>
    public List<long> PlayerLeft(string playerId, long nowMs, List<OwnershipMessage> output)
    {
        connected.Remove(playerId);
        var orphans = new List<long>();
        foreach (var (actorId, record) in actors)
        {
            if (record.Holder == playerId)
                record.Holder = null;

            if (record.Pending)
            {
                if (record.To == playerId)
                {
                    Stats.AbortedHandoffs++;
                    if (record.Owner != playerId && IsConnected(record.Owner))
                    {
                        Grant(actorId, record, record.Owner, nowMs, output);
                        continue;
                    }

                    Release(actorId, record, nowMs, output);
                    orphans.Add(actorId);
                }
                else if (record.Owner == playerId)
                {
                    Commit(actorId, record, nowMs, true, output);
                }

                continue;
            }

            if (record.Owner == playerId)
            {
                Release(actorId, record, nowMs, output);
                orphans.Add(actorId);
            }
        }

        return orphans;
    }

    /// <summary>Starts tracking an actor and picks its first owner (a Grant if anyone is eligible).</summary>
    public void Track(long actorId, IReadOnlyList<PlayerPresence> who, long nowMs, List<OwnershipMessage> output)
    {
        actors.TryAdd(actorId, new ActorRecord());
        Evaluate(actorId, who, nowMs, output);
    }

    /// <summary>
    /// Starts tracking an actor that <paramref name="owner"/> just spawned, at epoch 1, without a message:
    /// every machine derives the same record from the spawn itself. Does nothing if the actor is tracked.
    /// </summary>
    public void TrackOwned(long actorId, string owner, long nowMs)
    {
        actors.TryAdd(actorId, new ActorRecord { Owner = owner, Epoch = 1, ChangedAtMs = nowMs });
    }

    /// <summary>Stops tracking an actor. A Release is emitted if it had an owner or a pending handoff.</summary>
    public void Untrack(long actorId, List<OwnershipMessage> output)
    {
        if (!actors.TryGetValue(actorId, out var record))
            return;

        if (record.Owner != null || record.Pending)
            Release(actorId, record, 0, output);

        actors.Remove(actorId);
    }

    /// <summary>Decides one actor again from fresh presence data. Nothing is emitted when nothing changes.</summary>
    public void Evaluate(long actorId, IReadOnlyList<PlayerPresence> who, long nowMs, List<OwnershipMessage> output)
    {
        if (!actors.TryGetValue(actorId, out var record) || record.Pending)
            return;

        string? target;
        if (record.Holder != null && IsConnected(record.Holder))
        {
            target = record.Holder;
        }
        else
        {
            var held = record.Owner != null && nowMs - record.ChangedAtMs < config.MinHoldMs;
            target = OwnerPicker.Pick(who, record.Owner, held, hostId, config);
        }

        if (target == record.Owner)
            return;

        if (target == null)
        {
            Release(actorId, record, nowMs, output);
            return;
        }

        if (record.Owner == null || !IsConnected(record.Owner))
        {
            Grant(actorId, record, target, nowMs, output);
            return;
        }

        BeginTransfer(actorId, record, target, nowMs, output);
    }

    /// <summary>
    /// Sets the player holding the actor (null clears it). A connected holder owns the actor wherever it goes,
    /// without hold time or eligibility test.
    /// </summary>
    public void SetHolder(
        long actorId,
        string? holder,
        IReadOnlyList<PlayerPresence> who,
        long nowMs,
        List<OwnershipMessage> output)
    {
        if (!actors.TryGetValue(actorId, out var record))
            return;

        record.Holder = holder;
        Evaluate(actorId, who, nowMs, output);
    }

    /// <summary>The current assignment as Grant and Release messages, for a joining machine. Never owned actors are skipped.</summary>
    public void Snapshot(List<OwnershipMessage> output)
    {
        foreach (var (actorId, record) in actors)
        {
            if (record.Epoch == 0 && record.Owner == null)
                continue;

            output.Add(CurrentMessage(actorId, record));
        }
    }

    /// <summary>The current assignment of one actor, for example to correct a machine that is behind.</summary>
    public bool TryGetCurrent(long actorId, out OwnershipMessage message)
    {
        if (!actors.TryGetValue(actorId, out var record))
        {
            message = default;
            return false;
        }

        message = CurrentMessage(actorId, record);
        return true;
    }

    public string? OwnerOf(long actorId) => actors.TryGetValue(actorId, out var record) ? record.Owner : null;

    public uint EpochOf(long actorId) => actors.TryGetValue(actorId, out var record) ? record.Epoch : 0;

    public bool IsHandoffPending(long actorId) => actors.TryGetValue(actorId, out var record) && record.Pending;

    public bool IsTracked(long actorId) => actors.ContainsKey(actorId);

    public string? HolderOf(long actorId) => actors.TryGetValue(actorId, out var record) ? record.Holder : null;

    private static OwnershipMessage CurrentMessage(long actorId, ActorRecord record)
    {
        var kind = record.Owner == null ? OwnershipMessageKind.Release : OwnershipMessageKind.Grant;
        return new OwnershipMessage(kind, actorId, record.Epoch, record.Owner, null);
    }

    private bool IsConnected(string? playerId) => playerId != null && connected.Contains(playerId);
}
