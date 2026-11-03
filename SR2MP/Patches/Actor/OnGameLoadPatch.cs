using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Components.Actor;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Actor;

[HarmonyPatch(typeof(SceneContext), nameof(SceneContext.Start))]
internal static class OnGameLoadPatch
{
    public static void Postfix() => Main.Server.OnServerStarted += () =>
    {
        foreach (var actor in GameState.identifiables)
        {
            if (actor.value.TryCast<ActorModel>() == null) continue;

            // Drones dont get NetworkActor
            if (NetworkDroneManager.IsDroneModel(actor.value)) continue;

            var transform = actor.value.Transform;

            if (!transform)
                continue;
            
            if (transform.GetComponent<NetworkActor>())
                continue;

            // The host simulates every actor until a player who has it loaded is assigned to it.
            var networkComponent = transform.gameObject.AddComponent<NetworkActor>();
            networkComponent.LocallyOwned = true;

            ActorManager.Actors[actor.value.actorId.Value] = actor.value;
        }

        GameState._actorIdProvider._nextActorId =
            NetworkActorManager.GetHighestActorIdInRange(0, ActorIdOffset) + 10;
    };
}