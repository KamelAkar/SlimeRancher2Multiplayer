using SR2MP.Packets.Utils;
using SR2MP.Shared.Ownership;

namespace SR2MP.Packets.Actor;

/// <summary>
/// Host to clients: ownership decisions (grant, transfer, commit, release), each stamped with an epoch.
/// Every machine applies them to its <see cref="OwnershipView"/>; duplicates and old messages are ignored there.
/// </summary>
internal sealed class ActorOwnershipPacket : IPacket
{
    public List<OwnershipMessage> Messages = new();

    // The host refused the receiver's vacuum hold on these actors: its vacuum has to let go.
    public bool HoldDenied;

    public PacketType Type => PacketType.ActorOwnership;
    public PacketReliability Reliability => PacketReliability.Reliable;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteBool(HoldDenied);
        writer.WritePackedInt(Messages.Count);

        foreach (var message in Messages)
        {
            writer.WriteByte((byte)message.Kind);
            writer.WritePackedLong(message.ActorId);
            writer.WritePackedUInt(message.Epoch);
            writer.WriteString(message.Owner);
            writer.WriteString(message.PreviousOwner);
        }
    }

    public void Deserialise(PacketReader reader)
    {
        HoldDenied = reader.ReadBool();
        var count = reader.ReadPackedInt();
        Messages = new List<OwnershipMessage>(Math.Max(0, count));

        for (var i = 0; i < count; i++)
        {
            var kind = (OwnershipMessageKind)reader.ReadByte();
            var actorId = reader.ReadPackedLong();
            var epoch = reader.ReadPackedUInt();
            var owner = reader.ReadPooledString(returnNullOnZero: true);
            var previousOwner = reader.ReadPooledString(returnNullOnZero: true);

            Messages.Add(new OwnershipMessage(kind, actorId, epoch, owner, previousOwner));
        }
    }
}
