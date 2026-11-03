namespace SR2MP.Shared.Ownership;

internal sealed partial class OwnershipView
{
    /// <summary>
    /// Judges one incoming actor update. <paramref name="from"/> is null for an update relayed by the host with an
    /// unknown sender: the host already checked the sender, so only the epoch, the echo and the unowned cases are
    /// judged.
    /// </summary>
    public UpdateVerdict Check(long actorId, string? from, uint epoch)
    {
        var verdict = Judge(actorId, from, epoch);
        switch (verdict)
        {
            case UpdateVerdict.Accept:
                Stats.Accepted++;
                break;
            case UpdateVerdict.NotOwner:
                Stats.NotOwner++;
                break;
            case UpdateVerdict.StaleEpoch:
                Stats.StaleEpoch++;
                break;
            case UpdateVerdict.FutureEpoch:
                Stats.FutureEpoch++;
                break;
            case UpdateVerdict.NotYetActive:
                Stats.NotYetActive++;
                break;
            case UpdateVerdict.EchoOfSelf:
                Stats.Echoes++;
                break;
            default:
                Stats.Unknown++;
                break;
        }

        return verdict;
    }

    /// <summary>True while this machine owns the actor. <paramref name="epoch"/> is the epoch to stamp on the update.</summary>
    public bool MayPublish(long actorId, out uint epoch)
    {
        if (Self != null && actors.TryGetValue(actorId, out var record) && record.Owner == Self)
        {
            epoch = record.Epoch;
            return true;
        }

        epoch = 0;
        return false;
    }

    /// <summary>
    /// True while this machine is the new owner of a handoff that is not committed yet: apply the old owner's last
    /// state, then send the Ack carrying <paramref name="epoch"/>.
    /// </summary>
    public bool IsHandoffIncoming(long actorId, out uint epoch)
    {
        if (Self != null && actors.TryGetValue(actorId, out var record) && record.Pending && record.To == Self)
        {
            epoch = record.PendingEpoch;
            return true;
        }

        epoch = 0;
        return false;
    }

    /// <summary>
    /// True while this machine owns the actor and a pending handoff moves it to another player: send the final
    /// state and stop simulating it.
    /// </summary>
    public bool IsHandoffOutgoing(long actorId)
    {
        return Self != null
            && actors.TryGetValue(actorId, out var record)
            && record.Owner == Self
            && record.Pending
            && record.To != null
            && record.To != Self;
    }

    private UpdateVerdict Judge(long actorId, string? from, uint epoch)
    {
        if (!actors.TryGetValue(actorId, out var record))
            return UpdateVerdict.UnknownActor;

        var echo = from == null
            ? Self != null && record.Owner == Self && epoch == record.Epoch
            : from == Self;
        if (echo)
            return UpdateVerdict.EchoOfSelf;

        if (epoch < record.Epoch)
            return UpdateVerdict.StaleEpoch;

        if (epoch > record.Epoch)
        {
            var fromNewOwner = from == null || from == record.To;
            return record.Pending && epoch == record.PendingEpoch && fromNewOwner
                ? UpdateVerdict.NotYetActive
                : UpdateVerdict.FutureEpoch;
        }

        if (record.Owner == null || (from != null && from != record.Owner))
            return UpdateVerdict.NotOwner;

        return UpdateVerdict.Accept;
    }
}
