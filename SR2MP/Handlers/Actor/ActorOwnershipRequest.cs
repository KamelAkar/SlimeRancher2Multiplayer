using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.ActorOwnershipRequest, HandlerType.Server)]
internal sealed class ActorOwnershipRequestHandler : BasePacketHandler<ActorOwnershipRequestPacket>
{
    protected override bool Handle(ActorOwnershipRequestPacket packet, IPEndPoint? clientEp)
    {
        var playerId = Main.Server.ClientManager.GetPlayerId(clientEp);

        if (playerId != null)
            ActorOwnershipManager.HandleRequest(playerId, packet.Kind, packet.ActorId, packet.Epoch);

        return false;
    }
}
