using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Ownership;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

/// <summary>Host side: who is connected, where they are, and sending decisions to one player.</summary>
internal static partial class ActorOwnershipManager
{
    private const int MaxMessagesPerPacket = 200;

    private static readonly HashSet<string> connectedPlayers = new();
    private static readonly List<PlayerPresence> presence = new();

    // Player positions read once per host tick instead of once per actor.
    private static readonly Dictionary<string, Vector3> playerPositions = new();

    private static bool IsConnected(string playerId) => playerId == LocalID || connectedPlayers.Contains(playerId);

    private static void CachePlayerPositions()
    {
        playerPositions.Clear();

        var player = SceneContext.Instance ? SceneContext.Instance.player : null;
        if (player)
            playerPositions[LocalID] = player!.transform.position;

        foreach (var playerId in connectedPlayers)
        {
            if (PlayerObjects.TryGetValue(playerId, out var playerObject) && playerObject)
                playerPositions[playerId] = playerObject.transform.position;
        }
    }

    private static List<PlayerPresence> BuildPresence(long actorId)
    {
        if (playerPositions.Count == 0)
            CachePlayerPositions();

        presence.Clear();
        awakeBy.TryGetValue(actorId, out var awake);
        var actorPosition = GetActorPosition(actorId);

        AddPresence(LocalID, awake, actorPosition);
        foreach (var playerId in connectedPlayers)
            AddPresence(playerId, awake, actorPosition);

        return presence;
    }

    private static void AddPresence(string playerId, HashSet<string>? awake, Vector3? actorPosition)
    {
        var isAwake = awake?.Contains(playerId) == true;
        var distance = float.MaxValue;

        if (isAwake && actorPosition.HasValue && playerPositions.TryGetValue(playerId, out var playerPosition))
            distance = Vector3.Distance(playerPosition, actorPosition.Value);

        presence.Add(new PlayerPresence(playerId, true, isAwake, distance));
    }

    private static Vector3? GetActorPosition(long actorId)
    {
        if (liveActors.TryGetValue(actorId, out var actor) && actor)
            return actor.transform.position;

        if (ActorManager.Actors.TryGetValue(actorId, out var model))
            return model?.TryCast<ActorModel>()?.lastPosition;

        return null;
    }

    private static void OnPlayerAdded(string playerId)
    {
        if (service == null || !connectedPlayers.Add(playerId))
            return;

        service.PlayerJoined(playerId);

        foreach (var (id, players) in awakeBy)
        {
            if (players.Contains(playerId))
                dirtyActors.Add(id);
        }

        var snapshot = new List<OwnershipMessage>();
        service.Snapshot(snapshot);
        SendToPlayer(playerId, snapshot);
        SyncLog.Write($"Sent {snapshot.Count} ownership records to {playerId}");
    }

    private static void OnPlayerRemoved(string playerId)
    {
        if (service == null || !connectedPlayers.Remove(playerId))
            return;

        foreach (var players in awakeBy.Values)
            players.Remove(playerId);

        outgoing.Clear();
        dirtyActors.UnionWith(service.PlayerLeft(playerId, NowMs, outgoing));
        Broadcast();
    }

    /// <summary>Refuses a vacuum hold: the player gets the current owner and its vacuum lets go.</summary>
    private static void SendCurrentAssignment(string playerId, long actorId)
    {
        if (service!.TryGetCurrent(actorId, out var current))
            SendToPlayer(playerId, new List<OwnershipMessage> { current }, holdDenied: true);
    }

    private static void SendToPlayer(string playerId, List<OwnershipMessage> messages, bool holdDenied = false)
    {
        if (playerId == LocalID)
        {
            ApplyMessages(messages, holdDenied);
            return;
        }

        var client = Main.Server.ClientManager.GetAllClients().FirstOrDefault(info => info.PlayerId == playerId);
        if (client == null)
            return;

        foreach (var chunk in messages.Chunk(MaxMessagesPerPacket))
        {
            var packet = new ActorOwnershipPacket { Messages = chunk.ToList(), HoldDenied = holdDenied };
            Main.Server.SendToClient(packet, client.EndPoint);
        }
    }
}
