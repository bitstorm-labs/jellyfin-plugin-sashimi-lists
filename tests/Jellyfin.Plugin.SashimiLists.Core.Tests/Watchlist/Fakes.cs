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
