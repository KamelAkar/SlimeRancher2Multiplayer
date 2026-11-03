using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Actor;

/// <summary>
/// Old owner to everyone: the actor's last state when its ownership is being handed to another player.
/// The new owner starts from this state instead of its own copy, which may lag behind or sit at its spawn point.
/// Reliable, unlike regular position updates, because a lost one would bring back the snap-back bug.
/// </summary>
internal sealed class ActorHandoffStatePacket : IPacket
{
    public long ActorId;

    // Epoch of the old owner, the one still in charge until the handoff is committed.
    public uint Epoch;

    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Velocity;

    public PacketType Type => PacketType.ActorHandoffState;
    public PacketReliability Reliability => PacketReliability.Reliable;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(ActorId);
        writer.WritePackedUInt(Epoch);
        writer.WriteVector3(Position);
        writer.WriteQuaternion(Rotation);
        writer.WriteVector3(Velocity);
    }

    public void Deserialise(PacketReader reader)
    {
        ActorId = reader.ReadPackedLong();
        Epoch = reader.ReadPackedUInt();
        Position = reader.ReadVector3();
        Rotation = reader.ReadQuaternion();
        Velocity = reader.ReadVector3();
    }
}
