using HarmonyLib;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Actor;

/// <summary>
/// A player who holds an actor in their vacuum must simulate it wherever they carry it. The host is asked to give it
/// to them for as long as they hold it.
/// </summary>
[HarmonyPatch(typeof(Vacuumable))]
internal static class OnActorVacced
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(Vacuumable.Capture))]
    public static void CapturePostfix(Vacuumable __instance)
    {
        var identifiable = __instance._identifiable;
        if (identifiable)
            ActorOwnershipManager.OnLocalHold(identifiable.GetActorId().Value);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Vacuumable.Release))]
    public static void ReleasePostfix(Vacuumable __instance)
    {
        var identifiable = __instance._identifiable;
        if (identifiable)
            ActorOwnershipManager.OnLocalRelease(identifiable.GetActorId().Value);
    }
}
