using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Ammo;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Ammo;

[PacketHandler((byte)PacketType.SiloState, HandlerType.Client)]
internal sealed class SiloStateHandler : BasePacketHandler<SiloStatePacket>
{
    protected override bool Handle(SiloStatePacket packet, IPEndPoint? clientEp)
    {
        SiloSyncManager.ApplyHostState(packet);
        return false;
    }
}
