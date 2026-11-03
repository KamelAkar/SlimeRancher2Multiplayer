using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Ownership;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Handoff: when the host moves an actor from one machine to another, the old owner stops simulating it and sends
/// its last state, the new owner starts from that state and acknowledges, then the host commits the change. Without
/// this the new owner restarted the actor from its own stale copy, which is the freeze then snap back players saw.
/// If the state never comes (old owner gone), the host commits anyway after its timeout.
/// </summary>
internal static partial class ActorOwnershipManager
{
    // A last state that arrived before the transfer it belongs to.
    private static readonly Dictionary<long, ActorHandoffStatePacket> pendingHandoffStates = new();

    private static void OnTransferApplied(OwnershipMessage transfer)
    {
        if (transfer.PreviousOwner == LocalID)
        {
            SendHandoffState(transfer.ActorId);
            KeepHeldActor(transfer.ActorId);
        }

        if (transfer.Owner == LocalID
            && pendingHandoffStates.TryGetValue(transfer.ActorId, out var state)
            && state.Epoch == view.EpochOf(transfer.ActorId))
        {
            ApplyHandoffState(state);
            AcknowledgeHandoff(state.ActorId);
        }
    }

    private static void SendHandoffState(long actorId)
    {
        var packet = new ActorHandoffStatePacket { ActorId = actorId, Epoch = view.EpochOf(actorId) };

        if (liveActors.TryGetValue(actorId, out var actor) && actor)
        {
            packet.Position = actor.transform.position;
            packet.Rotation = actor.transform.rotation;
            packet.Velocity = actor.CurrentVelocity;

            // If the host cancels the handoff, this machine resumes from the state it handed over.
            actor.RememberVelocity(packet.Velocity);
        }
        else if (ActorManager.Actors.TryGetValue(actorId, out var model) && model?.TryCast<ActorModel>() is { } actorModel)
        {
            // Not loaded here any more (zone change): the model still has where the actor stopped.
            packet.Position = actorModel.lastPosition;
            packet.Rotation = actorModel.lastRotation;
        }
        else
        {
            return;
        }

        SyncLog.Write($"Actor {actorId}: sending handoff state at epoch {packet.Epoch}");

        if (IsHost)
            Main.Server.SendToAll(packet);
        else
            Main.Client.SendPacket(packet);
    }

    /// <summary>
    /// Applies an old owner's last state. Returns true if the state is valid, so the host relays it to the others.
    /// <paramref name="sender"/> is known on the host only.
    /// </summary>
    internal static bool OnHandoffState(ActorHandoffStatePacket packet, string? sender)
    {
        var verdict = view.Check(packet.ActorId, sender, packet.Epoch);

        // A client can get the state before the ownership packet that introduces the actor: keep it for the transfer,
        // which checks its epoch before using it. The host knows every actor, so it never keeps an unchecked state.
        if (verdict == UpdateVerdict.UnknownActor && sender == null)
        {
            pendingHandoffStates[packet.ActorId] = packet;
            return false;
        }

        if (verdict != UpdateVerdict.Accept)
            return false;

        ApplyHandoffState(packet);

        if (view.IsHandoffIncoming(packet.ActorId, out _))
            AcknowledgeHandoff(packet.ActorId);
        else
            pendingHandoffStates[packet.ActorId] = packet;

        return true;
    }

    private static void ApplyHandoffState(ActorHandoffStatePacket state)
    {
        if (liveActors.TryGetValue(state.ActorId, out var actor) && actor && !actor.LocallyOwned)
            actor.ApplyHandoffState(state.Position, state.Rotation, state.Velocity);
    }

    private static void AcknowledgeHandoff(long actorId)
    {
        pendingHandoffStates.Remove(actorId);

        if (view.IsHandoffIncoming(actorId, out var newEpoch))
            SendRequest(ActorOwnershipRequestKind.Ack, actorId, newEpoch);
    }
}
