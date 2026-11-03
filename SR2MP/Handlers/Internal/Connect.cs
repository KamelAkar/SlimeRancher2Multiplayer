using System.Net;
using SR2MP.Packets;
using SR2MP.Packets.Internal;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Utils;

namespace SR2MP.Handlers.Internal;

[PacketHandler((byte)PacketType.Connect, HandlerType.Server)]
internal sealed class ConnectHandler : BasePacketHandler<ConnectPacket>
{
    protected override bool Handle(ConnectPacket packet, IPEndPoint? clientEp)
    {
        if (clientEp == null)
            return false;

        SrLogger.LogMessage(
            $"Connect request received with PlayerId: {packet.PlayerId}",
            $"Connect request from {clientEp} with PlayerId: {packet.PlayerId}");

        Main.Server.ClientManager.AddClient(clientEp, packet.PlayerId);

        if (packet.ProtocolVersion != SyncProtocol.Version)
        {
            SrLogger.LogMessage($"Refused {packet.PlayerId}: protocol {packet.ProtocolVersion}, host uses {SyncProtocol.Version}");

            var denyPacket = new ConnectionDenyPacket
            {
                Reason = "Your version of Ranching Together does not match the host's version.\n" +
                         "Everyone needs the exact same build of the mod."
            };
            Main.Server.SendToClient(denyPacket, clientEp);
            Main.Server.ClientManager.RemoveClient(clientEp);
            return false;
        }

        var informPacket = new EmptyPacket()
        {
            Type = PacketType.ModSync,
            Reliability = PacketReliability.ReliableOrdered
        };
        Main.Server.SendToClient(informPacket, clientEp);
        return false;
    }
}