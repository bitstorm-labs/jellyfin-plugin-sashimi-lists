using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

/// <summary>plex.tv response shapes, as recorded against a real account on 2026-11-09.</summary>
public class PlexJsonTests
{
    [Fact]
    public void Account_reads_uuid_and_username()
    {
        var account = PlexJson.ParseAccount("""{"id":12345,"uuid":"a1b2c3","username":"alice","title":"Alice","email":"a@example.com"}""");

        Assert.Equal(new PlexAccount("a1b2c3", "alice"), account);
    }

    [Fact]
    public void Account_without_username_falls_back_to_title()
    {
        Assert.Equal(new PlexAccount("u", "Alice"), PlexJson.ParseAccount("""{"uuid":"u","title":"Alice"}"""));
    }

    [Fact]
    public void Account_without_uuid_is_an_error()
    {
        Assert.Throws<FormatException>(() => PlexJson.ParseAccount("""{"username":"alice"}"""));
    }

    [Fact]
    public void Watchlist_page_keeps_movies_and_shows_and_counts_everything()
    {
        var page = PlexJson.ParseWatchlistPage("""
            {"MediaContainer":{"offset":0,"size":3,"totalSize":3,"Metadata":[
              {"ratingKey":"5d776b59ad5437001f79c6f8","type":"movie","title":"Dune","year":2021,"thumb":"https://metadata-static.plex.tv/a.jpg"},
              {"ratingKey":"5d9c086c46115600200aa2fe","type":"show","title":"Severance","thumb":"https://image.tmdb.org/t/p/w500/b.jpg"},
              {"ratingKey":"col1","type":"collection","title":"A collection"}
            ]}}
            """);

        Assert.Equal(3, page.TotalSize);
        Assert.Equal(3, page.RawCount);
        Assert.Equal(
            [
                new PlexItem("5d776b59ad5437001f79c6f8", WatchlistType.Movie, "Dune", 2021, "https://metadata-static.plex.tv/a.jpg"),
                new PlexItem("5d9c086c46115600200aa2fe", WatchlistType.Series, "Severance", null, "https://image.tmdb.org/t/p/w500/b.jpg"),
            ],
            page.Items);
    }

    [Fact]
    public void Empty_watchlist_page_has_no_metadata_key()
    {
        var page = PlexJson.ParseWatchlistPage("""{"MediaContainer":{"offset":0,"size":0,"totalSize":0}}""");

        Assert.Equal(0, page.TotalSize);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void Ids_come_from_the_Guid_array()
    {
        var ids = PlexJson.ParseIds("""
            {"MediaContainer":{"Metadata":[{"ratingKey":"k","Guid":[{"id":"imdb://tt1160419"},{"id":"tmdb://438631"},{"id":"tvdb://12345"}]}]}}
            """);

        Assert.Equal(new ExternalIds("438631", "12345", "tt1160419"), ids);
    }

    [Fact]
    public void Ids_missing_are_null()
    {
        Assert.Equal(new ExternalIds(null, null, null), PlexJson.ParseIds("""{"MediaContainer":{"Metadata":[{"ratingKey":"k"}]}}"""));
        Assert.Equal(new ExternalIds(null, null, null), PlexJson.ParseIds("""{"MediaContainer":{}}"""));
    }

    [Fact]
    public void Matches_returns_first_ratingKey_or_null()
    {
        Assert.Equal("abc", PlexJson.ParseFirstRatingKey("""{"MediaContainer":{"size":1,"Metadata":[{"ratingKey":"abc","type":"movie"}]}}"""));
        Assert.Null(PlexJson.ParseFirstRatingKey("""{"MediaContainer":{"size":0}}"""));
    }

    private static string Page(int total, params string[] keys)
        => "{\"MediaContainer\":{\"totalSize\":" + total + ",\"Metadata\":["
            + string.Join(",", keys.Select(k => "{\"ratingKey\":\"" + k + "\",\"type\":\"movie\",\"title\":\"" + k + "\"}"))
            + "]}}";

    [Fact]
    public async Task ReadAllPages_walks_every_page()
    {
        var starts = new List<int>();
        var all = await PlexJson.ReadAllPages(
            (start, _) =>
            {
                starts.Add(start);
                return Task.FromResult(start == 0 ? Page(3, "a", "b") : Page(3, "c"));
            },
            pageSize: 2,
            CancellationToken.None);

        Assert.Equal([0, 2], starts);
        Assert.Equal(["a", "b", "c"], all.Select(i => i.RatingKey));
    }

    [Fact]
    public async Task ReadAllPages_throws_rather_than_return_a_short_list()
    {
        // The list shrank between pages: one title slid onto a page already read.
        await Assert.ThrowsAsync<PlexListChangedException>(() => PlexJson.ReadAllPages(
            (start, _) => Task.FromResult(start == 0 ? Page(3, "a", "b") : Page(2)),
            pageSize: 2,
            CancellationToken.None));
    }

    [Fact]
    public async Task ReadAllPages_of_an_empty_list_is_empty()
    {
        Assert.Empty(await PlexJson.ReadAllPages((_, _) => Task.FromResult(Page(0)), 100, CancellationToken.None));
    }
}
