using System.Collections.Generic;

namespace SR2MP.Shared.Ownership;

internal sealed partial class OwnershipService
{
    /// <summary>
    /// Handles a client's Ack for a pending Transfer. Only the pending new owner with the pending epoch commits;
    /// anything else is ignored and returns false.
    /// </summary>
    public bool OnAck(OwnershipMessage ack, string from, long nowMs, List<OwnershipMessage> output)
    {
        if (ack.Kind != OwnershipMessageKind.Ack || !actors.TryGetValue(ack.ActorId, out var record))
            return false;

        if (!record.Pending || ack.Epoch != record.PendingEpoch || from != record.To)
            return false;

        Commit(ack.ActorId, record, nowMs, false, output);
        return true;
    }

    /// <summary>
    /// Drives handoff timeouts, call it once per host tick. A handoff older than the timeout is committed if the
    /// new owner is still connected, otherwise the actor is granted back to the old owner.
    /// </summary>
    public void Tick(long nowMs, List<OwnershipMessage> output)
    {
        foreach (var (actorId, record) in actors)
        {
            if (!record.Pending || nowMs - record.PendingSinceMs < config.HandoffTimeoutMs)
                continue;

            if (IsConnected(record.To))
            {
                Commit(actorId, record, nowMs, true, output);
            }
            else
            {
                Stats.AbortedHandoffs++;
                Grant(actorId, record, record.Owner, nowMs, output);
            }
        }
    }

    private static uint NextEpoch(ActorRecord record)
    {
        var current = record.Pending ? System.Math.Max(record.Epoch, record.PendingEpoch) : record.Epoch;
        return current + 1;
    }

    private void Grant(long actorId, ActorRecord record, string? owner, long nowMs, List<OwnershipMessage> output)
    {
        var epoch = NextEpoch(record);
        output.Add(new OwnershipMessage(OwnershipMessageKind.Grant, actorId, epoch, owner, record.Owner));
        record.Epoch = epoch;
        record.Owner = owner;
        record.ClearPending();
        record.ChangedAtMs = nowMs;
        Stats.Grants++;
    }

    private void BeginTransfer(long actorId, ActorRecord record, string to, long nowMs, List<OwnershipMessage> output)
    {
        var epoch = NextEpoch(record);
        output.Add(new OwnershipMessage(OwnershipMessageKind.Transfer, actorId, epoch, to, record.Owner));
        record.Pending = true;
        record.To = to;
        record.PendingEpoch = epoch;
        record.PendingSinceMs = nowMs;
        Stats.Transfers++;
    }

    private void Commit(long actorId, ActorRecord record, long nowMs, bool forced, List<OwnershipMessage> output)
    {
        output.Add(new OwnershipMessage(
            OwnershipMessageKind.Commit, actorId, record.PendingEpoch, record.To, record.Owner));
        record.Owner = record.To;
        record.Epoch = record.PendingEpoch;
        record.ClearPending();
        record.ChangedAtMs = nowMs;
        Stats.Commits++;
        if (forced)
            Stats.ForcedCommits++;
    }

    private void Release(long actorId, ActorRecord record, long nowMs, List<OwnershipMessage> output)
    {
        var epoch = NextEpoch(record);
        output.Add(new OwnershipMessage(OwnershipMessageKind.Release, actorId, epoch, null, record.Owner));
        record.Epoch = epoch;
        record.Owner = null;
        record.ClearPending();
        record.ChangedAtMs = nowMs;
        Stats.Releases++;
    }

    private sealed class ActorRecord
    {
        public string? Owner { get; set; }
        public uint Epoch { get; set; }
        public long ChangedAtMs { get; set; }
        public string? Holder { get; set; }
        public bool Pending { get; set; }
        public string? To { get; set; }
        public uint PendingEpoch { get; set; }
        public long PendingSinceMs { get; set; }

        public void ClearPending()
        {
            Pending = false;
            To = null;
        }
    }
}
