using System.Collections.Concurrent;

namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>
/// Two-way sync between one user's watchlist and their Plex watchlist.
/// Network calls happen outside <see cref="IWatchlistStore.Update{T}"/>; only the
/// final merge runs inside it, against freshly read state, so API writes made
/// while a sync is in flight survive.
/// </summary>
public class PlexSync
{
    private readonly IWatchlistStore _store;
    private readonly IPlexWatchlistApi _plex;
    private readonly IClock _clock;

    // One sync per user at a time: the scheduled run and a link-triggered run
    // would otherwise both push the same outbox.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _running = new();

    private readonly Action<Guid, int>? _onUnmatched;

    /// <param name="store">Watchlist storage.</param>
    /// <param name="plex">plex.tv.</param>
    /// <param name="clock">The time.</param>
    /// <param name="onUnmatched">Told how many Plex titles a run couldn't import yet (no external ids); they are retried next run.</param>
    public PlexSync(IWatchlistStore store, IPlexWatchlistApi plex, IClock clock, Action<Guid, int>? onUnmatched = null)
    {
        _store = store;
        _plex = plex;
        _clock = clock;
        _onUnmatched = onUnmatched;
    }

    /// <summary>
    /// Syncs one user. Does nothing when the user isn't linked or needs to
    /// reconnect. A rejected token marks the link <c>NeedsReconnect</c>; any other
    /// failure throws and saves nothing, so the next run starts from the last
    /// good snapshot.
    /// </summary>
    public async Task SyncUser(Guid user, CancellationToken ct)
    {
        var gate = _running.GetOrAdd(user, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SyncUserLocked(user, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SyncUserLocked(Guid user, CancellationToken ct)
    {
        var state = _store.Read(user);
        var link = state.Plex;
        if (link is null || link.NeedsReconnect)
        {
            return;
        }

        try
        {
            var plan = await Plan(state, link, ct).ConfigureAwait(false);
            var applied = _store.Update(user, w => Merge(w, link, plan));
            if (applied && plan.Unmatched.Count > 0)
            {
                _onUnmatched?.Invoke(user, plan.Unmatched.Count);
            }
        }
        catch (PlexUnauthorizedException)
        {
            _store.Update(user, w =>
            {
                if (SameLink(w.Plex, link))
                {
                    w.Plex!.NeedsReconnect = true;
                }

                return 0;
            });
        }
    }

    private async Task<SyncPlan> Plan(UserWatchlist state, PlexLinkState link, CancellationToken ct)
    {
        var token = link.Token;
        var listed = await _plex.GetWatchlist(token, ct).ConfigureAwait(false);
        var plexItems = new Dictionary<string, PlexItem>(StringComparer.Ordinal);
        foreach (var item in listed)
        {
            plexItems[item.RatingKey] = item;
        }

        var onPlex = new HashSet<string>(plexItems.Keys, StringComparer.Ordinal);
        var plan = new SyncPlan(onPlex);
        var entries = state.Entries;

        // The key each entry holds after this run: stored ones plus new assignments.
        string? KeyOf(WatchlistEntry e) => plan.Keys.TryGetValue(e.Id, out var k) ? k : e.PlexRatingKey;
        bool Held(string key) => entries.Any(e => KeyOf(e) == key);

        async Task PushEntry(WatchlistEntry entry)
        {
            var key = KeyOf(entry) ?? await _plex.FindRatingKey(token, entry.Type, entry.Ids, ct).ConfigureAwait(false);
            if (key is null)
            {
                return; // Plex doesn't know the title, so it can't be on a Plex watchlist.
            }

            if (!onPlex.Contains(key))
            {
                await _plex.Add(token, key, ct).ConfigureAwait(false);
                onPlex.Add(key);
            }

            plan.Keys[entry.Id] = key;
        }

        // 1. Outbox, in order.
        foreach (var op in state.Outbox)
        {
            switch (op)
            {
                case PushAdd add:
                    var entry = entries.FirstOrDefault(e => e.Id == add.EntryId);
                    if (entry is not null)
                    {
                        await PushEntry(entry).ConfigureAwait(false);
                    }

                    break;
                case PushRemove remove:
                    // Another entry still holds this title (re-added, or a second entry
                    // Plex resolves to the same title): it stays on Plex.
                    if (Held(remove.RatingKey))
                    {
                        break;
                    }

                    if (onPlex.Contains(remove.RatingKey))
                    {
                        await _plex.Remove(token, remove.RatingKey, ct).ConfigureAwait(false);
                    }

                    onPlex.Remove(remove.RatingKey);
                    break;
            }

            plan.ProcessedOps.Add(op);
        }

        // 2. Reconcile.
        var newOnPlex = link.FirstSyncDone
            ? onPlex.Where(k => !state.LastPlexSnapshot.Contains(k) && plexItems.ContainsKey(k)).ToList()
            : onPlex.Where(plexItems.ContainsKey).ToList();

        foreach (var key in newOnPlex.Where(k => !Held(k)))
        {
            var item = plexItems[key];
            var ids = await _plex.GetIds(token, key, ct).ConfigureAwait(false);
            var match = entries.FirstOrDefault(e =>
                (KeyOf(e) is null || !onPlex.Contains(KeyOf(e)!))
                && WatchlistService.SameTitle(e.Type, e.Ids, item.Type, ids));
            if (match is not null)
            {
                plan.Keys[match.Id] = key;
            }
            else if (!ids.Any)
            {
                // Plex hasn't matched it to TMDB/TVDB/IMDb yet. Leave it out of the
                // snapshot so the next run looks at it again.
                plan.Unmatched.Add(key);
            }
            else
            {
                plan.Imports.Add(new WatchlistEntry
                {
                    Id = Guid.NewGuid(),
                    Type = item.Type,
                    Title = item.Title,
                    Year = item.Year,
                    PosterUrl = PosterUrls.Clean(item.Thumb),
                    Ids = ids,
                    AddedAt = _clock.Now,
                    PlexRatingKey = key,
                });
            }
        }

        if (!link.FirstSyncDone)
        {
            // Union: every local title goes to Plex too. Nothing is removed on this pass.
            foreach (var entry in entries.Where(e => KeyOf(e) is not { } k || !onPlex.Contains(k)))
            {
                await PushEntry(entry).ConfigureAwait(false);
            }

            // Plex's matcher can tie a local entry to a Plex item whose reported ids
            // share none with it; that item is the entry, not a new title.
            plan.Imports.RemoveAll(i => entries.Any(e => KeyOf(e) == i.PlexRatingKey));
        }
        else
        {
            // Judge removals by what Plex reported, not by the set this run's own
            // PushRemove ops already changed.
            foreach (var key in state.LastPlexSnapshot.Where(k => !plexItems.ContainsKey(k)))
            {
                plan.RemovedOnPlex.Add(key);
            }
        }

        return plan;
    }

    /// <summary>The stored link is still the one a sync started under (not unlinked, relinked or switched since).</summary>
    private static bool SameLink(PlexLinkState? current, PlexLinkState started)
        => current is not null && current.LinkId == started.LinkId && current.AccountUuid == started.AccountUuid;

    private static bool Merge(UserWatchlist w, PlexLinkState link, SyncPlan plan)
    {
        // Unlinked, relinked or switched account while this sync ran: its results don't apply.
        if (!SameLink(w.Plex, link) || w.Plex!.NeedsReconnect)
        {
            return false;
        }

        foreach (var (entryId, key) in plan.Keys)
        {
            var entry = w.Entries.FirstOrDefault(e => e.Id == entryId);
            if (entry is not null)
            {
                entry.PlexRatingKey = key;
            }
            else if (plan.OnPlex.Contains(key) && !w.Entries.Any(e => e.PlexRatingKey == key))
            {
                // Removed locally while this sync was pushing it: take it back off Plex next run.
                w.Outbox.Add(new PushRemove(key));
            }
        }

        w.Entries.RemoveAll(e => e.PlexRatingKey is not null && plan.RemovedOnPlex.Contains(e.PlexRatingKey));

        foreach (var import in plan.Imports)
        {
            var existing = w.Entries.FirstOrDefault(e =>
                e.PlexRatingKey == import.PlexRatingKey || WatchlistService.SameTitle(e.Type, e.Ids, import.Type, import.Ids));
            if (existing is null)
            {
                w.Entries.Add(import);
            }
            else
            {
                existing.PlexRatingKey ??= import.PlexRatingKey;
            }
        }

        foreach (var op in plan.ProcessedOps)
        {
            w.Outbox.Remove(op);
        }

        w.Plex.FirstSyncDone = true;
        w.LastPlexSnapshot = new HashSet<string>(plan.OnPlex.Where(k => !plan.Unmatched.Contains(k)), StringComparer.Ordinal);
        return true;
    }

    private sealed class SyncPlan(HashSet<string> onPlex)
    {
        public HashSet<string> OnPlex { get; } = onPlex;

        public Dictionary<Guid, string> Keys { get; } = [];

        public List<WatchlistEntry> Imports { get; } = [];

        public HashSet<string> RemovedOnPlex { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Unmatched { get; } = new(StringComparer.Ordinal);

        public List<PlexOp> ProcessedOps { get; } = [];
    }
}
