namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>Why a user's play state was saved, reduced to what drop-off cares about.</summary>
public enum PlayedSave
{
    /// <summary>Start, progress, rating, import: never a finish (a rewatch's progress still says Played).</summary>
    Other,
    PlaybackFinished,
    MarkedPlayed,
    UserDataUpdate,
}

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

    /// <summary>When this save means the title was finished, or null when it doesn't count.</summary>
    public static DateTimeOffset? FinishedAt(PlayedSave save, bool played, DateTimeOffset? lastPlayed, DateTimeOffset now)
    {
        if (!played)
        {
            return null;
        }

        return save switch
        {
            PlayedSave.PlaybackFinished or PlayedSave.MarkedPlayed => lastPlayed ?? now,
            // A generic user-data post carries Played=true for anything watched
            // before; without a play date it says nothing about a new finish.
            PlayedSave.UserDataUpdate => lastPlayed,
            _ => null,
        };
    }
}
