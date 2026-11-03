using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.ActorHandoffState)]
internal sealed class ActorHandoffStateHandler : BasePacketHandler<ActorHandoffStatePacket>
{
    protected override bool Handle(ActorHandoffStatePacket packet, IPEndPoint? clientEp)
    {
        if (!IsServerSide)
            return ActorOwnershipManager.OnHandoffState(packet, null);

        // The host checks that the sender really is the old owner before relaying the state to everyone else.
        var sender = Main.Server.ClientManager.GetPlayerId(clientEp);
        return sender != null && ActorOwnershipManager.OnHandoffState(packet, sender);
    }
}
