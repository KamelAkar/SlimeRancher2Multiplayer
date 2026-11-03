using HarmonyLib;
using SR2MP.Components.Actor;

namespace SR2MP.Patches.Ammo;

/// <summary>
/// Every machine has a copy of every item. Only the machine that simulates an item may let a storage catch it,
/// otherwise the same item is stored once per machine and duplicated.
/// </summary>
[HarmonyPatch(typeof(SiloCatcher))]
internal static class OnSiloCatcherTrigger
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(SiloCatcher.OnTriggerEnter))]
    public static bool EnterPrefix(Collider collider) => MayCatch(collider);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SiloCatcher.OnTriggerStay))]
    public static bool StayPrefix(Collider collider) => MayCatch(collider);

    private static bool MayCatch(Collider? collider)
    {
        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
            return true;

        if (!collider)
            return true;

        var body = collider!.attachedRigidbody;
        var networkActor = body
            ? body.GetComponent<NetworkActor>()
            : collider.GetComponentInParent<NetworkActor>();

        return !networkActor || networkActor.LocallyOwned;
    }
}
