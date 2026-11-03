using Il2CppMonomiPark.SlimeRancher.Player;
using SR2MP.Packets.Ammo;
using SR2MP.Shared.Sync;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Syncs shared storages (silos, plot collectors, feeders, warp depots) with the host as the only authority.
///
/// Every machine keeps a mirror of each storage: the content it expects. After any game call that may change a
/// storage, the current content is compared to the mirror. Comparing instead of hooking each operation means a change
/// is sent exactly once, even when game methods call each other.
/// The host broadcasts the exact content with a rising version. A client sends its own changes to the host as ordered,
/// numbered operations and keeps the ones the host has not confirmed yet. When the host's content arrives, the client
/// replays its unconfirmed operations on top of it, so an answer that predates them never undoes them.
/// </summary>
internal static partial class SiloSyncManager
{
    private sealed class Mirror
    {
        public SiloSlotState[] Slots = Array.Empty<SiloSlotState>();
        public uint Version;
        public bool HasVersion;
        public readonly List<(uint Sequence, StorageOperation Operation)> Pending = new();
    }

    private static readonly SiloSlotState EmptySlot = new(SiloSlotState.EmptyType, 0);
    private static readonly Dictionary<string, Mirror> mirrors = new();
    private static readonly Dictionary<string, SiloStatePacket> pendingStates = new();
    private static uint nextSequence;
    private static bool initialized;

    private static bool IsActive => Main.Server.IsRunning || Main.Client.IsConnected;

    internal static void Initialize()
    {
        Reset();

        if (initialized)
            return;

        initialized = true;
        Main.Server.OnServerStarted += Reset;
        Main.Client.OnConnected += _ => Reset();
        Main.Client.OnDisconnected += () => MainThreadDispatcher.Instance.Enqueue(Reset);
        PlayerManager.OnPlayerRemoved += ForgetPlayer;
        StartCoroutine(HostScanLoop());
    }

    /// <summary>New session: every registered storage starts from its current content, with nothing in flight.</summary>
    private static void Reset()
    {
        mirrors.Clear();
        pendingStates.Clear();
        lastAppliedSequence.Clear();
        nextSequence = 0;

        foreach (var (id, ammo) in NetworkAmmoManager.RegisteredAmmo)
        {
            if (ammo != null)
                GetOrCreateMirror(id).Slots = Read(ammo);
        }
    }

    /// <summary>
    /// Records the current content of a storage as agreed, without sending anything.
    /// Used when a storage is registered or after its content was loaded from the host.
    /// </summary>
    internal static void Capture(string id, AmmoSlotManager ammo)
    {
        if (pendingStates.Remove(id, out var pending))
        {
            ApplyHostState(pending);
            return;
        }

        GetOrCreateMirror(id).Slots = Read(ammo);
    }

    /// <summary>Called after any game method that may have changed a storage.</summary>
    internal static void OnAmmoChanged(AmmoSlotManager? ammo)
    {
        if (ammo == null || !IsActive)
            return;

        var id = ammo.GetPlotID();
        if (id != null)
            Flush(id, ammo);
    }

    /// <inheritdoc cref="OnAmmoChanged(AmmoSlotManager?)"/>
    internal static void OnAmmoChanged(AmmoSlot? slot)
    {
        if (slot == null || !IsActive)
            return;

        var id = slot.GetPlotID();
        if (id != null)
            OnAmmoChanged(NetworkAmmoManager.GetAmmo(id));
    }

    /// <summary>Client side: applies the host's content, with this client's unconfirmed changes on top.</summary>
    internal static void ApplyHostState(SiloStatePacket packet)
    {
        var ammo = NetworkAmmoManager.GetAmmo(packet.Id);
        if (ammo == null)
        {
            if (!pendingStates.TryGetValue(packet.Id, out var kept) || kept.Version < packet.Version)
                pendingStates[packet.Id] = packet;
            return;
        }

        var mirror = GetOrCreateMirror(packet.Id);
        if (mirror.HasVersion && packet.Version <= mirror.Version)
            return;

        // Send our own unsent change first, so it is part of the operations replayed below.
        Flush(packet.Id, ammo);

        mirror.Pending.RemoveAll(entry => entry.Sequence <= packet.AckedSequence);
        var expected = StorageDiff.Apply(ToStorageSlots(packet.Slots), mirror.Pending.Select(entry => entry.Operation));

        HandlingPacket = true;
        try
        {
            Write(ammo, Array.ConvertAll(expected, slot => new SiloSlotState(slot.TypeId, slot.Count)));
        }
        finally
        {
            HandlingPacket = false;
        }

        mirror.Slots = Read(ammo);
        mirror.Version = packet.Version;
        mirror.HasVersion = true;

        if (SyncLog.Enabled)
            SyncLog.Write($"Storage {packet.Id}: host content v{packet.Version}, {mirror.Pending.Count} own changes replayed " +
                          $"[{Describe(mirror.Slots)}]");
    }

    /// <summary>Compares a storage with its mirror and sends the difference.</summary>
    private static void Flush(string id, AmmoSlotManager ammo)
    {
        var current = Read(ammo);

        if (!mirrors.TryGetValue(id, out var mirror))
        {
            GetOrCreateMirror(id).Slots = current;
            return;
        }

        if (current.AsSpan().SequenceEqual(mirror.Slots))
            return;

        var previous = mirror.Slots;
        mirror.Slots = current;

        if (Main.Server.IsRunning)
        {
            Broadcast(id, mirror);
            return;
        }

        // A change made while handling a packet was dictated by the network: the host makes it on its side too.
        if (!HandlingPacket)
            SendOperations(id, mirror, previous, current);
    }

    private static void SendOperations(string id, Mirror mirror, SiloSlotState[] previous, SiloSlotState[] current)
    {
        foreach (var operation in StorageDiff.Compute(ToStorageSlots(previous), ToStorageSlots(current)))
        {
            var sequence = ++nextSequence;
            mirror.Pending.Add((sequence, operation));

            SyncLog.Write($"Storage {id}: sending #{sequence} slot {operation.SlotIndex} type {operation.TypeId} delta {operation.Delta}");
            Main.Client.SendPacket(new SiloOperationPacket
            {
                Id = id,
                SlotIndex = operation.SlotIndex,
                TypeId = operation.TypeId,
                Delta = operation.Delta,
                Sequence = sequence
            });
        }
    }

    private static StorageSlot[] ToStorageSlots(IReadOnlyList<SiloSlotState> slots)
    {
        var result = new StorageSlot[slots.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = StorageSlot.Of(slots[i].TypeId, slots[i].Count);
        return result;
    }

    private static Mirror GetOrCreateMirror(string id)
    {
        if (!mirrors.TryGetValue(id, out var mirror))
        {
            mirror = new Mirror();
            mirrors[id] = mirror;
        }

        return mirror;
    }

    private static string Describe(SiloSlotState[] slots)
        => string.Join(", ", slots.Select(slot => slot.IsEmpty ? "empty" : $"{slot.TypeId}x{slot.Count}"));
}
