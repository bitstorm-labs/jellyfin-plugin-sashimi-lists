using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset Tick(int minutes = 1)
    {
        Now = Now.AddMinutes(minutes);
        return Now;
    }
}

/// <summary>Answers lookups from a table of (user, tmdb id) → item id. Users not in the table see nothing.</summary>
internal sealed class FakeLibrary : ILibraryLookup
{
    public Dictionary<(Guid User, string Tmdb), string> Visible { get; } = [];

    public string? FindVisibleItemId(Guid userId, WatchlistType type, ExternalIds ids)
        => ids.Tmdb is not null && Visible.TryGetValue((userId, ids.Tmdb), out var itemId) ? itemId : null;
}

/// <summary>A real file store in a temp directory, removed on dispose.</summary>
internal sealed class TempStore : IDisposable
{
    public TempStore()
    {
        Directory = Path.Combine(Path.GetTempPath(), "sashimi-watchlist-tests-" + Guid.NewGuid().ToString("N"));
        Store = new FileWatchlistStore(Directory);
    }

    public string Directory { get; }

    public FileWatchlistStore Store { get; }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

/// <summary>
/// An in-memory plex.tv: a catalog of titles (ratingKey → item + ids) and one
/// account's watchlist. Records every Add/Remove call.
/// </summary>
internal sealed class FakePlexApi : IPlexWatchlistApi
{
    public const string Token = "good-token";

    public Dictionary<string, (PlexItem Item, ExternalIds Ids)> Catalog { get; } = [];

    public List<string> Watchlist { get; } = [];

    /// <summary>Extra ids FindRatingKey matches on but GetIds doesn't report (Plex's matcher knows more than the Guid list).</summary>
    public Dictionary<string, ExternalIds> MatchAliases { get; } = [];

    public List<string> Added { get; } = [];

    public List<string> Removed { get; } = [];

    public PlexAccount Account { get; set; } = new("uuid-alice", "alice");

    /// <summary>When set, GetWatchlist throws it.</summary>
    public Exception? WatchlistFailure { get; set; }

    /// <summary>When true, every call throws <see cref="PlexUnauthorizedException"/>.</summary>
    public bool Revoked { get; set; }

    /// <summary>When set, GetWatchlist signals <see cref="Entered"/> and waits for this before answering.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, Add signals <see cref="AddEntered"/> and waits for this before recording the add.</summary>
    public TaskCompletionSource? AddGate { get; set; }

    public TaskCompletionSource AddEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Title(string key, WatchlistType type, string title, ExternalIds ids, bool onWatchlist = false)
    {
        Catalog[key] = (new PlexItem(key, type, title, 2020, "https://thumb.example/" + key), ids);
        if (onWatchlist)
        {
            Watchlist.Add(key);
        }
    }

    public Task<PlexAccount> GetAccount(string token, CancellationToken ct)
    {
        Check(token);
        return Task.FromResult(Account);
    }

    public int WatchlistCalls;

    public async Task<IReadOnlyList<PlexItem>> GetWatchlist(string token, CancellationToken ct)
    {
        Check(token);
        Interlocked.Increment(ref WatchlistCalls);
        if (Gate is not null)
        {
            Entered.TrySetResult();
            await Gate.Task.ConfigureAwait(false);
        }

        if (WatchlistFailure is not null)
        {
            throw WatchlistFailure;
        }

        return Watchlist.Select(k => Catalog[k].Item).ToList();
    }

    public Task<ExternalIds> GetIds(string token, string ratingKey, CancellationToken ct)
    {
        Check(token);
        return Task.FromResult(Catalog[ratingKey].Ids);
    }

    public Task<string?> FindRatingKey(string token, WatchlistType type, ExternalIds ids, CancellationToken ct)
    {
        Check(token);
        var hit = Catalog.FirstOrDefault(c => c.Value.Item.Type == type
            && (c.Value.Ids.Agrees(ids) || (MatchAliases.TryGetValue(c.Key, out var alias) && alias.Agrees(ids))));
        return Task.FromResult<string?>(hit.Key);
    }

    public async Task Add(string token, string ratingKey, CancellationToken ct)
    {
        Check(token);
        if (AddGate is not null)
        {
            AddEntered.TrySetResult();
            await AddGate.Task.ConfigureAwait(false);
        }

        Added.Add(ratingKey);
        if (!Watchlist.Contains(ratingKey))
        {
            Watchlist.Add(ratingKey);
        }
    }

    public Task Remove(string token, string ratingKey, CancellationToken ct)
    {
        Check(token);
        Removed.Add(ratingKey);
        Watchlist.Remove(ratingKey);
        return Task.CompletedTask;
    }

    private void Check(string token)
    {
        if (Revoked || token != Token)
        {
            throw new PlexUnauthorizedException();
        }
    }
}
