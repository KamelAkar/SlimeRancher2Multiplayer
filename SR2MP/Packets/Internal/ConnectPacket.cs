using SR2MP.Packets.Utils;
using SR2MP.Shared.Utils;

namespace SR2MP.Packets.Internal;

internal sealed class ConnectPacket : IPacket
{
    public string PlayerId;
    public string Username;

    // Builds older than the protocol check send nothing here and read as version 0.
    public ushort ProtocolVersion = SyncProtocol.Version;

    public PacketType Type => PacketType.Connect;
    public PacketReliability Reliability => PacketReliability.Reliable;
    public NetworkChannel Channel => NetworkChannel.Important;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteStringWithoutSize(PlayerId);
        writer.WriteString(Username);
        writer.WriteUShort(ProtocolVersion);
    }

    public void Deserialise(PacketReader reader)
    {
        PlayerId = reader.ReadPooledStringOfSize(16)!;
        Username = reader.ReadPooledString()!;
        ProtocolVersion = reader.BytesRemaining >= sizeof(ushort) ? reader.ReadUShort() : (ushort)0;
    }
}