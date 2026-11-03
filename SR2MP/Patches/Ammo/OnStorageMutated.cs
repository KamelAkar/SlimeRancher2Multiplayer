using System.Reflection;
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Ammo;

/// <summary>
/// Every game method that can change the content of an ammo container. After each call the storage sync compares the
/// container with its last agreed content, so nested calls and unknown internal paths still send a change only once.
/// Containers that are not shared storages (the player's own inventory) are ignored by the sync.
/// </summary>
internal static class StorageMutationTargets
{
    internal static IEnumerable<MethodBase> Find(Type type, params string[] names)
        => AccessTools.GetDeclaredMethods(type).Where(method => names.Contains(method.Name));
}

[HarmonyPatch]
internal static class OnAmmoSlotManagerMutated
{
    public static IEnumerable<MethodBase> TargetMethods() => StorageMutationTargets.Find(typeof(AmmoSlotManager),
        nameof(AmmoSlotManager.MaybeAddToSpecificSlot),
        nameof(AmmoSlotManager.MaybeAddToAnySlot),
        nameof(AmmoSlotManager.MaybeAddResource),
        nameof(AmmoSlotManager.Decrement),
        nameof(AmmoSlotManager.Clear),
        nameof(AmmoSlotManager.ClearSelected),
        nameof(AmmoSlotManager.PopFromSelectedSlot));

    public static void Postfix(AmmoSlotManager __instance) => SiloSyncManager.OnAmmoChanged(__instance);
}

[HarmonyPatch]
internal static class OnAmmoSlotMutated
{
    public static IEnumerable<MethodBase> TargetMethods() => StorageMutationTargets.Find(typeof(AmmoSlot),
        nameof(AmmoSlot.DecrementAmmo),
        nameof(AmmoSlot.Clear),
        nameof(AmmoSlot.SetFromMetadata),
        nameof(AmmoSlot.AverageIn));

    public static void Postfix(AmmoSlot __instance) => SiloSyncManager.OnAmmoChanged(__instance);
}

[HarmonyPatch]
internal static class OnSiloStorageMutated
{
    public static IEnumerable<MethodBase> TargetMethods() => StorageMutationTargets.Find(typeof(SiloStorage),
        nameof(SiloStorage.MaybeAddToAnySlot),
        nameof(SiloStorage.MaybeAddToSpecificSlot),
        nameof(SiloStorage.MaybeAddAsResource));

    public static void Postfix(SiloStorage __instance) => SiloSyncManager.OnAmmoChanged(__instance.GetRelevantAmmo());
}
