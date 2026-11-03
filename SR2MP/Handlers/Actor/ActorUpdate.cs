using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.ActorUpdate)]
internal sealed class ActorUpdateHandler : BasePacketHandler<ActorUpdatePacket>
{
    protected override bool Handle(ActorUpdatePacket packet, IPEndPoint? clientEp)
    {
        var actorId = packet.ActorId.Value;

        // The host knows who sent the update and only relays it if that player owns the actor at that epoch.
        // Clients receive updates the host already checked, so they only compare epochs.
        string? sender = null;
        if (IsServerSide)
        {
            sender = Main.Server.ClientManager.GetPlayerId(clientEp);
            if (sender == null)
                return false;
        }

        if (!ActorOwnershipManager.AcceptUpdate(actorId, sender, packet.Epoch))
            return false;

        if (ActorManager.Actors.TryGetValue(actorId, out var model))
        {
            var actor = model.Cast<ActorModel>();
            actor.lastPosition = packet.Position;
            actor.lastRotation = packet.Rotation;

            if (actor.TryGetNetworkComponent(out var networkComponent))
            {
                networkComponent.OnNetworkWorldUpdate(packet);

                if (networkComponent.RegionMember?._hibernating == true)
                {
                    networkComponent.transform.position = packet.Position;
                    networkComponent.transform.rotation = packet.Rotation;
                }
            }
        }

        // Relayed even when the host has no copy loaded, so a third player still gets the owner's movement.
        return true;
    }
}
