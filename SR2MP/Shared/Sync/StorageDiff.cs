using System.Collections.Generic;

namespace SR2MP.Shared.Sync;

/// <summary>One slot of a storage: the persistent type id (-1 when empty) and the item count.</summary>
internal readonly record struct StorageSlot(int TypeId, int Count)
{
    public const int EmptyType = -1;

    public static readonly StorageSlot Empty = new(EmptyType, 0);

    public bool IsEmpty => Count <= 0;

    /// <summary>A slot with no items is always stored as the empty slot, whatever type it had.</summary>
    public static StorageSlot Of(int typeId, int count) => count > 0 ? new StorageSlot(typeId, count) : Empty;
}

/// <summary>A change to one slot: add (positive delta) or remove (negative delta) items of one type.</summary>
internal readonly record struct StorageOperation(int SlotIndex, int TypeId, int Delta);

/// <summary>
/// Turns two contents of the same storage into the operations that lead from the first to the second.
/// Replaying the operations on the first content gives exactly the second one.
/// </summary>
internal static class StorageDiff
{
    public static List<StorageOperation> Compute(IReadOnlyList<StorageSlot> previous, IReadOnlyList<StorageSlot> current)
    {
        var operations = new List<StorageOperation>();
        var length = System.Math.Max(previous.Count, current.Count);

        for (var i = 0; i < length; i++)
        {
            var before = i < previous.Count ? Normalize(previous[i]) : StorageSlot.Empty;
            var after = i < current.Count ? Normalize(current[i]) : StorageSlot.Empty;

            if (before == after)
                continue;

            if (before.TypeId == after.TypeId)
            {
                operations.Add(new StorageOperation(i, after.TypeId, after.Count - before.Count));
                continue;
            }

            if (!before.IsEmpty)
                operations.Add(new StorageOperation(i, before.TypeId, -before.Count));

            if (!after.IsEmpty)
                operations.Add(new StorageOperation(i, after.TypeId, after.Count));
        }

        return operations;
    }

    /// <summary>
    /// Replays operations the way the host applies them: an addition only goes into an empty slot or a slot of the
    /// same type, and a removal never takes more than the slot holds.
    /// </summary>
    public static StorageSlot[] Apply(IReadOnlyList<StorageSlot> content, IEnumerable<StorageOperation> operations)
    {
        var result = new StorageSlot[content.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = Normalize(content[i]);

        foreach (var operation in operations)
        {
            if (operation.SlotIndex < 0 || operation.SlotIndex >= result.Length || operation.Delta == 0)
                continue;

            var slot = result[operation.SlotIndex];

            if (operation.Delta > 0 && (slot.IsEmpty || slot.TypeId == operation.TypeId))
                result[operation.SlotIndex] = StorageSlot.Of(operation.TypeId, slot.Count + operation.Delta);
            else if (operation.Delta < 0 && !slot.IsEmpty && slot.TypeId == operation.TypeId)
                result[operation.SlotIndex] = StorageSlot.Of(slot.TypeId, System.Math.Max(0, slot.Count + operation.Delta));
        }

        return result;
    }

    private static StorageSlot Normalize(StorageSlot slot) => StorageSlot.Of(slot.TypeId, slot.Count);
}
