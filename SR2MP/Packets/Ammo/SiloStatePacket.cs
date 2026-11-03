using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Ammo;

/// <summary>
/// Host to clients: the exact content of one shared storage (silo, plort collector, feeder...).
/// The host is the only authority. Clients apply a state only if its version is newer than the last one applied.
/// </summary>
internal sealed class SiloStatePacket : IPacket
{
    public string Id = string.Empty;
    public uint Version;

    // The last operation from the receiving client that this content includes.
    public uint AckedSequence;
    public List<SiloSlotState> Slots = new();

    public PacketType Type => PacketType.SiloState;
    public PacketReliability Reliability => PacketReliability.Reliable;
    public NetworkChannel Channel => NetworkChannel.Ammo;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(Id);
        writer.WritePackedUInt(Version);
        writer.WritePackedUInt(AckedSequence);
        writer.WriteList(Slots, PacketWriterDels.NetObject<SiloSlotState>.Writer, CountType.Byte);
    }

    public void Deserialise(PacketReader reader)
    {
        Id = reader.ReadPooledString() ?? string.Empty;
        Version = reader.ReadPackedUInt();
        AckedSequence = reader.ReadPackedUInt();
        Slots = reader.ReadList(PacketReaderDels.NetObject<SiloSlotState>.Reader, CountType.Byte) ?? new List<SiloSlotState>();
    }
}

/// <summary>
/// One slot of a shared storage. <see cref="TypeId"/> is the persistent identifiable id, or -1 when the slot is empty.
/// </summary>
internal struct SiloSlotState : INetObject, IEquatable<SiloSlotState>
{
    public const int EmptyType = -1;

    public int TypeId;
    public int Count;

    public SiloSlotState(int typeId, int count)
    {
        TypeId = count > 0 ? typeId : EmptyType;
        Count = Math.Max(0, count);
    }

    public readonly bool IsEmpty => Count <= 0;

    public readonly void Serialise(PacketWriter writer)
    {
        writer.WritePackedInt(TypeId);
        writer.WritePackedInt(Count);
    }

    public void Deserialise(PacketReader reader)
    {
        TypeId = reader.ReadPackedInt();
        Count = reader.ReadPackedInt();
    }

    public readonly bool Equals(SiloSlotState other) => TypeId == other.TypeId && Count == other.Count;

    public override readonly bool Equals(object? obj) => obj is SiloSlotState other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(TypeId, Count);
}
