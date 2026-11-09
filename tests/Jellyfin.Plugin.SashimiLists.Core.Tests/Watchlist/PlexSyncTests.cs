using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

public sealed class PlexSyncTests : IDisposable
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private readonly TempStore _temp = new();
    private readonly FakeClock _clock = new();
    private readonly FakePlexApi _plex = new();
    private readonly WatchlistService _service;
    private readonly PlexSync _sync;

    public PlexSyncTests()
    {
        _service = new WatchlistService(_temp.Store, new FakeLibrary(), _clock, _plex);
        _sync = new PlexSync(_temp.Store, _plex, _clock);
    }

    public void Dispose() => _temp.Dispose();

    private static NewEntry Movie(string title, string tmdb) => new(WatchlistType.Movie, title, 2020, null, new ExternalIds(tmdb, null, null));

    private void Link(bool firstSyncDone, bool needsReconnect = false) => _temp.Store.Update(Alice, w =>
    {
        w.Plex = new PlexLinkState
        {
            Token = FakePlexApi.Token,
            AccountUuid = "uuid-alice",
            Username = "alice",
            FirstSyncDone = firstSyncDone,
            NeedsReconnect = needsReconnect,
        };
        return 0;
    });

    private UserWatchlist Stored => _temp.Store.Read(Alice);

    private async Task LinkedAndSynced()
    {
        Link(firstSyncDone: false);
        await _sync.SyncUser(Alice, CancellationToken.None);
    }

    [Fact]
    public async Task First_sync_unions_both_sides_and_deletes_nothing()
    {
        // Local A, B (added before linking, so no outbox); Plex B' (agrees with B by imdb), C.
        _service.Add(Alice, Movie("A", tmdb: "1"));
        _service.Add(Alice, new NewEntry(WatchlistType.Movie, "B", 2020, null, new ExternalIds("2", null, "tt0002")));
        _plex.Title("kA", WatchlistType.Movie, "A", new ExternalIds("1", null, null));
        _plex.Title("kB", WatchlistType.Movie, "B'", new ExternalIds(null, null, "TT0002"), onWatchlist: true);
        _plex.Title("kC", WatchlistType.Movie, "C", new ExternalIds("3", null, "tt0003"), onWatchlist: true);
        Link(firstSyncDone: false);

        await _sync.SyncUser(Alice, CancellationToken.None);

        var stored = Stored;
        Assert.Equal(["A", "B", "C"], stored.Entries.Select(e => e.Title).Order());
        Assert.Equal("kB", stored.Entries.Single(e => e.Title == "B").PlexRatingKey);
        Assert.Equal("kA", stored.Entries.Single(e => e.Title == "A").PlexRatingKey);
        Assert.Equal(["kA"], _plex.Added);
        Assert.Empty(_plex.Removed);
        Assert.Equal(["kA", "kB", "kC"], _plex.Watchlist.Order());
        Assert.True(stored.Plex!.FirstSyncDone);
        Assert.Equal(["kA", "kB", "kC"], stored.LastPlexSnapshot.Order());
    }

    [Fact]
    public async Task Item_added_on_Plex_is_imported_with_external_ids()
    {
        await LinkedAndSynced();
        _plex.Title("kX", WatchlistType.Series, "Severance", new ExternalIds("95396", "371980", "tt11280740"), onWatchlist: true);
        _clock.Tick(5);

        await _sync.SyncUser(Alice, CancellationToken.None);

        var entry = Assert.Single(Stored.Entries);
        Assert.Equal(WatchlistType.Series, entry.Type);
        Assert.Equal("Severance", entry.Title);
        Assert.Equal(new ExternalIds("95396", "371980", "tt11280740"), entry.Ids);
        Assert.Equal("kX", entry.PlexRatingKey);
        Assert.Equal("https://thumb.example/kX", entry.PosterUrl);
        Assert.Equal(_clock.Now, entry.AddedAt);
        Assert.Empty(_plex.Added);
    }

    [Fact]
    public async Task Item_removed_on_Plex_after_first_sync_is_removed_locally()
    {
        _plex.Title("kX", WatchlistType.Movie, "X", new ExternalIds("9", null, null), onWatchlist: true);
        _plex.Title("kY", WatchlistType.Movie, "Y", new ExternalIds("8", null, null), onWatchlist: true);
        await LinkedAndSynced();
        Assert.Equal(2, Stored.Entries.Count);

        _plex.Watchlist.Remove("kX");
        await _sync.SyncUser(Alice, CancellationToken.None);

        Assert.Equal("Y", Assert.Single(Stored.Entries).Title);
        Assert.Empty(Stored.Outbox);
        Assert.Empty(_plex.Removed);
    }

    [Fact]
    public async Task Local_add_is_pushed_and_records_ratingKey()
    {
        _plex.Title("kD", WatchlistType.Movie, "Dune", new ExternalIds("438631", null, null));
        await LinkedAndSynced();

        var added = _service.Add(Alice, Movie("Dune", tmdb: "438631")).Entry;
        await _sync.SyncUser(Alice, CancellationToken.None);

        Assert.Equal(["kD"], _plex.Added);
        Assert.Equal("kD", Stored.Entries.Single(e => e.Id == added.Id).PlexRatingKey);
        Assert.Empty(Stored.Outbox);
        Assert.Contains("kD", Stored.LastPlexSnapshot);

        // The next sync sees kD as already known: no duplicate import.
        await _sync.SyncUser(Alice, CancellationToken.None);
        Assert.Single(Stored.Entries);
    }

    [Fact]
    public async Task Local_remove_is_pushed_to_Plex()
    {
        _plex.Title("kX", WatchlistType.Movie, "X", new ExternalIds("9", null, null), onWatchlist: true);
        await LinkedAndSynced();

        _service.Remove(Alice, Stored.Entries.Single().Id);
        await _sync.SyncUser(Alice, CancellationToken.None);

        Assert.Equal(["kX"], _plex.Removed);
        Assert.Empty(_plex.Watchlist);
        Assert.Empty(Stored.Entries);
        Assert.Empty(Stored.Outbox);
        Assert.Empty(Stored.LastPlexSnapshot);
    }

    [Fact]
    public async Task PushAdd_for_title_Plex_cannot_find_is_dropped_without_error()
    {
        await LinkedAndSynced();
        var local = _service.Add(Alice, Movie("Obscure", tmdb: "424242")).Entry;

        await _sync.SyncUser(Alice, CancellationToken.None);

        Assert.Empty(_plex.Added);
        Assert.Empty(Stored.Outbox);
        var entry = Assert.Single(Stored.Entries);
        Assert.Equal(local.Id, entry.Id);
        Assert.Null(entry.PlexRatingKey);
    }

    [Fact]
    public async Task GetWatchlist_failure_saves_nothing_and_keeps_outbox()
    {
        _plex.Title("kX", WatchlistType.Movie, "X", new ExternalIds("9", null, null), onWatchlist: true);
        _plex.Title("kD", WatchlistType.Movie, "Dune", new ExternalIds("438631", null, null));
        await LinkedAndSynced();
        var added = _service.Add(Alice, Movie("Dune", tmdb: "438631")).Entry;
        var before = File.ReadAllText(Path.Combine(_temp.Directory, Alice.ToString("N") + ".json"));

        _plex.WatchlistFailure = new HttpRequestException("503");
        await Assert.ThrowsAsync<HttpRequestException>(() => _sync.SyncUser(Alice, CancellationToken.None));

        Assert.Equal(before, File.ReadAllText(Path.Combine(_temp.Directory, Alice.ToString("N") + ".json")));
        Assert.Equal<PlexOp>([new PushAdd(added.Id)], Stored.Outbox);
        Assert.Equal(["kX"], Stored.LastPlexSnapshot);
        Assert.Equal(2, Stored.Entries.Count);
        Assert.Empty(_plex.Added);
    }

    [Fact]
    public async Task Unauthorized_sets_needsReconnect_and_changes_no_entries()
    {
        _plex.Title("kX", WatchlistType.Movie, "X", new ExternalIds("9", null, null), onWatchlist: true);
        await LinkedAndSynced();
        _service.Add(Alice, Movie("Local", tmdb: "5"));
        var entriesBefore = Stored.Entries.Select(e => (e.Id, e.PlexRatingKey)).ToList();

        _plex.Revoked = true;
        await _sync.SyncUser(Alice, CancellationToken.None);

        var stored = Stored;
        Assert.True(stored.Plex!.NeedsReconnect);
        Assert.Equal(entriesBefore, stored.Entries.Select(e => (e.Id, e.PlexRatingKey)).ToList());
        Assert.Single(stored.Outbox);
        Assert.Empty(_plex.Removed);
    }

    [Fact]
    public async Task Entry_added_through_service_during_sync_survives()
    {
        _plex.Title("kD", WatchlistType.Movie, "Dune", new ExternalIds("438631", null, null));
        await LinkedAndSynced();
        _plex.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var syncing = _sync.SyncUser(Alice, CancellationToken.None);
        await _plex.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var added = _service.Add(Alice, Movie("Dune", tmdb: "438631")).Entry;
        _plex.Gate.SetResult();
        await syncing.WaitAsync(TimeSpan.FromSeconds(5));

        var stored = Stored;
        Assert.Equal(added.Id, Assert.Single(stored.Entries).Id);
        Assert.Equal<PlexOp>([new PushAdd(added.Id)], stored.Outbox);
        Assert.Empty(_plex.Added);
    }

    [Fact]
    public async Task NeedsReconnect_link_is_not_synced()
    {
        _plex.Title("kX", WatchlistType.Movie, "X", new ExternalIds("9", null, null), onWatchlist: true);
        _plex.Title("kL", WatchlistType.Movie, "Local", new ExternalIds("5", null, null));
        _service.Add(Alice, Movie("Local", tmdb: "5"));
        Link(firstSyncDone: false, needsReconnect: true);

        await _sync.SyncUser(Alice, CancellationToken.None);

        Assert.Empty(_plex.Added); // nothing is sent to Plex with a token it rejected
        Assert.Equal(["Local"], Stored.Entries.Select(e => e.Title));
        Assert.Empty(Stored.LastPlexSnapshot);
        Assert.False(Stored.Plex!.FirstSyncDone);
    }

    [Fact]
    public async Task Entry_removed_locally_while_its_PushAdd_was_in_flight_is_taken_back_off_Plex()
    {
        _plex.Title("kD", WatchlistType.Movie, "Dune", new ExternalIds("438631", null, null));
        await LinkedAndSynced();
        var added = _service.Add(Alice, Movie("Dune", tmdb: "438631")).Entry;
        _plex.AddGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var syncing = _sync.SyncUser(Alice, CancellationToken.None);
        await _plex.AddEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _service.Remove(Alice, added.Id); // no ratingKey yet, so no PushRemove from the service
        _plex.AddGate.SetResult();
        await syncing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(Stored.Entries);
        Assert.Equal<PlexOp>([new PushRemove("kD")], Stored.Outbox);

        await _sync.SyncUser(Alice, CancellationToken.None);
        Assert.Empty(_plex.Watchlist);
        Assert.Empty(Stored.Entries);
    }
}
