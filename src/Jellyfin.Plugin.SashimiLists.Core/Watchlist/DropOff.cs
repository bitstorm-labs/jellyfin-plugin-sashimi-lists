namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>When a finished title leaves the watchlist.</summary>
public static class DropOff
{
    /// <summary>
    /// Only a play newer than the add removes the entry, so re-adding something
    /// already watched (a rewatch) keeps it.
    /// </summary>
    public static bool ShouldRemove(WatchlistEntry e, DateTimeOffset playedAt)
    {
        ArgumentNullException.ThrowIfNull(e);
        return playedAt > e.AddedAt;
    }
}
