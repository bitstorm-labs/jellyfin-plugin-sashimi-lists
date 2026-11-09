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
    public async Task Imported_item_with_relative_thumb_has_no_poster()
    {
        await LinkedAndSynced();
        _plex.Title("kR", WatchlistType.Movie, "Relative", new ExternalIds("77", null, null), onWatchlist: true);
        _plex.Catalog["kR"] = (_plex.Catalog["kR"].Item with { Thumb = "/library/metadata/kR/thumb/1" }, _plex.Catalog["kR"].Ids);

        await _sync.SyncUser(Alice, CancellationToken.None);

        Assert.Null(Assert.Single(Stored.Entries).PosterUrl);
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

    [Fact]
    public async Task Two_syncs_of_one_user_run_one_at_a_time()
    {
        _plex.Title("kD", WatchlistType.Movie, "Dune", new ExternalIds("438631", null, null));
        await LinkedAndSynced();
        _service.Add(Alice, Movie("Dune", tmdb: "438631"));
        _plex.WatchlistCalls = 0;
        _plex.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = _sync.SyncUser(Alice, CancellationToken.None);
        await _plex.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = _sync.SyncUser(Alice, CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(1, _plex.WatchlistCalls); // the second waits for the first

        _plex.Gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, _plex.WatchlistCalls);
        Assert.Equal(["kD"], _plex.Added); // pushed once, not by both
    }

    [Fact]
    public async Task Title_removed_and_readded_during_a_sync_survives_on_both_sides()
    {
        // Review finding: the merge queued PushRemove(k) after the re-add's PushAdd,
        // and the next sync read its own removal as a removal on Plex.
        _plex.Title("kD", WatchlistType.Movie, "Dune", new ExternalIds("438631", null, null));
        var first = _service.Add(Alice, Movie("Dune", tmdb: "438631")).Entry;
        Link(firstSyncDone: false);
        _plex.AddGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var syncing = _sync.SyncUser(Alice, CancellationToken.None);
        await _plex.AddEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _service.Remove(Alice, first.Id);
        var again = _service.Add(Alice, Movie("Dune", tmdb: "438631")).Entry;
        _plex.AddGate.SetResult();
        await syncing.WaitAsync(TimeSpan.FromSeconds(5));
        _plex.AddGate = null;

        await _sync.SyncUser(Alice, CancellationToken.None);
        await _sync.SyncUser(Alice, CancellationToken.None);

        var entry = Assert.Single(Stored.Entries);
        Assert.Equal(again.Id, entry.Id);
        Assert.Equal("kD", entry.PlexRatingKey);
        Assert.Equal(["kD"], _plex.Watchlist);
        Assert.Empty(Stored.Outbox);
    }

    [Fact]
    public async Task Removing_one_of_two_entries_on_the_same_Plex_title_keeps_the_other()
    {
        // No shared id, so they are two entries, but Plex resolves both to one title.
        _plex.Title("kD", WatchlistType.Movie, "Dune", new ExternalIds("438631", null, "tt1160419"));
        await LinkedAndSynced();
        var byTmdb = _service.Add(Alice, Movie("Dune", tmdb: "438631")).Entry;
        var byImdb = _service.Add(Alice, new NewEntry(WatchlistType.Movie, "Dune", 2021, null, new ExternalIds(null, null, "tt1160419"))).Entry;
        await _sync.SyncUser(Alice, CancellationToken.None);
        Assert.All(Stored.Entries, e => Assert.Equal("kD", e.PlexRatingKey));

        _service.Remove(Alice, byTmdb.Id);
        await _sync.SyncUser(Alice, CancellationToken.None);
        await _sync.SyncUser(Alice, CancellationToken.None);

        Assert.Equal(byImdb.Id, Assert.Single(Stored.Entries).Id);
        Assert.Equal(["kD"], _plex.Watchlist);
    }

    [Fact]
    public async Task First_sync_does_not_duplicate_a_title_whose_ids_only_Plex_can_match()
    {
        // The Plex item reports only a tvdb id; the local entry has only tmdb,
        // yet Plex's matcher resolves the local entry to the same ratingKey.
        _plex.Title("kS", WatchlistType.Series, "Show", new ExternalIds(null, "T1", null), onWatchlist: true);
        _plex.MatchAliases["kS"] = new ExternalIds("M1", null, null);
        var local = _service.Add(Alice, new NewEntry(WatchlistType.Series, "Show", 2020, null, new ExternalIds("M1", null, null))).Entry;
        Link(firstSyncDone: false);

        await _sync.SyncUser(Alice, CancellationToken.None);

        var entry = Assert.Single(Stored.Entries);
        Assert.Equal(local.Id, entry.Id);
        Assert.Equal("kS", entry.PlexRatingKey);
        Assert.Empty(_plex.Added);
    }

    [Fact]
    public async Task Unlink_and_relink_during_a_sync_still_gets_a_first_sync_merge()
    {
        await LinkedAndSynced();
        _plex.Title("kU", WatchlistType.Movie, "Unlinked add", new ExternalIds("31", null, null));
        _plex.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stale = _sync.SyncUser(Alice, CancellationToken.None);
        await _plex.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _service.UnlinkPlex(Alice);
        _service.Add(Alice, Movie("Unlinked add", tmdb: "31")); // no outbox: not linked
        Assert.IsType<Linked>(await _service.LinkPlex(Alice, FakePlexApi.Token, replace: false, CancellationToken.None));
        _plex.Gate.SetResult();
        await stale.WaitAsync(TimeSpan.FromSeconds(5));
        _plex.Gate = null;

        Assert.False(Stored.Plex!.FirstSyncDone); // the stale run must not mark the new link synced

        await _sync.SyncUser(Alice, CancellationToken.None);
        Assert.Contains("kU", _plex.Added);
    }

    [Fact]
    public async Task Plex_item_without_ids_is_retried_and_counted()
    {
        var unmatched = new List<int>();
        var sync = new PlexSync(_temp.Store, _plex, _clock, (_, count) => unmatched.Add(count));
        _plex.Title("kN", WatchlistType.Movie, "New on Plex", new ExternalIds(null, null, null), onWatchlist: true);
        Link(firstSyncDone: false);

        await sync.SyncUser(Alice, CancellationToken.None);
        Assert.Empty(Stored.Entries);
        Assert.DoesNotContain("kN", Stored.LastPlexSnapshot);
        Assert.Equal([1], unmatched);

        // Plex fills in the ids later; the next run picks the title up.
        _plex.Catalog["kN"] = (_plex.Catalog["kN"].Item, new ExternalIds("41", null, null));
        await sync.SyncUser(Alice, CancellationToken.None);

        Assert.Equal("kN", Assert.Single(Stored.Entries).PlexRatingKey);
        Assert.Contains("kN", Stored.LastPlexSnapshot);
        Assert.Equal([1], unmatched); // nothing unmatched this time, so no report
    }
}
