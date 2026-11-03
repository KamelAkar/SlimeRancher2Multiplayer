using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Ammo;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Ammo;

[PacketHandler((byte)PacketType.SiloOperation, HandlerType.Server)]
internal sealed class SiloOperationHandler : BasePacketHandler<SiloOperationPacket>
{
    protected override bool Handle(SiloOperationPacket packet, IPEndPoint? clientEp)
    {
        var playerId = Main.Server.ClientManager.GetPlayerId(clientEp);

        if (playerId != null)
            SiloSyncManager.ApplyClientOperation(packet, playerId);

        return false;
    }
}
