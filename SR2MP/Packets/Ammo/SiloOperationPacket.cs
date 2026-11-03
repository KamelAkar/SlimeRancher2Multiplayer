using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Ammo;

/// <summary>
/// Client to host: a change the client made to a shared storage. Positive <see cref="Delta"/> adds items of
/// <see cref="TypeId"/> to the slot, negative removes them. The host applies it to its own storage and answers with
/// the resulting <see cref="SiloStatePacket"/>. Sent ordered so the host replays a client's changes in the same order.
/// </summary>
internal sealed class SiloOperationPacket : IPacket
{
    public string Id = string.Empty;
    public int SlotIndex;
    public int TypeId;
    public int Delta;

    // Rises with every operation a client sends in a session, so host answers can say which ones they include.
    public uint Sequence;

    public PacketType Type => PacketType.SiloOperation;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.Ammo;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(Id);
        writer.WritePackedInt(SlotIndex);
        writer.WritePackedInt(TypeId);
        writer.WritePackedInt(Delta);
        writer.WritePackedUInt(Sequence);
    }

    public void Deserialise(PacketReader reader)
    {
        Id = reader.ReadPooledString() ?? string.Empty;
        SlotIndex = reader.ReadPackedInt();
        TypeId = reader.ReadPackedInt();
        Delta = reader.ReadPackedInt();
        Sequence = reader.ReadPackedUInt();
    }
}
