namespace SR2MP.Shared.Utils;

/// <summary>
/// Version of the network protocol. Raise it whenever a packet layout or a sync rule changes, so that players on
/// different builds are refused at connection instead of silently desyncing.
/// </summary>
internal static class SyncProtocol
{
    internal const ushort Version = 1;
}
