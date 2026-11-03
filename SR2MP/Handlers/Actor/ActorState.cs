using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Slime;
using SR2MP.Components.Actor;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.ActorState)]
internal sealed class ActorStateHandler : BasePacketHandler<ActorStatePacket>
{
    protected override bool Handle(ActorStatePacket packet, IPEndPoint? clientEp)
    {
        // Same rule as positions: only the owner at the current epoch may change the actor's state.
        var sender = IsServerSide ? Main.Server.ClientManager.GetPlayerId(clientEp) : null;
        if ((IsServerSide && sender == null) || !ActorOwnershipManager.AcceptUpdate(packet.ActorId.Value, sender, packet.Epoch))
            return false;

        if (!ActorManager.Actors.TryGetValue(packet.ActorId.Value, out var model))
            return true;

        var actor = model.Cast<ActorModel>();

        if (!actor.TryGetNetworkComponent(out var networkComponent))
            return true;

        var actorId = packet.ActorId;

        SlimeModel?   slime    = null;
        ProduceModel? resource = null;

        if (actorId.Value != 0 && GameState.identifiables.TryGetValue(actorId, out var identModel))
        {
            if (identModel == null)
                SrLogger.LogWarning("IdentifiableModel is null in state handler!");

            slime    = identModel!.TryCast<SlimeModel>();
            resource = identModel.TryCast<ProduceModel>();
        }

        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (packet.UpdateType)
        {
            case ActorUpdateType.Slime when slime != null:
            {
                slime.isSleeping = packet.Sleeping;

                var slimeEmotions = networkComponent.GetComponent<SlimeEmotions>();
                if (slimeEmotions)
                    slimeEmotions.SetAll(packet.Emotions);
                break;
            }

            case ActorUpdateType.Resource when resource != null:
            {
                resource.state = packet.ResourceState;
                
                if (!NetworkActor.IsResourceFrozen(packet.ResourceProgress))
                    resource.progressTime = packet.ResourceProgress;

                networkComponent.SetResourceState(packet.ResourceState, packet.ResourceProgress, true);
                networkComponent.ApplyResourceScale(packet.ResourceScale);
                break;
            }
        }

        return true;
    }
}