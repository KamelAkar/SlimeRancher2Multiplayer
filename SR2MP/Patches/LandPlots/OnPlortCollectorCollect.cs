using HarmonyLib;
using SR2MP.Components.Actor;

namespace SR2MP.Patches.LandPlots;

/// <summary>
/// A collection runs on every machine (the start is replicated), and each collector pulls the plorts around it with
/// joints, then stores and destroys them. Without a filter every machine stored its own copy of the same plort, so the
/// collector counted it once per player. A collector now only keeps the plorts this machine simulates: each plort is
/// collected by exactly one machine, whose storage change then reaches the others through the storage sync.
/// </summary>
[HarmonyPatch(typeof(PlortCollector), nameof(PlortCollector.DoCollection))]
internal static class OnPlortCollectorCollect
{
    public static void Postfix(PlortCollector __instance)
    {
        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
            return;

        var joints = __instance._joints;
        if (joints == null)
            return;

        for (var i = joints.Count - 1; i >= 0; i--)
        {
            var reference = joints[i];
            var vacuumable = reference?.Vacuumable;
            var networkActor = vacuumable ? vacuumable!.GetComponent<NetworkActor>() : null;

            if (!networkActor || networkActor!.LocallyOwned)
                continue;

            reference!.Destroy();
            joints.RemoveAt(i);
        }
    }
}
