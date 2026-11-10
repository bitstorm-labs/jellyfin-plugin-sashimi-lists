using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

/// <summary>The details page for a title that isn't on the server.</summary>
public class TitleDetailsTests
{
    private static TitleDetailsSource Full() => new(
        Name: " Dune ",
        Year: 2021,
        Overview: "Paul Atreides...",
        PosterUrl: "https://image.example/poster.jpg",
        BackdropUrl: "https://image.example/backdrop.jpg",
        RuntimeMinutes: 155,
        SeasonCount: null,
        OfficialRating: "PG-13",
        Genres: ["Science Fiction", " Adventure ", "Science Fiction", ""],
        CommunityRating: 7.789f,
        ProviderName: "TheMovieDb",
        Ids: new ExternalIds("438631", null, "tt1160419"));

    [Fact]
    public void Build_cleans_up_what_the_provider_gave()
    {
        var details = TitleDetailsRules.Build(WatchlistType.Movie, Full(), itemId: null, watchlist: [])!;

        Assert.Equal("Dune", details.Name);
        Assert.Equal(2021, details.Year);
        Assert.Equal(155, details.RuntimeMinutes);
        Assert.Null(details.SeasonCount);
        Assert.Equal(["Science Fiction", "Adventure"], details.Genres);
        Assert.Equal(7.8, details.CommunityRating);
        Assert.Equal("TMDB", details.Source);
        Assert.Equal("tt1160419", details.Ids.Imdb);
        Assert.Null(details.EntryId);
        Assert.Null(details.ItemId);
    }

    [Fact]
    public void Unknown_and_meaningless_values_are_dropped()
    {
        var source = new TitleDetailsSource("Show", 0, " ", "/relative.jpg", "ftp://x/y.jpg", 0, 0, "", null, 0f, null, new ExternalIds("1", null, null));

        var details = TitleDetailsRules.Build(WatchlistType.Series, source, null, [])!;

        Assert.Null(details.Year);
        Assert.Null(details.Overview);
        Assert.Null(details.PosterUrl);
        Assert.Null(details.BackdropUrl);
        Assert.Null(details.SeasonCount);
        Assert.Null(details.OfficialRating);
        Assert.Empty(details.Genres);
        Assert.Null(details.CommunityRating);
        Assert.Null(details.Source);
    }

    [Fact]
    public void Movies_have_no_season_count_and_series_no_runtime()
    {
        var both = Full() with { RuntimeMinutes = 50, SeasonCount = 3 };

        var movie = TitleDetailsRules.Build(WatchlistType.Movie, both, null, [])!;
        var series = TitleDetailsRules.Build(WatchlistType.Series, both, null, [])!;

        Assert.Equal((50, (int?)null), (movie.RuntimeMinutes, movie.SeasonCount));
        Assert.Equal(((int?)null, 3), (series.RuntimeMinutes, series.SeasonCount));
    }

    [Fact]
    public void Nothing_known_means_no_details()
    {
        Assert.Null(TitleDetailsRules.Build(WatchlistType.Movie, Full() with { Name = "  " }, null, []));
        Assert.Null(TitleDetailsRules.Build(WatchlistType.Movie, Full() with { Name = null }, null, []));
    }

    [Fact]
    public void Details_say_whether_the_title_is_on_the_watchlist_and_on_the_server()
    {
        var entry = new WatchlistEntry { Id = Guid.NewGuid(), Type = WatchlistType.Movie, Title = "Dune", Ids = new ExternalIds(null, null, "TT1160419") };

        var details = TitleDetailsRules.Build(WatchlistType.Movie, Full(), "item1", [entry])!;

        Assert.Equal(entry.Id, details.EntryId);
        Assert.Equal("item1", details.ItemId);
    }

    [Fact]
    public void Requested_ids_fill_ids_the_provider_left_out()
    {
        var details = TitleDetailsRules.Build(
            WatchlistType.Series,
            Full() with { Ids = new ExternalIds("1399", null, null) },
            null,
            [],
            requested: new ExternalIds(null, "121361", null))!;

        Assert.Equal(new ExternalIds("1399", "121361", null), details.Ids);
    }

    [Theory]
    [InlineData("TheMovieDb", "TMDB")]
    [InlineData("themoviedb", "TMDB")]
    [InlineData("The Open Movie Database", "OMDb")]
    [InlineData("TheTVDB", "TheTVDB")]
    [InlineData(null, null)]
    [InlineData(" ", null)]
    public void Provider_names_read_like_people_say_them(string? provider, string? label)
        => Assert.Equal(label, TitleDetailsRules.SourceLabel(provider));

    [Theory]
    [InlineData("movie", "1", null, null, WatchlistType.Movie)]
    [InlineData("Movie", null, null, "tt1", WatchlistType.Movie)]
    [InlineData("series", null, "2", null, WatchlistType.Series)]
    [InlineData("SERIES", "3", "4", "tt5", WatchlistType.Series)]
    public void Query_accepts_a_type_and_any_id(string type, string? tmdb, string? tvdb, string? imdb, WatchlistType expected)
    {
        Assert.True(TitleDetailsRules.TryReadQuery(type, tmdb, tvdb, imdb, out var parsed, out var ids));
        Assert.Equal(expected, parsed);
        Assert.Equal(new ExternalIds(tmdb, tvdb, imdb), ids);
    }

    [Theory]
    [InlineData("episode", "1", null, null)]
    [InlineData(null, "1", null, null)]
    [InlineData("movie", null, null, null)]
    [InlineData("movie", " ", "", null)]
    [InlineData("movie", "abc", null, null)]
    [InlineData("series", null, "x1", null)]
    public void Query_rejects_other_types_and_missing_or_malformed_ids(string? type, string? tmdb, string? tvdb, string? imdb)
        => Assert.False(TitleDetailsRules.TryReadQuery(type, tmdb, tvdb, imdb, out _, out _));
}
