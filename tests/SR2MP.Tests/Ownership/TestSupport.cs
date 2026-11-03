using SR2MP.Shared.Ownership;

namespace SR2MP.Tests.Ownership;

/// <summary>Player ids and small builders shared by the ownership tests.</summary>
internal static class TestSupport
{
    public const string Host = "PLAYER_HOST";
    public const string PlayerA = "PLAYER_A0DA7B604";
    public const string PlayerB = "PLAYER_B13C9F2E1";
    public const string PlayerC = "PLAYER_C77E01D5A";

    public static readonly OwnershipConfig HostFirst = new() { PreferHost = true };

    public static PlayerPresence Near(string player, float distance) => new(player, true, true, distance);

    public static PlayerPresence Asleep(string player, float distance) => new(player, true, false, distance);

    public static PlayerPresence Gone(string player, float distance) => new(player, false, true, distance);

    public static OwnershipMessage Grant(long actor, uint epoch, string? owner) =>
        new(OwnershipMessageKind.Grant, actor, epoch, owner, null);

    public static OwnershipMessage Transfer(long actor, uint epoch, string to, string? from) =>
        new(OwnershipMessageKind.Transfer, actor, epoch, to, from);

    public static OwnershipMessage Commit(long actor, uint epoch, string to, string? from) =>
        new(OwnershipMessageKind.Commit, actor, epoch, to, from);

    public static OwnershipMessage Release(long actor, uint epoch) =>
        new(OwnershipMessageKind.Release, actor, epoch, null, null);

    public static OwnershipMessage AckFor(OwnershipMessage transfer) => transfer with { Kind = OwnershipMessageKind.Ack };

    /// <summary>A service with the host and the given players connected.</summary>
    public static OwnershipService Service(OwnershipConfig? config, params string[] players)
    {
        var service = new OwnershipService(Host, config);
        foreach (var player in players)
            service.PlayerJoined(player);
        return service;
    }

    /// <summary>Applies every message to every view.</summary>
    public static void Broadcast(IEnumerable<OwnershipMessage> messages, params OwnershipView[] views)
    {
        foreach (var message in messages)
        {
            foreach (var view in views)
                view.Apply(message);
        }
    }
}

/// <summary>Small deterministic RNG (splitmix64). The same seed gives the same sequence on every platform.</summary>
internal sealed class SplitMix
{
    private ulong state;

    public SplitMix(ulong seed)
    {
        state = seed + 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < 4; i++)
            Next();
    }

    public ulong Next()
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Inclusive range.</summary>
    public int Range(int low, int high) => low + (int)(Next() % (ulong)(high - low + 1));

    public bool Chance(double probability) => (Next() >> 11) * (1.0 / 9007199254740992.0) < probability;

    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count; i > 1; i--)
        {
            var j = Range(0, i - 1);
            (items[i - 1], items[j]) = (items[j], items[i - 1]);
        }
    }
}
