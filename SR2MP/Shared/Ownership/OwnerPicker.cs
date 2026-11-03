using System.Collections.Generic;

namespace SR2MP.Shared.Ownership;

/// <summary>The pure owner choice rule.</summary>
internal static class OwnerPicker
{
    /// <summary>
    /// Returns the owner to use for one actor, or null for nobody.
    /// <paramref name="current"/> is null when the actor is unowned. <paramref name="held"/> is true while
    /// <see cref="OwnershipConfig.MinHoldMs"/> has not elapsed since the last change.
    /// </summary>
    public static string? Pick(
        IReadOnlyList<PlayerPresence> who,
        string? current,
        bool held,
        string hostId,
        OwnershipConfig config)
    {
        var hasBest = false;
        var hasCurrent = false;
        PlayerPresence best = default;
        PlayerPresence cur = default;

        foreach (var presence in who)
        {
            if (current != null && presence.PlayerId == current)
            {
                cur = presence;
                hasCurrent = true;
            }

            if (!presence.Eligible)
                continue;

            if (!hasBest || IsBetter(presence, best, hostId, config.PreferHost))
            {
                best = presence;
                hasBest = true;
            }
        }

        if (current == null || !hasCurrent || !cur.Connected)
            return hasBest ? best.PlayerId : null;

        // Nobody eligible, or the best is already the owner: keep it (a dormant actor stays with its owner).
        if (!hasBest || best.PlayerId == current)
            return current;

        // The current owner no longer has the actor awake: switch at once.
        if (!cur.Eligible)
            return best.PlayerId;

        if (held)
            return current;

        if (config.PreferHost && best.PlayerId == hostId)
            return best.PlayerId;

        return best.Distance < cur.Distance * (1.0 - config.SwitchMargin) ? best.PlayerId : current;
    }

    private static bool IsBetter(PlayerPresence candidate, PlayerPresence best, string hostId, bool preferHost)
    {
        if (preferHost)
        {
            var candidateIsHost = candidate.PlayerId == hostId;
            var bestIsHost = best.PlayerId == hostId;
            if (candidateIsHost != bestIsHost)
                return candidateIsHost;
        }

        if (candidate.Distance < best.Distance)
            return true;

        // Equal distance goes to the ordinal-lowest player id.
        return !(candidate.Distance > best.Distance)
            && string.CompareOrdinal(candidate.PlayerId, best.PlayerId) < 0;
    }
}
