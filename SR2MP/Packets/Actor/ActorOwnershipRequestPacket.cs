using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Actor;

internal enum ActorOwnershipRequestKind : byte
{
    /// <summary>The new owner of a handoff applied the old owner's last state and is ready.</summary>
    Ack = 1,

    /// <summary>The player holds the actor in their vacuum: they must simulate it wherever it goes.</summary>
    Hold = 2,

    /// <summary>The player let go of the actor: normal ownership rules apply again.</summary>
    ReleaseHold = 3
}

/// <summary>Client to host: a request about the ownership of one actor. Only the host decides.</summary>
internal sealed class ActorOwnershipRequestPacket : IPacket
{
    public ActorOwnershipRequestKind Kind;
    public long ActorId;
    public uint Epoch;

    public PacketType Type => PacketType.ActorOwnershipRequest;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteByte((byte)Kind);
        writer.WritePackedLong(ActorId);
        writer.WritePackedUInt(Epoch);
    }

    public void Deserialise(PacketReader reader)
    {
        Kind = (ActorOwnershipRequestKind)reader.ReadByte();
        ActorId = reader.ReadPackedLong();
        Epoch = reader.ReadPackedUInt();
    }
}
