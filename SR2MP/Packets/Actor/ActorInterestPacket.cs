using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Actor;

/// <summary>
/// Client to host: actors that just woke up or fell asleep (hibernation) on the client's machine.
/// Only a machine where an actor is awake can simulate it, so the host picks owners among those players.
/// Sent ordered so a quick sleep then wake of the same actor is replayed in the right order.
/// </summary>
internal sealed class ActorInterestPacket : IPacket
{
    public List<long> Awake = new();
    public List<long> Asleep = new();

    public PacketType Type => PacketType.ActorInterest;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteList(Awake, PacketWriterDels.PackedLong);
        writer.WriteList(Asleep, PacketWriterDels.PackedLong);
    }

    public void Deserialise(PacketReader reader)
    {
        Awake = reader.ReadList(PacketReaderDels.PackedLong) ?? new List<long>();
        Asleep = reader.ReadList(PacketReaderDels.PackedLong) ?? new List<long>();
    }
}
