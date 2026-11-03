using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.ActorInterest, HandlerType.Server)]
internal sealed class ActorInterestHandler : BasePacketHandler<ActorInterestPacket>
{
    protected override bool Handle(ActorInterestPacket packet, IPEndPoint? clientEp)
    {
        var playerId = Main.Server.ClientManager.GetPlayerId(clientEp);

        if (playerId != null)
            ActorOwnershipManager.OnInterest(playerId, packet.Awake, packet.Asleep);

        return false;
    }
}
