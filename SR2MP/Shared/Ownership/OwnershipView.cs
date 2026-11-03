using System.Collections.Generic;

namespace SR2MP.Shared.Ownership;

/// <summary>
/// Every machine's copy of the ownership table. Applies the host's messages (duplicated, reordered or replayed
/// messages are harmless because the epoch orders them) and judges incoming actor updates.
/// </summary>
internal sealed partial class OwnershipView
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly Dictionary<long, ViewRecord> actors = new();

    /// <summary>Creates an empty view.</summary>
    /// <param name="self">This machine's player id, or null when it is not known yet.</param>
    public OwnershipView(string? self)
    {
        Self = self;
    }

    public string? Self { get; set; }

    public ViewStats Stats { get; } = new();

    /// <summary>Applies one message from the host. Ack is a host side message and is ignored.</summary>
    public ApplyResult Apply(OwnershipMessage message)
    {
        var known = actors.TryGetValue(message.ActorId, out var record);
        var result = message.Kind switch
        {
            OwnershipMessageKind.Ack => ApplyResult.Duplicate,
            OwnershipMessageKind.Transfer => ApplyTransfer(message, known ? record : null),
            OwnershipMessageKind.Grant or OwnershipMessageKind.Commit or OwnershipMessageKind.Release
                => ApplyAssignment(message, known ? record : null),
            _ => ApplyResult.Stale,
        };
        Count(result);
        return result;
    }

    /// <summary>
    /// Records an actor that <paramref name="owner"/> just spawned, at epoch 1. Does nothing if the actor is
    /// already known (a message from the host got here first).
    /// </summary>
    public ApplyResult AdoptSpawn(long actorId, string owner)
    {
        var result = ApplyResult.Duplicate;
        if (!actors.ContainsKey(actorId))
        {
            actors.Add(actorId, new ViewRecord { Owner = owner, Epoch = 1 });
            result = ApplyResult.Applied;
        }

        Count(result);
        return result;
    }

    public string? OwnerOf(long actorId) => actors.TryGetValue(actorId, out var record) ? record.Owner : null;

    public uint EpochOf(long actorId) => actors.TryGetValue(actorId, out var record) ? record.Epoch : 0;

    public bool IsKnown(long actorId) => actors.ContainsKey(actorId);

    public void Forget(long actorId) => actors.Remove(actorId);

    public void Clear() => actors.Clear();

    /// <summary>
    /// FNV-1a 64 over (actor id, owner as UTF-8, epoch) of every owned actor in ascending id order.
    /// Equal views give equal digests. Numbers are hashed little endian on every platform.
    /// </summary>
    public ulong Digest()
    {
        var ids = new List<long>(actors.Keys);
        ids.Sort();
        var hash = FnvOffset;
        foreach (var actorId in ids)
        {
            var record = actors[actorId];
            if (record.Owner == null)
                continue;

            hash = HashLittleEndian(hash, (ulong)actorId, sizeof(long));
            foreach (var value in System.Text.Encoding.UTF8.GetBytes(record.Owner))
                hash = (hash ^ value) * FnvPrime;
            hash = HashLittleEndian(hash, record.Epoch, sizeof(uint));
        }

        return hash;
    }

    private static ulong HashLittleEndian(ulong hash, ulong value, int byteCount)
    {
        for (var i = 0; i < byteCount; i++)
            hash = (hash ^ ((value >> (8 * i)) & 0xFF)) * FnvPrime;
        return hash;
    }

    private ApplyResult ApplyTransfer(OwnershipMessage message, ViewRecord? record)
    {
        if (record != null)
        {
            if (record.Pending && message.Epoch == record.PendingEpoch && message.Owner == record.To)
                return ApplyResult.Duplicate;

            if (message.Epoch <= record.Epoch)
            {
                return message.Epoch == record.Epoch && message.Owner == record.Owner
                    ? ApplyResult.Duplicate
                    : ApplyResult.Stale;
            }

            if (record.Pending && message.Epoch < record.PendingEpoch)
                return ApplyResult.Stale;
        }
        else
        {
            // First news of this actor: the previous owner held it at the epoch before the transfer.
            record = new ViewRecord
            {
                Owner = message.PreviousOwner,
                Epoch = message.Epoch > 0 ? message.Epoch - 1 : 0,
            };
            actors.Add(message.ActorId, record);
        }

        record.Pending = true;
        record.To = message.Owner;
        record.PendingEpoch = message.Epoch;
        return ApplyResult.Applied;
    }

    private ApplyResult ApplyAssignment(OwnershipMessage message, ViewRecord? record)
    {
        var newOwner = message.Kind == OwnershipMessageKind.Release ? null : message.Owner;
        if (record != null)
        {
            if (message.Epoch < record.Epoch)
                return ApplyResult.Stale;

            if (message.Epoch == record.Epoch)
                return newOwner == record.Owner ? ApplyResult.Duplicate : ApplyResult.Stale;

            // A Commit older than the handoff already announced must not cancel it.
            if (record.Pending && message.Epoch < record.PendingEpoch && message.Kind == OwnershipMessageKind.Commit)
                return ApplyResult.Stale;
        }
        else
        {
            record = new ViewRecord();
            actors.Add(message.ActorId, record);
        }

        record.Owner = newOwner;
        record.Epoch = message.Epoch;
        record.Pending = false;
        record.To = null;
        return ApplyResult.Applied;
    }

    private void Count(ApplyResult result)
    {
        switch (result)
        {
            case ApplyResult.Applied:
                Stats.Applied++;
                break;
            case ApplyResult.Duplicate:
                Stats.Duplicates++;
                break;
            default:
                Stats.StaleMessages++;
                break;
        }
    }

    private sealed class ViewRecord
    {
        public string? Owner { get; set; }
        public uint Epoch { get; set; }
        public bool Pending { get; set; }
        public string? To { get; set; }
        public uint PendingEpoch { get; set; }
    }
}
