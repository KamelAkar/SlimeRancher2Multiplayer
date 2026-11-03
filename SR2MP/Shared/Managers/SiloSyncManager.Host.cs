using System.Collections;
using Il2CppMonomiPark.SlimeRancher.Player;
using SR2MP.Packets.Ammo;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

internal static partial class SiloSyncManager
{
    private const float HostScanInterval = 1f;

    // Host side: for each player and storage, the last operation of that player already applied.
    private static readonly Dictionary<(string PlayerId, string StorageId), uint> lastAppliedSequence = new();

    /// <summary>
    /// Host side: replays a client's change on the host's storage, then answers with the resulting content.
    /// The answer is sent even when the change was refused, so the client drops its optimistic version.
    /// </summary>
    internal static void ApplyClientOperation(SiloOperationPacket packet, string playerId)
    {
        var ammo = NetworkAmmoManager.GetAmmo(packet.Id);
        if (ammo == null)
        {
            SyncLog.Write($"Storage {packet.Id}: operation from {playerId} ignored, storage unknown on the host");
            return;
        }

        if (!mirrors.ContainsKey(packet.Id))
            Capture(packet.Id, ammo);

        var mirror = GetOrCreateMirror(packet.Id);
        var versionBefore = mirror.Version;

        // Recorded before applying: the game call broadcasts the new content from inside, and that content already
        // includes this operation, so the client must not replay it on top.
        lastAppliedSequence[(playerId, packet.Id)] = packet.Sequence;
        var accepted = TryApplyOperation(ammo, packet);

        SyncLog.Write($"Storage {packet.Id}: {(accepted ? "applied" : "refused")} #{packet.Sequence} slot {packet.SlotIndex} " +
                      $"type {packet.TypeId} delta {packet.Delta} from {playerId}");

        Flush(packet.Id, ammo);

        if (mirror.Version == versionBefore)
            Broadcast(packet.Id, mirror);
    }

    private static bool TryApplyOperation(AmmoSlotManager ammo, SiloOperationPacket packet)
    {
        var slots = ammo.Slots;
        if (slots == null || packet.SlotIndex < 0 || packet.SlotIndex >= slots.Count || packet.Delta == 0)
            return false;

        if (!ActorManager.ActorTypes.TryGetValue(packet.TypeId, out var type) || type == null)
            return false;

        var slot = slots[packet.SlotIndex];
        if (slot == null)
            return false;

        HandlingPacket = true;
        try
        {
            ClearEmptySlot(slot);

            if (packet.Delta > 0)
                return ammo.MaybeAddToSpecificSlot(new AmmoSlot.AmmoMetadata(type), packet.SlotIndex, packet.Delta, false);

            if (slot.Id != type || slot.Count <= 0)
                return false;

            ammo.Decrement(packet.SlotIndex, Math.Min(-packet.Delta, slot.Count));
            ClearEmptySlot(slot);
            return true;
        }
        finally
        {
            HandlingPacket = false;
        }
    }

    /// <summary>
    /// Sends the content to every client. Each copy says which of that client's operations it includes, so the client
    /// knows which of its own changes still have to be replayed on top.
    /// </summary>
    private static void Broadcast(string id, Mirror mirror)
    {
        mirror.Version++;
        mirror.HasVersion = true;

        if (SyncLog.Enabled)
            SyncLog.Write($"Storage {id}: broadcasting v{mirror.Version} [{Describe(mirror.Slots)}]");

        var slots = mirror.Slots.ToList();
        foreach (var client in Main.Server.ClientManager.GetAllClients())
        {
            Main.Server.SendToClient(new SiloStatePacket
            {
                Id = id,
                Version = mirror.Version,
                AckedSequence = lastAppliedSequence.GetValueOrDefault((client.PlayerId, id)),
                Slots = slots
            }, client.EndPoint);
        }
    }

    /// <summary>A player who leaves starts again from operation 1 when they come back.</summary>
    private static void ForgetPlayer(string playerId)
    {
        foreach (var key in lastAppliedSequence.Keys.Where(key => key.PlayerId == playerId).ToArray())
            lastAppliedSequence.Remove(key);
    }

    /// <summary>
    /// Safety net for game code paths that change a storage without going through a patched method.
    /// Only the host scans: a client must never turn a change it did not make into an operation.
    /// </summary>
    private static IEnumerator HostScanLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(HostScanInterval);

            if (!Main.Server.IsRunning)
                continue;

            foreach (var (id, ammo) in NetworkAmmoManager.RegisteredAmmo.ToArray())
            {
                try
                {
                    if (ammo != null)
                        Flush(id, ammo);
                }
                catch (Exception ex)
                {
                    SrLogger.LogWarning($"Storage {id}: scan failed: {ex.Message}");
                }
            }
        }
    }
}
