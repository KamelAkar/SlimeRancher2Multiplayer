using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using UnityEngine;

namespace CoopQA.Bot;

public sealed partial class BotMod
{
    // Actors to report (position and whether this copy simulates them), by test tag.
    private readonly Dictionary<string, long> watchedActors = new();

    // Silo inputs whose storage content to report, by index in Catchers().
    private readonly HashSet<int> watchedCatchers = new();

    // Plort collectors whose storage content to report, by index in Collectors().
    private readonly HashSet<int> watchedCollectors = new();

    private void WriteState()
    {
        var state = new Dictionary<string, object>
        {
            ["t"] = Math.Round(Time.realtimeSinceStartup, 2),
            ["role"] = Setting("role"),
            ["last_cmd"] = lastCommand,
            ["events"] = events.ToArray(),
            ["spawned"] = spawned,
            ["pending_actions"] = queuedActions.Count
        };

        var scene = SceneContext.Instance;
        var inGame = scene != null && scene.PlayerState != null && scene.Player != null;
        state["in_game"] = inGame;

        if (inGame)
        {
            state["currency"] = scene.PlayerState.GetCurrency(Newbucks());
            state["pos"] = Position(scene.Player.transform.position);
            state["actors"] = ActorReports();
            state["silos"] = SiloReports();
            state["collectors"] = CollectorReports();
        }

        if (sr2mp != null)
            AddNetworkState(state);

        var temporary = statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, statePath, true);
    }

    private static float[] Position(Vector3 value) => new[] { value.x, value.y, value.z };

    private void AddNetworkState(Dictionary<string, object> state)
    {
        var server = Sr("SR2MP.Main", "Server");
        var client = Sr("SR2MP.Main", "Client");
        var serverRunning = server != null && (bool)Prop(server, "IsRunning");

        state["server_running"] = serverRunning;
        state["server_clients"] = serverRunning ? Prop(server, "GetClientCount") : 0;
        state["client_connected"] = client != null && (bool)Prop(client, "IsConnected");
        state["local_id"] = Sr("SR2MP.GlobalVariables", "LocalID");

        var remotes = new List<object>();
        var players = Sr("SR2MP.GlobalVariables", "PlayerManager");
        if (players != null)
        {
            foreach (var remote in (System.Collections.IEnumerable)Call(players, "GetAllPlayers"))
            {
                var id = remote.GetType().GetField("PlayerId").GetValue(remote);
                var position = (Vector3)Prop(remote, "Position");
                remotes.Add(new Dictionary<string, object> { ["id"] = id, ["pos"] = Position(position) });
            }
        }

        state["remote_players"] = remotes;
    }

    private Dictionary<string, object> ActorReports()
    {
        var reports = new Dictionary<string, object>();
        var identifiables = SceneContext.Instance.GameModel?.identifiables;

        foreach (var (tag, id) in watchedActors)
        {
            var report = new Dictionary<string, object> { ["id"] = id, ["exists"] = false };
            reports[tag] = report;

            if (identifiables == null || !identifiables.TryGetValue(new ActorId(id), out var model) || model == null)
                continue;

            report["exists"] = true;
            var transform = model.Transform;
            if (!transform)
                continue;

            report["loaded"] = true;
            report["pos"] = Position(transform.position);
            report["owned"] = IsSimulatedHere(transform.gameObject);
        }

        return reports;
    }

    private object IsSimulatedHere(GameObject actor)
    {
        var type = sr2mp?.GetType("SR2MP.Components.Actor.NetworkActor");
        if (type == null)
            return null;

        var component = actor.GetComponent(Il2CppType.From(type));
        if (!component)
            return null;

        var cast = typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.TryCast))!.MakeGenericMethod(type);
        var networkActor = cast.Invoke(component, null);
        return networkActor == null ? null : type.GetProperty("LocallyOwned", BindingFlags.Public | BindingFlags.Instance)?.GetValue(networkActor);
    }

    private Dictionary<string, object> SiloReports()
    {
        var reports = new Dictionary<string, object>();
        if (watchedCatchers.Count == 0)
            return reports;

        var catchers = Catchers();

        foreach (var index in watchedCatchers)
        {
            if (index >= catchers.Count)
                continue;

            var slots = new List<object>();
            var ammo = catchers[index]._storageSilo.GetRelevantAmmo();
            if (ammo?.Slots != null)
            {
                foreach (var slot in ammo.Slots)
                    slots.Add(new object[] { slot?.Id ? slot.Id.name : "", slot?.Count ?? 0, slot?.MaxCount ?? 0 });
            }

            reports[index.ToString()] = new Dictionary<string, object> { ["slot"] = catchers[index].SlotIdx, ["slots"] = slots };
        }

        return reports;
    }

    private Dictionary<string, object> CollectorReports()
    {
        var reports = new Dictionary<string, object>();
        if (watchedCollectors.Count == 0)
            return reports;

        var collectors = Collectors();
        foreach (var index in watchedCollectors)
        {
            if (index >= collectors.Count)
                continue;

            var total = 0;
            var ammo = collectors[index]._storage.GetRelevantAmmo();
            if (ammo?.Slots != null)
            {
                foreach (var slot in ammo.Slots)
                    total += slot?.Count ?? 0;
            }

            reports[index.ToString()] = total;
        }

        return reports;
    }
}
