namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>What a playlist item is, as far as the merge cares.</summary>
public enum PlaylistItemKind
{
    Movie,
    Series,
    Season,
    Episode,

    /// <summary>Anything else (music, home video folders, ...). Never merged.</summary>
    Other,
}

/// <summary>A title's name, year and provider ids as the library has them.</summary>
public sealed record TitleInfo(string Title, int? Year, ExternalIds Ids);

/// <summary>
/// One item of an old Watchlist playlist. <paramref name="Series"/> is the show a
/// season or episode belongs to (null for anything else, or when Jellyfin lost it).
/// </summary>
public sealed record PlaylistItem(string ItemId, PlaylistItemKind Kind, TitleInfo Own, TitleInfo? Series);

/// <summary>An old Watchlist playlist with its items in playlist order.</summary>
public sealed record OldPlaylist(Guid Id, IReadOnlyList<PlaylistItem> Items);

/// <summary>The user's own Jellyfin playlists, for the one-time merge. The plugin shell backs this with Jellyfin.</summary>
public interface IOldWatchlistPlaylists
{
    /// <summary>Playlists <paramref name="userId"/> owns whose name passes <paramref name="isWatchlistName"/>.</summary>
    IReadOnlyList<OldPlaylist> FindOwned(Guid userId, Func<string, bool> isWatchlistName);

    /// <summary>Deletes the playlist; a playlist that is already gone is not an error.</summary>
    void Delete(Guid playlistId);
}

/// <summary>What a merge did. <see cref="Ran"/> is false when it had already run.</summary>
public sealed record PlaylistMergeResult(
    bool Ran,
    int Added,
    IReadOnlyList<string> SkippedItemIds,
    IReadOnlyList<(Guid PlaylistId, Exception Error)> DeleteFailures)
{
    public static readonly PlaylistMergeResult NotRun = new(false, 0, [], []);
}

/// <summary>
/// Moves a user's old "Watchlist" playlist(s) into their Watchlist, once.
/// Entries and the done flag are saved in one store update, so a failure before
/// that point leaves nothing behind and the next call retries. Deleting the
/// playlists comes after; a failed delete is reported but the merge stays done.
/// </summary>
public sealed class PlaylistMerger
{
    private readonly IWatchlistStore _store;
    private readonly IClock _clock;
    private readonly IOldWatchlistPlaylists _playlists;

    public PlaylistMerger(IWatchlistStore store, IClock clock, IOldWatchlistPlaylists playlists)
    {
        _store = store;
        _clock = clock;
        _playlists = playlists;
    }

    /// <summary>"watchlist", ignoring case and surrounding spaces.</summary>
    public static bool IsWatchlistName(string? name)
        => string.Equals(name?.Trim(), "watchlist", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Watchlist title a playlist item stands for: movies and series as they are,
    /// seasons and episodes as their series. Null when it can't go on the Watchlist
    /// (other kinds, or no TMDB/TVDB/IMDb id).
    /// </summary>
    public static (WatchlistType Type, TitleInfo Title)? ToTitle(PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        (WatchlistType Type, TitleInfo? Title) mapped = item.Kind switch
        {
            PlaylistItemKind.Movie => (WatchlistType.Movie, item.Own),
            PlaylistItemKind.Series => (WatchlistType.Series, item.Own),
            PlaylistItemKind.Season or PlaylistItemKind.Episode => (WatchlistType.Series, item.Series),
            _ => (WatchlistType.Movie, null),
        };

        if (mapped.Title is null || mapped.Title.Ids is null || !mapped.Title.Ids.Any || string.IsNullOrWhiteSpace(mapped.Title.Title))
        {
            return null;
        }

        return (mapped.Type, mapped.Title);
    }

    /// <summary>Runs the merge unless it already ran for this user. Exceptions before the save propagate.</summary>
    public PlaylistMergeResult MergeOnce(Guid user)
    {
        if (_store.Read(user).PlaylistMerged)
        {
            return PlaylistMergeResult.NotRun;
        }

        var playlists = _playlists.FindOwned(user, IsWatchlistName);
        var titles = new List<(WatchlistType Type, TitleInfo Title)>();
        var skipped = new List<string>();
        foreach (var item in playlists.SelectMany(p => p.Items))
        {
            if (ToTitle(item) is { } title)
            {
                titles.Add(title);
            }
            else
            {
                skipped.Add(item.ItemId);
            }
        }

        var added = _store.Update(user, w =>
        {
            if (w.PlaylistMerged)
            {
                // A concurrent first GET got here first.
                return -1;
            }

            // Newest-first is the view's order, so the first playlist item gets the
            // latest time. Whole seconds apart: addedAt goes out with second precision.
            var now = _clock.Now;
            var count = 0;
            foreach (var (type, title) in titles)
            {
                if (w.Entries.Any(e => WatchlistService.SameTitle(e.Type, e.Ids, type, title.Ids)))
                {
                    continue;
                }

                var entry = new WatchlistEntry
                {
                    Id = Guid.NewGuid(),
                    Type = type,
                    Title = title.Title.Trim(),
                    Year = title.Year,
                    Ids = title.Ids,
                    AddedAt = now.AddSeconds(-count),
                };
                w.Entries.Add(entry);
                if (w.Plex is not null)
                {
                    w.Outbox.Add(new PushAdd(entry.Id));
                }

                count++;
            }

            w.PlaylistMerged = true;
            return count;
        });

        if (added < 0)
        {
            return PlaylistMergeResult.NotRun;
        }

        var failures = new List<(Guid, Exception)>();
        foreach (var playlist in playlists)
        {
            try
            {
                _playlists.Delete(playlist.Id);
            }
            catch (Exception e)
            {
                failures.Add((playlist.Id, e));
            }
        }

        return new PlaylistMergeResult(true, added, skipped, failures);
    }
}
