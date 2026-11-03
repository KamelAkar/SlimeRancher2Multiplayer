using Il2CppMonomiPark.SlimeRancher.Player;
using SR2MP.Packets.Ammo;

namespace SR2MP.Shared.Managers;

internal static partial class SiloSyncManager
{
    private static SiloSlotState[] Read(AmmoSlotManager ammo)
    {
        var slots = ammo.Slots;
        if (slots == null)
            return Array.Empty<SiloSlotState>();

        var result = new SiloSlotState[slots.Count];

        for (var i = 0; i < result.Length; i++)
        {
            var slot = slots[i];
            result[i] = slot == null || slot.Id == null || slot.Count <= 0
                ? EmptySlot
                : new SiloSlotState(NetworkActorManager.GetPersistentID(slot.Id), slot.Count);
        }

        return result;
    }

    private static void Write(AmmoSlotManager ammo, IReadOnlyList<SiloSlotState> states)
    {
        var slots = ammo.Slots;
        if (slots == null)
            return;

        var modelSlots = ammo._ammoModel?.Slots;
        var length = Math.Min(slots.Count, states.Count);

        for (var i = 0; i < length; i++)
        {
            var slot = slots[i];
            WriteSlot(slot, states[i]);

            // The model keeps its own slot objects on some storages, keep both in step.
            if (modelSlots != null && i < modelSlots.Count && slot != null && modelSlots[i]?.Pointer != slot.Pointer)
                WriteSlot(modelSlots[i], states[i]);
        }
    }

    private static void WriteSlot(AmmoSlot? slot, SiloSlotState state)
    {
        if (slot == null)
            return;

        if (state.IsEmpty || !ActorManager.ActorTypes.TryGetValue(state.TypeId, out var type) || type == null)
        {
            slot.Count = 0;
            ClearEmptySlot(slot);
            return;
        }

        slot.Id = type;
        slot.Count = state.Count;
    }

    /// <summary>
    /// The game keeps the type of a slot after it is emptied, which would stop another type from going in. Only done
    /// when the sync itself writes a slot, never in the middle of a game call that may still read the type.
    /// </summary>
    private static void ClearEmptySlot(AmmoSlot slot)
    {
        if (slot.Count <= 0 && slot._id != null)
            slot._id = null;
    }
}
