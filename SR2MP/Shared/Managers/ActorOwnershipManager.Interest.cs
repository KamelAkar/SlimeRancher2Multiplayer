using System.Collections;
using SR2MP.Packets.Actor;

namespace SR2MP.Shared.Managers;

internal static partial class ActorOwnershipManager
{
    private const float InterestInterval = 0.5f;

    // Actors last reported to the host as awake on this machine.
    private static readonly HashSet<long> reportedAwake = new();

    /// <summary>
    /// Every machine tells the host which actors are loaded and not hibernating on its side. Polling the hibernation
    /// flag is used instead of the game's hibernation event because the event fires before a freshly enabled
    /// component can listen to it, and a missed wake up left slimes frozen for many seconds.
    /// </summary>
    private static IEnumerator InterestLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(InterestInterval);

            if (sessionActive && !IsActive)
            {
                EndSession();
                continue;
            }

            if (!sessionActive)
                continue;

            try
            {
                ExpireHoldClaims();
                ReportInterest();
            }
            catch (Exception ex)
            {
                SrLogger.LogWarning($"Actor interest report failed: {ex.Message}");
            }
        }
    }

    private static void ReportInterest()
    {
        var awakeNow = new HashSet<long>();
        var gone = new List<long>();

        foreach (var (id, actor) in liveActors)
        {
            if (!actor)
                gone.Add(id);
            else if (actor.IsAwake)
                awakeNow.Add(id);
        }

        foreach (var id in gone)
            liveActors.Remove(id);

        var added = awakeNow.Where(id => !reportedAwake.Contains(id)).ToList();
        var removed = reportedAwake.Where(id => !awakeNow.Contains(id)).ToList();

        if (added.Count == 0 && removed.Count == 0)
            return;

        reportedAwake.Clear();
        reportedAwake.UnionWith(awakeNow);

        if (IsHost)
            OnInterest(LocalID, added, removed);
        else
            Main.Client.SendPacket(new ActorInterestPacket { Awake = added, Asleep = removed });
    }
}
