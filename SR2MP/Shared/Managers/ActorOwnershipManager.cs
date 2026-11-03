using SR2MP.Components.Actor;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Ownership;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Decides which machine simulates each actor (slimes, food, plorts...), following the coop-anything ownership rules:
/// the host is the only one who decides, every decision carries an epoch, and a handoff passes the old owner's last
/// state to the new owner. Every machine applies the host's decisions to an <see cref="OwnershipView"/> and only
/// simulates the actors that view says it owns.
/// </summary>
internal static partial class ActorOwnershipManager
{
    private static OwnershipView view = new(null);
    private static OwnershipService? service;

    private static readonly Dictionary<long, NetworkActor> liveActors = new();

    private static bool initialized;
    private static bool sessionActive;

    private static bool IsActive => Main.Server.IsRunning || Main.Client.IsConnected;
    private static bool IsHost => Main.Server.IsRunning;
    private static long NowMs => Environment.TickCount64;

    internal static void Initialize()
    {
        if (initialized)
            return;

        initialized = true;
        Main.Server.OnServerStarted += StartSession;
        Main.Client.OnConnected += _ => StartSession();
        PlayerManager.OnPlayerAdded += OnPlayerAdded;
        PlayerManager.OnPlayerRemoved += OnPlayerRemoved;
        StartCoroutine(InterestLoop());
        StartCoroutine(HostLoop());
    }

    private static void StartSession()
    {
        sessionActive = true;
        view = new OwnershipView(LocalID);
        service = IsHost ? new OwnershipService(LocalID) : null;
        ResetSessionState();
        SyncLog.Write($"Ownership session started as {(IsHost ? "host" : "client")} {LocalID}");
    }

    /// <summary>Back to single player: this machine simulates everything again.</summary>
    private static void EndSession()
    {
        sessionActive = false;
        view = new OwnershipView(null);
        service = null;
        ResetSessionState();
        SyncLog.Write("Ownership session ended");
    }

    private static void ResetSessionState()
    {
        holdClaims.Clear();
        heldByMe.Clear();
        pendingHandoffStates.Clear();
        reportedAwake.Clear();
        awakeBy.Clear();
        dirtyActors.Clear();
        connectedPlayers.Clear();

        foreach (var id in liveActors.Keys.ToArray())
            Refresh(id);
    }

    /// <summary>Whether this machine must run the physics and AI of an actor right now.</summary>
    internal static bool ShouldSimulate(long actorId)
    {
        if (!IsActive)
            return true;

        if (holdClaims.ContainsKey(actorId))
            return true;

        // Actors the host never assigned are awake for nobody else: the host keeps them, clients wait.
        if (!view.IsKnown(actorId))
            return IsHost;

        return view.MayPublish(actorId, out _) && !view.IsHandoffOutgoing(actorId);
    }

    /// <summary>The epoch to stamp on an update for this actor, or false if this machine may not publish it.</summary>
    internal static bool TryGetPublishEpoch(long actorId, out uint epoch)
    {
        epoch = 0;
        return IsActive && view.MayPublish(actorId, out epoch) && !view.IsHandoffOutgoing(actorId);
    }

    /// <summary>
    /// Whether an incoming update (position or state) may be applied. <paramref name="sender"/> is known on the host
    /// only, clients receive updates the host already checked.
    /// </summary>
    internal static bool AcceptUpdate(long actorId, string? sender, uint epoch)
    {
        if (holdClaims.ContainsKey(actorId))
            return false;

        return view.Check(actorId, sender, epoch) == UpdateVerdict.Accept;
    }

    internal static void Register(NetworkActor actor, long actorId)
    {
        if (actorId == 0)
            return;

        liveActors[actorId] = actor;
        actor.LocallyOwned = ShouldSimulate(actorId);
    }

    internal static void Unregister(NetworkActor actor, long actorId)
    {
        if (liveActors.TryGetValue(actorId, out var current) && current == actor)
            liveActors.Remove(actorId);
    }

    /// <summary>An actor this machine just created: it simulates it from the first frame.</summary>
    internal static void OnLocalSpawn(long actorId) => AdoptSpawn(actorId, LocalID);

    /// <summary>An actor another machine created: that machine simulates it.</summary>
    internal static void OnRemoteSpawn(long actorId, string owner) => AdoptSpawn(actorId, owner);

    private static void AdoptSpawn(long actorId, string owner)
    {
        if (!IsActive || actorId == 0 || string.IsNullOrEmpty(owner))
            return;

        view.AdoptSpawn(actorId, owner);

        if (service != null)
        {
            service.TrackOwned(actorId, owner, NowMs);

            // The spawner has the actor loaded. Without this the host could take it away before the spawner's
            // next awake report arrives, as if the spawner could not simulate it.
            MarkAwake(owner, actorId);
        }

        Refresh(actorId);
    }

    /// <summary>The actor is gone for good (not just unloaded): forget its ownership record.</summary>
    internal static void OnActorDestroyed(long actorId)
    {
        view.Forget(actorId);
        holdClaims.Remove(actorId);
        heldByMe.Remove(actorId);
        pendingHandoffStates.Remove(actorId);
        ForgetOnHost(actorId);
    }

    /// <summary>
    /// Applies ownership decisions from the host (or from the local service on the host).
    /// <paramref name="holdDenied"/> means the host refused this machine's vacuum hold on these actors.
    /// </summary>
    internal static void ApplyMessages(IEnumerable<OwnershipMessage> messages, bool holdDenied = false)
    {
        foreach (var message in messages)
        {
            if (holdDenied)
                DropHoldClaim(message.ActorId, releaseVacuum: true);
            else if (message.Owner == LocalID && message.Kind is OwnershipMessageKind.Grant or OwnershipMessageKind.Commit)
                holdClaims.Remove(message.ActorId);

            if (view.Apply(message) == ApplyResult.Applied)
            {
                if (SyncLog.Enabled)
                    SyncLog.Write($"Actor {message.ActorId}: {message.Kind} epoch {message.Epoch} owner {message.Owner ?? "nobody"} " +
                                  $"(was {message.PreviousOwner ?? "nobody"})");

                if (message.Kind == OwnershipMessageKind.Transfer)
                    OnTransferApplied(message);
                else
                    pendingHandoffStates.Remove(message.ActorId);
            }

            Refresh(message.ActorId);
        }
    }

    private static void Refresh(long actorId)
    {
        if (liveActors.TryGetValue(actorId, out var actor) && actor)
            actor.LocallyOwned = ShouldSimulate(actorId);
    }

    private static void SendRequest(ActorOwnershipRequestKind kind, long actorId, uint epoch = 0)
    {
        if (IsHost)
        {
            HandleRequest(LocalID, kind, actorId, epoch);
            return;
        }

        Main.Client.SendPacket(new ActorOwnershipRequestPacket { Kind = kind, ActorId = actorId, Epoch = epoch });
    }
}
