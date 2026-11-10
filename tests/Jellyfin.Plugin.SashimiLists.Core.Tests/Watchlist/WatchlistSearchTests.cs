using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

/// <summary>How library and remote search hits become the two result groups.</summary>
public class WatchlistSearchTests
{
    private static LibraryTitle Lib(string itemId, string name, string? tmdb = null, string? imdb = null, WatchlistType type = WatchlistType.Movie)
        => new(itemId, type, name, 2021, new ExternalIds(tmdb, null, imdb), "library overview");

    private static RemoteTitle Remote(string name, string? tmdb = null, string? imdb = null, WatchlistType type = WatchlistType.Movie, string? poster = null)
        => new(type, name, 2021, new ExternalIds(tmdb, null, imdb), poster ?? "https://image.example/" + name + ".jpg", "remote overview");

    private static WatchlistEntry Entry(string tmdb, WatchlistType type = WatchlistType.Movie)
        => new() { Id = Guid.NewGuid(), Type = type, Title = "x", Ids = new ExternalIds(tmdb, null, null) };

    [Fact]
    public void Library_hits_are_on_server_and_remote_hits_are_not()
    {
        var results = WatchlistSearch.Combine(
            [Lib("item1", "Dune", tmdb: "438631")],
            [Remote("Dune: Part Two", tmdb: "693134")],
            [null],
            []);

        var onServer = Assert.Single(results.OnServer);
        Assert.Equal("item1", onServer.ItemId);
        Assert.Equal("Dune", onServer.Name);
        var notOnServer = Assert.Single(results.NotOnServer);
        Assert.Null(notOnServer.ItemId);
        Assert.Equal("693134", notOnServer.Ids.Tmdb);
        Assert.Equal("https://image.example/Dune: Part Two.jpg", notOnServer.PosterUrl);
    }

    [Fact]
    public void A_remote_hit_agreeing_with_a_library_hit_is_folded_into_it()
    {
        var results = WatchlistSearch.Combine(
            [Lib("item1", "Dune", tmdb: "438631")],
            [Remote("Dune", tmdb: "438631", imdb: "tt1160419", poster: "https://image.example/dune.jpg")],
            [null],
            []);

        var hit = Assert.Single(results.OnServer);
        Assert.Empty(results.NotOnServer);
        Assert.Equal("item1", hit.ItemId);
        Assert.Equal("tt1160419", hit.Ids.Imdb);
        Assert.Equal("https://image.example/dune.jpg", hit.PosterUrl);
        Assert.Equal("library overview", hit.Overview);
    }

    [Fact]
    public void A_remote_hit_the_library_has_but_the_title_search_missed_moves_to_on_server()
    {
        // e.g. the library item is titled differently ("Dune: Part One").
        var results = WatchlistSearch.Combine([], [Remote("Dune", tmdb: "438631"), Remote("Dune", tmdb: "841")], ["item9", null], []);

        var hit = Assert.Single(results.OnServer);
        Assert.Equal("item9", hit.ItemId);
        Assert.Equal("438631", hit.Ids.Tmdb);
        Assert.Equal("841", Assert.Single(results.NotOnServer).Ids.Tmdb);
    }

    [Fact]
    public void Two_remote_hits_for_the_same_library_item_show_once()
    {
        var results = WatchlistSearch.Combine(
            [Lib("item1", "Dune", tmdb: "438631")],
            [Remote("Dune", imdb: "tt1160419"), Remote("Dune", tmdb: "438631")],
            ["item1", "item1"],
            []);

        Assert.Single(results.OnServer);
        Assert.Empty(results.NotOnServer);
    }

    [Fact]
    public void Duplicate_remote_hits_show_once()
    {
        var results = WatchlistSearch.Combine([], [Remote("Dune", tmdb: "438631"), Remote("Dune", tmdb: "438631", imdb: "tt1160419")], [null, null], []);

        var hit = Assert.Single(results.NotOnServer);
        Assert.Equal("tt1160419", hit.Ids.Imdb);
    }

    [Fact]
    public void A_remote_hit_without_any_id_is_left_out()
    {
        var results = WatchlistSearch.Combine([], [Remote("Mystery")], [null], []);

        Assert.Empty(results.NotOnServer);
    }

    [Fact]
    public void A_movie_and_a_series_sharing_a_tmdb_number_both_show()
    {
        var results = WatchlistSearch.Combine(
            [],
            [Remote("A movie", tmdb: "1399"), Remote("A show", tmdb: "1399", type: WatchlistType.Series)],
            [null, null],
            []);

        Assert.Equal(2, results.NotOnServer.Count);
    }

    [Fact]
    public void Results_say_which_titles_are_already_on_the_watchlist()
    {
        var listed = Entry("438631");
        var listedShow = Entry("1399", WatchlistType.Series);

        var results = WatchlistSearch.Combine(
            [Lib("item1", "Dune", tmdb: "438631")],
            [Remote("Game of Thrones", tmdb: "1399", type: WatchlistType.Series), Remote("A movie", tmdb: "1399")],
            [null, null],
            [listed, listedShow]);

        Assert.Equal(listed.Id, results.OnServer[0].EntryId);
        Assert.Equal(listedShow.Id, results.NotOnServer[0].EntryId);
        Assert.Null(results.NotOnServer[1].EntryId);
    }

    [Fact]
    public void Each_group_is_capped()
    {
        var library = Enumerable.Range(0, 30).Select(i => Lib("item" + i, "L" + i, tmdb: "l" + i)).ToList();
        var remote = Enumerable.Range(0, 30).Select(i => Remote("R" + i, tmdb: "r" + i)).ToList();

        var results = WatchlistSearch.Combine(library, remote, remote.Select(_ => (string?)null).ToList(), [], limit: 20);

        Assert.Equal(20, results.OnServer.Count);
        Assert.Equal(20, results.NotOnServer.Count);
        Assert.Equal("L0", results.OnServer[0].Name);
        Assert.Equal("R0", results.NotOnServer[0].Name);
    }

    [Fact]
    public void Library_hits_with_the_same_item_id_show_once()
    {
        var results = WatchlistSearch.Combine([Lib("item1", "Dune", tmdb: "1"), Lib("item1", "Dune", tmdb: "1")], [], [], []);

        Assert.Single(results.OnServer);
    }

    [Fact]
    public void Remote_matches_must_line_up_with_remote_hits()
        => Assert.Throws<ArgumentException>(() => WatchlistSearch.Combine([], [Remote("Dune", tmdb: "1")], [], []));

    [Fact]
    public void Remote_movies_and_series_are_interleaved_so_neither_crowds_out_the_other()
    {
        Assert.Equal(["m1", "s1", "m2", "s2", "m3", "m4"], WatchlistSearch.Interleave(["m1", "m2", "m3", "m4"], ["s1", "s2"]));
        Assert.Equal(["s1", "s2"], WatchlistSearch.Interleave([], ["s1", "s2"]));
    }

    [Theory]
    [InlineData("dune", "dune")]
    [InlineData("  the bear ", "the bear")]
    public void Query_is_trimmed(string raw, string expected)
    {
        Assert.True(WatchlistSearch.TryReadQuery(raw, out var query));
        Assert.Equal(expected, query);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_query_is_rejected(string? raw) => Assert.False(WatchlistSearch.TryReadQuery(raw, out _));

    [Fact]
    public void An_overlong_query_is_rejected()
    {
        Assert.True(WatchlistSearch.TryReadQuery(new string('a', WatchlistSearch.MaxQueryLength), out _));
        Assert.False(WatchlistSearch.TryReadQuery(new string('a', WatchlistSearch.MaxQueryLength + 1), out _));
    }
}
