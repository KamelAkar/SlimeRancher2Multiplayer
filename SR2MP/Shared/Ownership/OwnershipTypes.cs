namespace SR2MP.Shared.Ownership;

/// <summary>The five ownership message kinds. Values are stable so they can go on the wire as a byte.</summary>
internal enum OwnershipMessageKind : byte
{
    /// <summary>The actor is owned by <c>Owner</c> at <c>Epoch</c> (first owner, take over, or a cancelled handoff).</summary>
    Grant = 1,

    /// <summary>Begin a handoff from <c>PreviousOwner</c> to <c>Owner</c>. The epoch only takes effect at Commit.</summary>
    Transfer = 2,

    /// <summary>New owner to host: the old owner's last state is applied, ready to take over.</summary>
    Ack = 3,

    /// <summary>Handoff finished: <c>Owner</c> is authoritative at <c>Epoch</c>.</summary>
    Commit = 4,

    /// <summary>Nobody owns the actor at <c>Epoch</c>.</summary>
    Release = 5,
}

/// <summary>One ownership message. A null owner means nobody.</summary>
internal readonly record struct OwnershipMessage(
    OwnershipMessageKind Kind,
    long ActorId,
    uint Epoch,
    string? Owner,
    string? PreviousOwner);

/// <summary>Result of applying an ownership message to a view.</summary>
internal enum ApplyResult : byte
{
    Applied,
    Duplicate,
    Stale,
}

/// <summary>Verdict on one incoming actor update.</summary>
internal enum UpdateVerdict : byte
{
    /// <summary>From the current owner at the current epoch.</summary>
    Accept,

    /// <summary>Right epoch, wrong sender, or the actor has no owner.</summary>
    NotOwner,

    /// <summary>Older epoch: a past owner is still talking.</summary>
    StaleEpoch,

    /// <summary>Newer epoch than this view knows: the view is behind, ask the host.</summary>
    FutureEpoch,

    /// <summary>The sender is the new owner of a handoff that is not committed yet.</summary>
    NotYetActive,

    /// <summary>The update came from this machine.</summary>
    EchoOfSelf,

    /// <summary>No record of this actor.</summary>
    UnknownActor,
}

/// <summary>What the host knows about one player relative to one actor, at evaluation time.</summary>
/// <param name="PlayerId">The player.</param>
/// <param name="Connected">The player is connected.</param>
/// <param name="Awake">The actor is loaded and not hibernating on that player's machine.</param>
/// <param name="Distance">Distance to the actor, in any unit (only compared).</param>
internal readonly record struct PlayerPresence(string PlayerId, bool Connected, bool Awake, float Distance)
{
    public bool Eligible => Connected && Awake;
}

/// <summary>Tuning of the owner choice and of handoffs.</summary>
internal sealed class OwnershipConfig
{
    /// <summary>After a change the owner is kept this long, unless it became ineligible.</summary>
    public long MinHoldMs { get; init; } = 1500;

    /// <summary>A challenger must be this fraction closer than the current owner.</summary>
    public double SwitchMargin { get; init; } = 0.25;

    /// <summary>How long the host waits for an Ack before it forces the outcome.</summary>
    public long HandoffTimeoutMs { get; init; } = 1000;

    /// <summary>When true the host wins whenever it is eligible. When false the host is a player like any other.</summary>
    public bool PreferHost { get; init; }
}

/// <summary>Counters of the messages a service emitted.</summary>
internal sealed class OwnershipStats
{
    public long Grants { get; set; }
    public long Transfers { get; set; }
    public long Commits { get; set; }
    public long Releases { get; set; }
    public long ForcedCommits { get; set; }
    public long AbortedHandoffs { get; set; }
}

/// <summary>Counters of what a view applied and judged.</summary>
internal sealed class ViewStats
{
    public long Applied { get; set; }
    public long Duplicates { get; set; }
    public long StaleMessages { get; set; }
    public long Accepted { get; set; }
    public long NotOwner { get; set; }
    public long StaleEpoch { get; set; }
    public long FutureEpoch { get; set; }
    public long NotYetActive { get; set; }
    public long Echoes { get; set; }
    public long Unknown { get; set; }
}
