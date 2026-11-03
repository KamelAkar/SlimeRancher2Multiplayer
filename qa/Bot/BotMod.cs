// CoopQA.Bot: test-only MelonLoader mod that drives Ranching Together in automated two-copy tests.
// Adapted from coop-anything games/slime-rancher-2/qa/Bot (MIT, Copyright (c) 2026 cranebeaker and contributors),
// https://github.com/cranebeaker/coop-anything
//
// Inert unless <game dir>/UserData/CoopQA.Bot.ini exists (only test copies have it).
// - loads a save at boot through the game's own CLI option (CliOptions.SaveFileToLoad)
// - gives each test copy its own player id (two copies on one PC would otherwise share one)
// - runs numbered commands from UserData/CoopQA/cmd.txt and writes UserData/CoopQA/state.json
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(CoopQA.Bot.BotMod), "CoopQA.Bot", "0.2.0", "coop-anything")]
[assembly: MelonGame("MonomiPark", "SlimeRancher2")]
[assembly: MelonOptionalDependencies("SR2MP")]

namespace CoopQA.Bot;

public sealed partial class BotMod : MelonMod
{
    private const float StateInterval = 0.25f;

    private static Dictionary<string, string> config;

    private readonly List<string> events = new();
    private string commandPath;
    private string statePath;
    private int lastCommand;
    private float nextState;
    private Assembly sr2mp;

    public override void OnInitializeMelon()
    {
        var ini = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "CoopQA.Bot.ini");
        if (!File.Exists(ini))
        {
            LoggerInstance.Msg("inert: no UserData/CoopQA.Bot.ini");
            return;
        }

        config = File.ReadAllLines(ini)
            .Where(line => line.Contains('=') && !line.TrimStart().StartsWith("#"))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

        var directory = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "CoopQA");
        Directory.CreateDirectory(directory);
        commandPath = Path.Combine(directory, "cmd.txt");
        statePath = Path.Combine(directory, "state.json");
        File.Delete(commandPath);
        File.Delete(statePath);

        sr2mp = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "SR2MP");
        SetPlayerId();
        SetSaveToLoad();
        LoggerInstance.Msg($"ACTIVE role={Setting("role")}");
    }

    private void SetPlayerId()
    {
        if (!config.TryGetValue("player_id", out var playerId) || sr2mp == null)
            return;

        var field = sr2mp.GetType("SR2MP.Shared.Utils.PlayerIdGenerator")
            ?.GetField("cachedPlayerId", BindingFlags.NonPublic | BindingFlags.Static);
        field?.SetValue(null, playerId);
        Event($"player_id set: {playerId} (field found: {field != null})");
    }

    private void SetSaveToLoad()
    {
        if (!config.TryGetValue("save", out var save))
            return;

        HarmonyInstance.Patch(AccessTools.Method(typeof(CliOptions), nameof(CliOptions.Initialize)),
            postfix: new HarmonyMethod(typeof(BotMod), nameof(CliPostfix)));

        try
        {
            CliOptions.SaveFileToLoad = save;
        }
        catch (Exception ex)
        {
            Event("early save set failed: " + ex.Message);
        }

        Event($"save to load: {save}");
    }

    private static void CliPostfix() => CliOptions.SaveFileToLoad = config["save"];

    private static string Setting(string key, string fallback = "")
        => config != null && config.TryGetValue(key, out var value) ? value : fallback;

    private void Event(string text)
    {
        events.Add($"{Time.realtimeSinceStartup:F1} {text}");
        LoggerInstance.Msg(text);
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (config != null)
            Event($"scene loaded: {sceneName}");
    }

    public override void OnUpdate()
    {
        if (config == null)
            return;

        try
        {
            RunQueuedActions();
        }
        catch (Exception ex)
        {
            Event("ACTION ERROR " + ex.Message);
        }

        if (Time.realtimeSinceStartup < nextState)
            return;

        nextState = Time.realtimeSinceStartup + StateInterval;

        try
        {
            RunCommands();
        }
        catch (Exception ex)
        {
            Event("CMD ERROR " + ex);
        }

        try
        {
            WriteState();
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning("state: " + ex.Message);
        }
    }

    private void RunCommands()
    {
        if (!File.Exists(commandPath))
            return;

        foreach (var line in File.ReadAllLines(commandPath))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out var number) || number <= lastCommand)
                continue;

            lastCommand = number;
            Event($"cmd {number}: {string.Join(' ', parts.Skip(1))}");
            RunCommand(parts[1], parts.Skip(2).ToArray());
        }
    }

    private object Sr(string type, string member)
    {
        var target = sr2mp.GetType(type);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var property = target.GetProperty(member, flags);
        return property != null ? property.GetValue(null) : target.GetField(member, flags)?.GetValue(null);
    }

    private static object Call(object target, string name, params object[] arguments)
    {
        var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .First(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length);
        return method.Invoke(target, arguments);
    }

    private static object Prop(object target, string name)
        => target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
}
