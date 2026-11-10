using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

/// <summary>The one-time merge of a user's old "Watchlist" playlist into their Watchlist.</summary>
public sealed class PlaylistMergeTests : IDisposable
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private readonly TempStore _temp = new();
    private readonly FakeClock _clock = new();
    private readonly FakePlaylists _playlists = new();
    private readonly PlaylistMerger _merger;

    public PlaylistMergeTests()
    {
        _merger = new PlaylistMerger(_temp.Store, _clock, _playlists);
    }

    public void Dispose() => _temp.Dispose();

    private static PlaylistItem MovieItem(string id, string title, string? tmdb = null, string? imdb = null)
        => new(id, PlaylistItemKind.Movie, new TitleInfo(title, 2020, new ExternalIds(tmdb, null, imdb)), null);

    private static PlaylistItem SeriesItem(string id, string title, string? tvdb)
        => new(id, PlaylistItemKind.Series, new TitleInfo(title, 2011, new ExternalIds(null, tvdb, null)), null);

    private static PlaylistItem EpisodeOf(string id, string series, string? seriesTvdb, PlaylistItemKind kind = PlaylistItemKind.Episode)
        => new(id, kind, new TitleInfo("Pilot", 2011, new ExternalIds(null, "ep" + id, null)), new TitleInfo(series, 2011, new ExternalIds(null, seriesTvdb, null)));

    [Theory]
    [InlineData("Watchlist", true)]
    [InlineData("  watchlist ", true)]
    [InlineData("WATCHLIST", true)]
    [InlineData("Watchlist 2", false)]
    [InlineData("My Watchlist", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_playlist_named_watchlist_counts(string? name, bool expected)
        => Assert.Equal(expected, PlaylistMerger.IsWatchlistName(name));

    [Fact]
    public void Merge_asks_for_watchlist_named_playlists_only()
    {
        _playlists.Add(Alice, "Watchlist", MovieItem("1", "Dune", tmdb: "438631"));
        _playlists.Add(Alice, "Road trip", MovieItem("2", "Cars", tmdb: "920"));

        _merger.MergeOnce(Alice);

        Assert.Equal(["Dune"], _temp.Store.Read(Alice).Entries.Select(e => e.Title));
    }

    [Fact]
    public void Movies_and_series_go_in_and_episodes_and_seasons_bring_their_series()
    {
        _playlists.Add(
            Alice,
            "watchlist",
            MovieItem("1", "Dune", tmdb: "438631"),
            SeriesItem("2", "Severance", tvdb: "371980"),
            EpisodeOf("3", "The Bear", seriesTvdb: "403245"),
            EpisodeOf("4", "Slow Horses", seriesTvdb: "393714", PlaylistItemKind.Season));

        var result = _merger.MergeOnce(Alice);

        var entries = _temp.Store.Read(Alice).Entries;
        Assert.True(result.Ran);
        Assert.Equal(4, result.Added);
        Assert.Equal(["Dune", "Severance", "The Bear", "Slow Horses"], entries.Select(e => e.Title));
        Assert.Equal([WatchlistType.Movie, WatchlistType.Series, WatchlistType.Series, WatchlistType.Series], entries.Select(e => e.Type));
        Assert.Equal("403245", entries[2].Ids.Tvdb);
        Assert.Equal("393714", entries[3].Ids.Tvdb);
    }

    [Fact]
    public void Items_without_any_external_id_are_skipped_and_reported()
    {
        _playlists.Add(
            Alice,
            "Watchlist",
            MovieItem("home", "Home video"),
            EpisodeOf("yt", "Some YouTube channel", seriesTvdb: null),
            new PlaylistItem("song", PlaylistItemKind.Other, new TitleInfo("A song", null, new ExternalIds("1", null, null)), null),
            MovieItem("ok", "Dune", tmdb: "438631"));

        var result = _merger.MergeOnce(Alice);

        Assert.Equal(["home", "yt", "song"], result.SkippedItemIds);
        Assert.Equal(["Dune"], _temp.Store.Read(Alice).Entries.Select(e => e.Title));
    }

    [Fact]
    public void Playlist_order_is_kept_in_the_newest_first_view()
    {
        _playlists.Add(Alice, "Watchlist", MovieItem("1", "First", tmdb: "1"), MovieItem("2", "Second", tmdb: "2"), MovieItem("3", "Third", tmdb: "3"));

        _merger.MergeOnce(Alice);

        var service = new WatchlistService(_temp.Store, new FakeLibrary(), _clock, new FakePlexApi());
        Assert.Equal(["First", "Second", "Third"], service.View(Alice).Entries.Select(e => e.Title));
        // Whole seconds apart, so the order survives the contract's second-precision addedAt.
        var added = _temp.Store.Read(Alice).Entries.Select(e => e.AddedAt).ToList();
        Assert.Equal(TimeSpan.FromSeconds(1), added[0] - added[1]);
        Assert.True(added.All(a => a <= _clock.Now));
    }

    [Fact]
    public void Titles_already_listed_or_repeated_are_not_added_twice()
    {
        var service = new WatchlistService(_temp.Store, new FakeLibrary(), _clock, new FakePlexApi());
        service.Add(Alice, new NewEntry(WatchlistType.Movie, "Dune", 2021, null, new ExternalIds("438631", null, null)));
        _playlists.Add(
            Alice,
            "Watchlist",
            MovieItem("1", "Dune (2021)", tmdb: null, imdb: "tt1160419") with { Own = new TitleInfo("Dune (2021)", 2021, new ExternalIds("438631", null, "tt1160419")) },
            EpisodeOf("2", "The Bear", seriesTvdb: "403245"),
            EpisodeOf("3", "The Bear", seriesTvdb: "403245"),
            SeriesItem("4", "The Bear", tvdb: "403245"));

        var result = _merger.MergeOnce(Alice);

        Assert.Equal(1, result.Added);
        Assert.Equal(["Dune", "The Bear"], _temp.Store.Read(Alice).Entries.Select(e => e.Title).Order());
    }

    [Fact]
    public void A_movie_and_a_series_with_the_same_tmdb_number_are_different_titles()
    {
        _playlists.Add(
            Alice,
            "Watchlist",
            MovieItem("1", "A movie", tmdb: "1399"),
            new PlaylistItem("2", PlaylistItemKind.Series, new TitleInfo("A show", 2011, new ExternalIds("1399", null, null)), null));

        Assert.Equal(2, _merger.MergeOnce(Alice).Added);
    }

    [Fact]
    public void Merged_playlists_are_deleted()
    {
        var a = _playlists.Add(Alice, "Watchlist", MovieItem("1", "Dune", tmdb: "438631"));
        var b = _playlists.Add(Alice, "watchlist ", MovieItem("2", "Cars", tmdb: "920"));

        var result = _merger.MergeOnce(Alice);

        Assert.Equal([a, b], _playlists.Deleted);
        Assert.Empty(result.DeleteFailures);
        Assert.Equal(2, _temp.Store.Read(Alice).Entries.Count);
    }

    [Fact]
    public void An_empty_watchlist_playlist_is_deleted_too()
    {
        var a = _playlists.Add(Alice, "Watchlist");

        var result = _merger.MergeOnce(Alice);

        Assert.True(result.Ran);
        Assert.Equal([a], _playlists.Deleted);
    }

    [Fact]
    public void Merge_runs_once_even_if_a_new_watchlist_playlist_appears_later()
    {
        _playlists.Add(Alice, "Watchlist", MovieItem("1", "Dune", tmdb: "438631"));
        Assert.True(_merger.MergeOnce(Alice).Ran);

        _playlists.Add(Alice, "Watchlist", MovieItem("2", "Cars", tmdb: "920"));
        var again = _merger.MergeOnce(Alice);

        Assert.False(again.Ran);
        Assert.Equal(["Dune"], _temp.Store.Read(Alice).Entries.Select(e => e.Title));
        Assert.Single(_playlists.Deleted);
        Assert.Equal(1, _playlists.FindCalls);
    }

    [Fact]
    public void A_user_with_no_watchlist_playlist_is_marked_done()
    {
        _merger.MergeOnce(Alice);
        _playlists.Add(Alice, "Watchlist", MovieItem("1", "Dune", tmdb: "438631"));

        Assert.False(_merger.MergeOnce(Alice).Ran);
        Assert.Empty(_temp.Store.Read(Alice).Entries);
    }

    [Fact]
    public void A_failed_delete_still_counts_as_merged()
    {
        _playlists.Add(Alice, "Watchlist", MovieItem("1", "Dune", tmdb: "438631"));
        _playlists.DeleteFailure = new IOException("disk full");

        var result = _merger.MergeOnce(Alice);

        Assert.True(result.Ran);
        var failure = Assert.Single(result.DeleteFailures);
        Assert.IsType<IOException>(failure.Error);
        Assert.True(_temp.Store.Read(Alice).PlaylistMerged);
        Assert.False(_merger.MergeOnce(Alice).Ran);
    }

    [Fact]
    public void A_failure_before_the_merge_finishes_leaves_it_to_retry()
    {
        _playlists.Add(Alice, "Watchlist", MovieItem("1", "Dune", tmdb: "438631"));
        _playlists.FindFailure = new InvalidOperationException("library busy");

        Assert.Throws<InvalidOperationException>(() => _merger.MergeOnce(Alice));
        Assert.False(_temp.Store.Read(Alice).PlaylistMerged);
        Assert.Empty(_temp.Store.Read(Alice).Entries);
        Assert.Empty(_playlists.Deleted);

        _playlists.FindFailure = null;
        Assert.True(_merger.MergeOnce(Alice).Ran);
        Assert.Single(_temp.Store.Read(Alice).Entries);
    }

    [Fact]
    public void For_a_linked_user_new_titles_are_queued_for_Plex()
    {
        _temp.Store.Update(Alice, w =>
        {
            w.Plex = new PlexLinkState { Token = "t", AccountUuid = "u", Username = "alice", FirstSyncDone = true };
            return 0;
        });
        _playlists.Add(Alice, "Watchlist", MovieItem("1", "Dune", tmdb: "438631"), SeriesItem("2", "Severance", tvdb: "371980"));

        _merger.MergeOnce(Alice);

        var stored = _temp.Store.Read(Alice);
        Assert.Equal(stored.Entries.Select(e => (PlexOp)new PushAdd(e.Id)), stored.Outbox);
    }

    [Fact]
    public void For_an_unlinked_user_nothing_is_queued()
    {
        _playlists.Add(Alice, "Watchlist", MovieItem("1", "Dune", tmdb: "438631"));

        _merger.MergeOnce(Alice);

        Assert.Empty(_temp.Store.Read(Alice).Outbox);
    }

    [Fact]
    public void Merge_flag_survives_a_round_trip_through_the_file()
    {
        _merger.MergeOnce(Alice);

        Assert.True(new FileWatchlistStore(_temp.Directory).Read(Alice).PlaylistMerged);
    }

    [Fact]
    public void Concurrent_first_GETs_merge_once()
    {
        _playlists.Add(Alice, "Watchlist", MovieItem("1", "Dune", tmdb: "438631"), MovieItem("2", "Cars", tmdb: "920"));

        var results = Enumerable.Range(0, 8).AsParallel().Select(_ => _merger.MergeOnce(Alice)).ToList();

        Assert.Equal(2, _temp.Store.Read(Alice).Entries.Count);
        Assert.Equal(2, results.Sum(r => r.Added));
    }
}

/// <summary>In-memory playlists owned by users, recording deletes.</summary>
internal sealed class FakePlaylists : IOldWatchlistPlaylists
{
    private readonly List<(Guid Owner, string Name, OldPlaylist Playlist)> _playlists = [];

    public List<Guid> Deleted { get; } = [];

    public Exception? DeleteFailure { get; set; }

    public Exception? FindFailure { get; set; }

    public int FindCalls;

    public Guid Add(Guid owner, string name, params PlaylistItem[] items)
    {
        var id = Guid.NewGuid();
        lock (_playlists)
        {
            _playlists.Add((owner, name, new OldPlaylist(id, items)));
        }

        return id;
    }

    public IReadOnlyList<OldPlaylist> FindOwned(Guid userId, Func<string, bool> isWatchlistName)
    {
        Interlocked.Increment(ref FindCalls);
        if (FindFailure is not null)
        {
            throw FindFailure;
        }

        lock (_playlists)
        {
            return _playlists.Where(p => p.Owner == userId && isWatchlistName(p.Name) && !Deleted.Contains(p.Playlist.Id)).Select(p => p.Playlist).ToList();
        }
    }

    public void Delete(Guid playlistId)
    {
        if (DeleteFailure is not null)
        {
            throw DeleteFailure;
        }

        lock (_playlists)
        {
            if (!Deleted.Contains(playlistId))
            {
                Deleted.Add(playlistId);
            }
        }
    }
}
