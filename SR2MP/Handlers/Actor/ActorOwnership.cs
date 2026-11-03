using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.ActorOwnership, HandlerType.Client)]
internal sealed class ActorOwnershipHandler : BasePacketHandler<ActorOwnershipPacket>
{
    protected override bool Handle(ActorOwnershipPacket packet, IPEndPoint? clientEp)
    {
        ActorOwnershipManager.ApplyMessages(packet.Messages, packet.HoldDenied);
        return false;
    }
}
