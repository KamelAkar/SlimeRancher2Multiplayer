using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Vacuum holds: a player who holds an actor in their vacuum must simulate it wherever they carry it. The player
/// claims it locally at once (simulates it and ignores other machines' updates) while the host confirms. The claim
/// ends when the host gives the actor to this machine, when the host refuses the hold, or after a timeout.
/// </summary>
internal static partial class ActorOwnershipManager
{
    private const long HoldClaimTimeoutMs = 3000;

    // Actors claimed by this machine's vacuum, with the time of the claim.
    private static readonly Dictionary<long, long> holdClaims = new();

    // Actors this machine asked to hold, so a release is only sent for a hold the host knows about.
    private static readonly HashSet<long> heldByMe = new();

    /// <summary>The local player caught the actor in their vacuum.</summary>
    internal static void OnLocalHold(long actorId)
    {
        if (!IsActive || actorId == 0 || !heldByMe.Add(actorId))
            return;

        if (view.OwnerOf(actorId) != LocalID || view.IsHandoffOutgoing(actorId))
            Claim(actorId);

        SendRequest(ActorOwnershipRequestKind.Hold, actorId);
    }

    /// <summary>The local player let go of the actor. It stays with this player until the host moves it.</summary>
    internal static void OnLocalRelease(long actorId)
    {
        if (!IsActive || !heldByMe.Remove(actorId))
            return;

        SendRequest(ActorOwnershipRequestKind.ReleaseHold, actorId);
    }

    private static void Claim(long actorId)
    {
        holdClaims[actorId] = NowMs;
        Refresh(actorId);
    }

    /// <summary>
    /// A handoff away from this machine was decided while its player holds the actor: keep simulating it, the host
    /// gives it back as soon as it sees the hold.
    /// </summary>
    private static void KeepHeldActor(long actorId)
    {
        if (heldByMe.Contains(actorId))
            Claim(actorId);
    }

    private static void DropHoldClaim(long actorId, bool releaseVacuum)
    {
        if (!holdClaims.Remove(actorId))
            return;

        if (releaseVacuum)
        {
            heldByMe.Remove(actorId);
            ReleaseFromVacuum(actorId);
        }

        Refresh(actorId);
    }

    /// <summary>A claim the host never confirmed must not keep two machines simulating the same actor.</summary>
    private static void ExpireHoldClaims()
    {
        if (holdClaims.Count == 0)
            return;

        var now = NowMs;
        foreach (var (actorId, claimedAt) in holdClaims.ToArray())
        {
            if (now - claimedAt < HoldClaimTimeoutMs)
                continue;

            SyncLog.Write($"Actor {actorId}: hold claim expired without an answer from the host");
            DropHoldClaim(actorId, releaseVacuum: true);
        }
    }

    private static void ReleaseFromVacuum(long actorId)
    {
        if (!liveActors.TryGetValue(actorId, out var actor) || !actor)
            return;

        try
        {
            var player = SceneContext.Instance.Player;
            var vacuum = player ? player.GetComponent<PlayerItemController>()._vacuumItem : null;

            if (vacuum == null || !vacuum || vacuum._held != actor.gameObject)
                return;

            vacuum.LockJoint.connectedBody = null;
            vacuum._held = null;
            vacuum.SetHeldRad(0f);
            vacuum._vacMode = VacuumItem.VacMode.NONE;
            actor.GetComponent<Vacuumable>().Release();
        }
        catch (Exception ex)
        {
            SrLogger.LogDebug($"Failed to release actor {actorId} from the vacuum: {ex.Message}");
        }
    }
}
