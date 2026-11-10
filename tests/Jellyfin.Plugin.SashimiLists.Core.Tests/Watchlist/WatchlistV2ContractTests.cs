using System.Text.Json;
using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

/// <summary>The Search, Details and Config contracts the client apps code against. Names must not drift.</summary>
public class WatchlistV2ContractTests
{
    private static readonly Guid EntryId = Guid.Parse("12345678-1234-1234-1234-123456789abc");

    private static JsonElement Body(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value, WatchlistContract.JsonOptions)).RootElement;

    private static IEnumerable<string> Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name);

    [Fact]
    public void Search_serializes_with_contract_field_names()
    {
        var results = new SearchResults(
            [new SearchHit(WatchlistType.Series, "Severance", 2022, new ExternalIds("95396", "371980", "tt11280740"), null, "Mark leads a team...", "abcdef", EntryId)],
            [new SearchHit(WatchlistType.Movie, "Dune", 2021, new ExternalIds("438631", null, null), "https://image.example/p.jpg", null, null, null)]);

        var json = Body(WatchlistContract.ToSearchDto("dune", results));

        Assert.Equal(["query", "onServer", "notOnServer"], Names(json));
        Assert.Equal("dune", json.GetProperty("query").GetString());
        var on = json.GetProperty("onServer")[0];
        string[] hitFields = ["type", "title", "year", "tmdbId", "tvdbId", "imdbId", "posterUrl", "overview", "itemId", "onWatchlist", "watchlistEntryId"];
        Assert.Equal(hitFields, Names(on));
        Assert.Equal("Series", on.GetProperty("type").GetString());
        Assert.Equal("abcdef", on.GetProperty("itemId").GetString());
        Assert.True(on.GetProperty("onWatchlist").GetBoolean());
        Assert.Equal(EntryId.ToString("N"), on.GetProperty("watchlistEntryId").GetString());
        var off = json.GetProperty("notOnServer")[0];
        Assert.Equal(hitFields, Names(off));
        Assert.Equal(JsonValueKind.Null, off.GetProperty("itemId").ValueKind);
        Assert.Equal(JsonValueKind.Null, off.GetProperty("watchlistEntryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, off.GetProperty("tvdbId").ValueKind);
        Assert.False(off.GetProperty("onWatchlist").GetBoolean());
        Assert.Equal(2021, off.GetProperty("year").GetInt32());
    }

    [Fact]
    public void A_search_hit_posts_straight_back_as_a_watchlist_add()
    {
        var hit = new SearchHit(WatchlistType.Movie, "Dune", 2021, new ExternalIds("438631", null, null), "https://image.example/p.jpg", null, null, null);
        var json = Body(WatchlistContract.ToSearchDto("dune", new SearchResults([], [hit])));

        Assert.True(WatchlistContract.TryReadAdd(json.GetProperty("notOnServer")[0], out var entry));
        Assert.Equal(new NewEntry(WatchlistType.Movie, "Dune", 2021, "https://image.example/p.jpg", new ExternalIds("438631", null, null)), entry);
    }

    [Fact]
    public void Details_serialize_every_field_when_known()
    {
        var details = new TitleDetails(
            WatchlistType.Movie, "Dune", 2021, "Overview", "https://image.example/p.jpg", "https://image.example/b.jpg",
            155, null, "PG-13", ["Science Fiction"], 7.8, "TMDB", new ExternalIds("438631", null, "tt1160419"), "abcdef", EntryId);

        var json = Body(WatchlistContract.ToDetailsDto(details));

        Assert.Equal(
            ["type", "title", "year", "overview", "posterUrl", "backdropUrl", "runtimeMinutes", "officialRating", "genres", "communityRating", "source", "tmdbId", "imdbId", "itemId", "onWatchlist", "watchlistEntryId"],
            Names(json));
        Assert.Equal(155, json.GetProperty("runtimeMinutes").GetInt32());
        Assert.Equal(7.8, json.GetProperty("communityRating").GetDouble());
        Assert.Equal("Science Fiction", json.GetProperty("genres")[0].GetString());
        Assert.True(json.GetProperty("onWatchlist").GetBoolean());
    }

    [Fact]
    public void Details_omit_unknown_fields()
    {
        var details = new TitleDetails(
            WatchlistType.Series, "Show", null, null, null, null, null, 3, null, [], null, null, new ExternalIds(null, "1", null), null, null);

        var json = Body(WatchlistContract.ToDetailsDto(details));

        Assert.Equal(["type", "title", "seasonCount", "tvdbId", "onWatchlist"], Names(json));
        Assert.Equal("Series", json.GetProperty("type").GetString());
        Assert.False(json.GetProperty("onWatchlist").GetBoolean());
    }

    [Fact]
    public void Config_serializes_requestsUrl_and_null_when_unset()
    {
        Assert.Equal("https://requests.example.com", Body(WatchlistContract.ToConfigDto(" https://requests.example.com/ ")).GetProperty("requestsUrl").GetString());
        var unset = Body(WatchlistContract.ToConfigDto(string.Empty));
        Assert.Equal(["requestsUrl"], Names(unset));
        Assert.Equal(JsonValueKind.Null, unset.GetProperty("requestsUrl").ValueKind);
    }

    [Theory]
    [InlineData("https://requests.example.com", "https://requests.example.com")]
    [InlineData("  http://10.0.0.5:5055/ ", "http://10.0.0.5:5055")]
    [InlineData("https://example.com/seerr/", "https://example.com/seerr")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("requests.example.com", null)]
    [InlineData("ftp://requests.example.com", null)]
    [InlineData("/relative", null)]
    public void Requests_address_is_an_absolute_http_url_or_nothing(string? configured, string? expected)
        => Assert.Equal(expected, RequestsAddress.Normalize(configured));
}
