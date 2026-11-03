using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher;
using Il2CppMonomiPark.SlimeRancher.Economy;
using Il2CppMonomiPark.SlimeRancher.Player;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;
using UnityEngine;

namespace CoopQA.Bot;

public sealed partial class BotMod
{
    // Actions spread over several frames, like a player spamming two keys.
    private readonly Queue<Action> queuedActions = new();

    // Actor ids spawned by this copy, by test tag.
    private readonly Dictionary<string, long> spawned = new();

    private void RunCommand(string name, string[] args)
    {
        switch (name)
        {
            case "host": Call(Sr("SR2MP.Main", "Server"), "Start", int.Parse(args[0]), true); break;
            case "connect": Call(Sr("SR2MP.Main", "Client"), "Connect", args[0], int.Parse(args[1])); break;
            case "addcurrency": SceneContext.Instance.PlayerState.AddCurrency(Newbucks(), int.Parse(args[0]), false, null); break;
            case "save": GameContext.Instance.AutoSaveDirector.SaveGameAndFlush(); break;
            case "quit": WriteState(); Application.Quit(); break;
            case "teleport_near": TeleportNearPlayer(args[0], Number(args[1]), Number(args[2])); break;
            case "teleport_silo": TeleportToCatcher(int.Parse(args[0])); break;
            case "spawn_ahead": SpawnAhead(args[0], args[1], Number(args[2]), Number(args[3]), Number(args[4])); break;
            case "spawn_catcher": SpawnInCatcher(args[0], args[1], int.Parse(args[2])); break;
            case "watch": watchedActors[args[1]] = long.Parse(args[0], CultureInfo.InvariantCulture); break;
            case "watch_silo": watchedCatchers.Add(int.Parse(args[0])); break;
            case "silo_spam": QueueSiloSpam(int.Parse(args[0]), args[1], int.Parse(args[2])); break;
            case "teleport_collector": TeleportToCollector(int.Parse(args[0])); break;
            case "collect": StartCollector(int.Parse(args[0])); break;
            case "spawn_collector": SpawnNearCollector(args[0], int.Parse(args[1]), int.Parse(args[2])); break;
            case "watch_collector": watchedCollectors.Add(int.Parse(args[0])); break;
            default: Event("unknown command " + name); break;
        }
    }

    private void RunQueuedActions()
    {
        if (queuedActions.Count > 0)
            queuedActions.Dequeue()();
    }

    private static float Number(string text) => float.Parse(text, CultureInfo.InvariantCulture);

    private static ICurrency Newbucks()
        => GameContext.Instance.LookupDirector._currencyList.GetDefaultCurrency().Cast<ICurrency>();

    private static void MovePlayer(Vector3 position)
    {
        var player = SceneContext.Instance.Player;
        var controller = player.GetComponent<SRCharacterController>();
        if (controller)
            controller.Position = position;
        else
            player.transform.position = position;
    }

    private void TeleportNearPlayer(string playerId, float dx, float dz)
    {
        var playerObjects = (System.Collections.IDictionary)Sr("SR2MP.GlobalVariables", "PlayerObjects");
        if (playerObjects[playerId] is not GameObject other || !other)
        {
            Event($"teleport_near: player {playerId} not found");
            return;
        }

        MovePlayer(other.transform.position + new Vector3(dx, 1f, dz));
        Event($"teleported near {playerId}");
    }

    /// <summary>
    /// Silo inputs (not plort collectors or feeders) sorted by position, so both copies give the same index to the
    /// same silo.
    /// </summary>
    private static List<SiloCatcher> Catchers()
        => UnityEngine.Object.FindObjectsOfType<SiloCatcher>()
            .Where(catcher => catcher && catcher._storageSilo && catcher._storageSilo.AmmoSetReference
                && catcher._storageSilo.AmmoSetReference.name.Contains("Silo", StringComparison.OrdinalIgnoreCase))
            .OrderBy(catcher => Mathf.Round(catcher.Position.x))
            .ThenBy(catcher => Mathf.Round(catcher.Position.z))
            .ToList();

    private void TeleportToCatcher(int index)
    {
        var catchers = Catchers();
        if (index >= catchers.Count)
        {
            Event($"teleport_silo: only {catchers.Count} silo inputs");
            return;
        }

        var catcher = catchers[index];
        MovePlayer(catcher.Position + catcher.transform.forward * 4f + Vector3.up * 1.5f);
        Event($"teleported to silo input {index} at {catcher.Position}");
    }

    private static IdentifiableType FindType(string fragment)
    {
        var types = GameContext.Instance.AutoSaveDirector._saveReferenceTranslation._identifiableTypeLookup;
        IdentifiableType best = null;

        foreach (var entry in types)
        {
            var type = entry.value;
            if (!type || !type.prefab || type.name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (string.Equals(type.name, fragment, StringComparison.OrdinalIgnoreCase))
                return type;

            best ??= type;
        }

        return best;
    }

    private long Spawn(string fragment, string tag, Vector3 position, Vector3 velocity)
    {
        var type = FindType(fragment);
        if (!type)
        {
            Event($"spawn: no type matching {fragment}");
            return 0;
        }

        var actor = InstantiationHelpers.InstantiateActor(type.prefab, SystemContext.Instance.SceneLoader.CurrentSceneGroup,
            position, Quaternion.identity, false, SlimeAppearance.AppearanceSaveSet.NONE,
            SlimeAppearance.AppearanceSaveSet.NONE, new Il2CppSystem.Nullable<AmmoSlot.AmmoMetadata>(), false, false);

        var body = actor.GetComponent<Rigidbody>();
        if (body)
            body.velocity = velocity;

        var id = actor.GetComponent<IdentifiableActor>().GetActorId().Value;
        spawned[tag] = id;
        watchedActors[tag] = id;
        Event($"spawned {type.name} as {tag} id {id} at {position} velocity {velocity}");
        return id;
    }

    private void SpawnAhead(string fragment, string tag, float distance, float speed, float up)
    {
        var camera = Camera.main ? Camera.main.transform : SceneContext.Instance.Player.transform;
        var forward = Vector3.ProjectOnPlane(camera.forward, Vector3.up).normalized;
        var start = SceneContext.Instance.Player.transform.position + forward * distance + Vector3.up * 1.5f;
        Spawn(fragment, tag, start, forward * speed + Vector3.up * up);
    }

    /// <summary>The type to use for a silo: a named type, or "auto" for the type already in the silo input's slot.</summary>
    private static IdentifiableType SiloType(SiloCatcher catcher, string fragment)
    {
        if (fragment != "auto")
            return FindType(fragment);

        var slots = catcher._storageSilo.GetRelevantAmmo()?.Slots;
        var index = catcher.SlotIdx;
        return slots != null && index >= 0 && index < slots.Count ? slots[index]?.Id : null;
    }

    private void SpawnInCatcher(string fragment, string tag, int index)
    {
        var catchers = Catchers();
        if (index >= catchers.Count)
        {
            Event($"spawn_catcher: only {catchers.Count} silo inputs");
            return;
        }

        var type = SiloType(catchers[index], fragment);
        if (!type)
        {
            Event($"spawn_catcher: no type for silo {index}");
            return;
        }

        Spawn(type.name, tag, catchers[index].Position, Vector3.zero);
    }

    /// <summary>Takes one item out and puts it back (or the reverse on an empty slot), one step per frame.</summary>
    private void QueueSiloSpam(int index, string fragment, int count)
    {
        var catchers = Catchers();
        var type = index < catchers.Count ? SiloType(catchers[index], fragment) : null;
        if (!type)
        {
            Event($"silo_spam: silo {index} or its type missing");
            return;
        }

        var ammo = catchers[index]._storageSilo.GetRelevantAmmo();
        var slot = catchers[index].SlotIdx;
        var startWithTake = ammo.Slots[slot] != null && ammo.Slots[slot].Count > 0;

        for (var i = 0; i < count; i++)
        {
            var take = (i % 2 == 0) == startWithTake;
            queuedActions.Enqueue(() =>
            {
                if (take)
                    ammo.Decrement(slot, 1);
                else
                    ammo.MaybeAddToSpecificSlot(new AmmoSlot.AmmoMetadata(type), slot, 1, false);
            });
        }

        Event($"silo_spam: queued {count} steps of {type.name} on silo {index} slot {slot}");
    }

    /// <summary>Plort collectors sorted by position, so both copies give the same index to the same collector.</summary>
    private static List<PlortCollector> Collectors()
        => UnityEngine.Object.FindObjectsOfType<PlortCollector>()
            .Where(collector => collector && collector._storage)
            .OrderBy(collector => Mathf.Round(collector.transform.position.x))
            .ThenBy(collector => Mathf.Round(collector.transform.position.z))
            .ToList();

    private void TeleportToCollector(int index)
    {
        var collectors = Collectors();
        if (index >= collectors.Count)
        {
            Event($"teleport_collector: only {collectors.Count} collectors");
            return;
        }

        MovePlayer(collectors[index].transform.position + Vector3.up * 3f);
        Event($"teleported to collector {index} at {collectors[index].transform.position}");
    }

    private void StartCollector(int index)
    {
        var collectors = Collectors();
        if (index >= collectors.Count)
        {
            Event($"collect: only {collectors.Count} collectors");
            return;
        }

        collectors[index].StartCollection();
        Event($"collect: started collector {index}");
    }

    /// <summary>Drops plorts just above a collector, spread on a small circle so they do not stack.</summary>
    private void SpawnNearCollector(string fragment, int index, int count)
    {
        var collectors = Collectors();
        if (index >= collectors.Count)
        {
            Event($"spawn_collector: only {collectors.Count} collectors");
            return;
        }

        var center = collectors[index].transform.position + Vector3.up * 1.5f;
        for (var i = 0; i < count; i++)
        {
            var angle = i * Mathf.PI * 2f / count;
            Spawn(fragment, $"P{i}", center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 1.5f, Vector3.zero);
        }
    }
}
