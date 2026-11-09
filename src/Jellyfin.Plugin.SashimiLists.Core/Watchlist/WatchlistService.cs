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

/// <summary>The outcome of linking a Plex account.</summary>
public abstract record LinkResult;

/// <summary>Linked (or the same account's token refreshed).</summary>
public sealed record Linked(PlexLinkView Link) : LinkResult;

/// <summary>plex.tv rejected the token.</summary>
public sealed record Rejected : LinkResult;

/// <summary>
/// Already linked to a different Plex account and the caller didn't ask to replace it.
/// <paramref name="Username"/> is the linked account, <paramref name="NewUsername"/> the token's.
/// </summary>
public sealed record ConflictsWith(string Username, string NewUsername) : LinkResult;

/// <summary>Adds, removes and shows one user's watchlist. Every call acts on the given user's list only.</summary>
public class WatchlistService
{
    private readonly IWatchlistStore _store;
    private readonly ILibraryLookup _library;
    private readonly IClock _clock;
    private readonly IPlexWatchlistApi _plex;

    public WatchlistService(IWatchlistStore store, ILibraryLookup library, IClock clock, IPlexWatchlistApi plex)
    {
        _store = store;
        _library = library;
        _clock = clock;
        _plex = plex;
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
                PosterUrl = PosterUrls.Clean(entry.PosterUrl),
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

    /// <summary>
    /// Links the user's own Plex account. A new account starts with a union-only
    /// first sync; re-linking the same account (by uuid) only refreshes the token
    /// and clears <c>NeedsReconnect</c>.
    /// </summary>
    public async Task<LinkResult> LinkPlex(Guid user, string token, bool replace, CancellationToken ct)
    {
        PlexAccount account;
        try
        {
            account = await _plex.GetAccount(token, ct).ConfigureAwait(false);
        }
        catch (PlexUnauthorizedException)
        {
            return new Rejected();
        }

        return _store.Update<LinkResult>(user, w =>
        {
            if (w.Plex is not null && w.Plex.AccountUuid == account.Uuid)
            {
                w.Plex.Token = token;
                w.Plex.Username = account.Username;
                w.Plex.NeedsReconnect = false;
            }
            else if (w.Plex is not null && !replace)
            {
                return new ConflictsWith(w.Plex.Username, account.Username);
            }
            else
            {
                w.Plex = new PlexLinkState { Token = token, AccountUuid = account.Uuid, Username = account.Username };
                // Pending pushes and the snapshot belong to the previous account.
                w.Outbox.Clear();
                w.LastPlexSnapshot.Clear();
            }

            return new Linked(new PlexLinkView(w.Plex.Username, w.Plex.NeedsReconnect));
        });
    }

    /// <summary>Stops syncing. The entries stay as they are.</summary>
    public void UnlinkPlex(Guid user)
    {
        _store.Update(user, w =>
        {
            w.Plex = null;
            w.Outbox.Clear();
            w.LastPlexSnapshot.Clear();
            return 0;
        });
    }

    /// <summary>
    /// The user finished a title (a movie played, or a show's last episode):
    /// entries for it that were added before that play drop off, and come off
    /// Plex too when linked.
    /// </summary>
    public void MarkFinished(Guid user, WatchlistType type, ExternalIds ids, DateTimeOffset playedAt)
    {
        bool Finished(WatchlistEntry e) => SameTitle(e.Type, e.Ids, type, ids) && DropOff.ShouldRemove(e, playedAt);

        // Every play by every user lands here; most touch no watchlist entry, so don't write.
        if (!_store.Read(user).Entries.Any(Finished))
        {
            return;
        }

        _store.Update(user, w =>
        {
            var finished = w.Entries.Where(Finished).Select(e => e.Id).ToList();
            foreach (var id in finished)
            {
                RemoveIn(w, id);
            }

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
