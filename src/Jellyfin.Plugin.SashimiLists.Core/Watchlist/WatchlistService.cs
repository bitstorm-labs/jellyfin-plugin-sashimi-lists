namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>What a client sends to add a title.</summary>
public sealed record NewEntry(WatchlistType Type, string Title, int? Year, string? PosterUrl, ExternalIds Ids);

/// <summary>One entry as the caller sees it.</summary>
public sealed record EntryView(
    Guid Id,
    WatchlistType Type,
    string Title,
    int? Year,
    string? PosterUrl,
    ExternalIds Ids,
    DateTimeOffset AddedAt,
    string? ItemId);

/// <summary>The Plex link as the caller sees it. Never carries the token.</summary>
public sealed record PlexLinkView(string Username, bool NeedsReconnect);

/// <summary>The caller's whole watchlist.</summary>
public sealed record WatchlistViewModel(PlexLinkView? Plex, bool CardDismissed, IReadOnlyList<EntryView> Entries);

/// <summary>Adds, removes and shows one user's watchlist. Every call acts on the given user's list only.</summary>
public class WatchlistService
{
    private readonly IWatchlistStore _store;
    private readonly ILibraryLookup _library;
    private readonly IClock _clock;

    public WatchlistService(IWatchlistStore store, ILibraryLookup library, IClock clock)
    {
        _store = store;
        _library = library;
        _clock = clock;
    }

    /// <summary>True when two titles are the same: same type and any agreeing id.</summary>
    public static bool SameTitle(WatchlistType typeA, ExternalIds idsA, WatchlistType typeB, ExternalIds idsB)
        => typeA == typeB && idsA.Agrees(idsB);

    public WatchlistViewModel View(Guid user)
    {
        var watchlist = _store.Read(user);
        var entries = watchlist.Entries
            .OrderByDescending(e => e.AddedAt)
            .Select(e => ToView(user, e))
            .ToList();
        var plex = watchlist.Plex is null ? null : new PlexLinkView(watchlist.Plex.Username, watchlist.Plex.NeedsReconnect);
        return new WatchlistViewModel(plex, watchlist.CardDismissed, entries);
    }

    /// <summary>
    /// Adds the title, or returns the entry already holding it (any agreeing id)
    /// with <c>Added = false</c>. Throws <see cref="ArgumentException"/> when the
    /// title has no external id.
    /// </summary>
    public (EntryView Entry, bool Added) Add(Guid user, NewEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Ids is null || !entry.Ids.Any)
        {
            throw new ArgumentException("A watchlist entry needs a TMDB, TVDB or IMDb id.", nameof(entry));
        }

        var (stored, added) = _store.Update(user, w =>
        {
            var existing = w.Entries.FirstOrDefault(e => SameTitle(e.Type, e.Ids, entry.Type, entry.Ids));
            if (existing is not null)
            {
                return (existing, false);
            }

            var created = new WatchlistEntry
            {
                Id = Guid.NewGuid(),
                Type = entry.Type,
                Title = entry.Title,
                Year = entry.Year,
                PosterUrl = entry.PosterUrl,
                Ids = entry.Ids,
                AddedAt = _clock.Now,
            };
            w.Entries.Add(created);
            if (w.Plex is not null)
            {
                w.Outbox.Add(new PushAdd(created.Id));
            }

            return (created, true);
        });

        return (ToView(user, stored), added);
    }

    /// <summary>Removes the entry; does nothing when it is already gone.</summary>
    public void Remove(Guid user, Guid entryId)
    {
        _store.Update(user, w =>
        {
            RemoveIn(w, entryId);
            return 0;
        });
    }

    public void DismissCard(Guid user)
    {
        _store.Update(user, w =>
        {
            w.CardDismissed = true;
            return 0;
        });
    }

    /// <summary>Removes an entry inside an update, queueing a Plex removal when it was synced.</summary>
    internal static void RemoveIn(UserWatchlist w, Guid entryId)
    {
        var entry = w.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null)
        {
            return;
        }

        w.Entries.Remove(entry);
        if (w.Plex is not null && entry.PlexRatingKey is not null)
        {
            w.Outbox.Add(new PushRemove(entry.PlexRatingKey));
        }
    }

    private EntryView ToView(Guid user, WatchlistEntry e) => new(
        e.Id,
        e.Type,
        e.Title,
        e.Year,
        e.PosterUrl,
        e.Ids,
        e.AddedAt,
        _library.FindVisibleItemId(user, e.Type, e.Ids));
}
