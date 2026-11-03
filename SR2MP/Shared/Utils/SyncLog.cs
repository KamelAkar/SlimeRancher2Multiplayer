using MelonLoader;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Optional log of actor ownership and shared storage decisions, to diagnose desyncs in real games.
/// Off by default, toggled with the <c>sync_log</c> preference.
/// </summary>
internal static class SyncLog
{
    private const string CategoryName = "SR2MP";
    private const string EntryName = "sync_log";

    private static MelonPreferences_Entry<bool>? entry;

    internal static bool Enabled => (entry ??= FindOrCreateEntry())?.Value ?? false;

    internal static void Write(string message)
    {
        if (Enabled)
            SrLogger.LogMessage($"[Sync] {message}");
    }

    private static MelonPreferences_Entry<bool>? FindOrCreateEntry()
    {
        var category = MelonPreferences.GetCategory(CategoryName);

        return category?.GetEntry<bool>(EntryName)
               ?? category?.CreateEntry(EntryName, false, display_name: "Sync Logging",
                   description: "Logs actor ownership and shared storage sync decisions (debug setting)");
    }
}
