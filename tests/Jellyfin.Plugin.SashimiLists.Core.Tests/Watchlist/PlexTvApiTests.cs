using System.Net;
using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

public class PlexTvApiTests
{
    private const string Token = "secret-token-123";

    /// <summary>Answers by URL substring; records every request.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers)> Requests { get; } = [];

        public List<(string UrlContains, HttpStatusCode Status, string Body)> Routes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));
            var url = request.RequestUri!.ToString();
            var route = Routes.FirstOrDefault(r => url.Contains(r.UrlContains, StringComparison.Ordinal));
            return Task.FromResult(new HttpResponseMessage(route.UrlContains is null ? HttpStatusCode.NotFound : route.Status)
            {
                Content = new StringContent(route.Body ?? string.Empty),
            });
        }
    }

    private static (PlexTvApi Api, FakeHandler Handler) Make()
    {
        var handler = new FakeHandler();
        return (new PlexTvApi(() => new HttpClient(handler, disposeHandler: false), "sashimi-lists-server1"), handler);
    }

    [Fact]
    public async Task Token_goes_only_in_the_header_with_the_plex_headers()
    {
        var (api, handler) = Make();
        handler.Routes.Add(("plex.tv/api/v2/user", HttpStatusCode.OK, """{"uuid":"u1","username":"alice"}"""));

        var account = await api.GetAccount(Token, CancellationToken.None);

        Assert.Equal(new PlexAccount("u1", "alice"), account);
        var (method, uri, headers) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("https://plex.tv/api/v2/user", uri.ToString());
        Assert.DoesNotContain(Token, uri.ToString(), StringComparison.Ordinal);
        Assert.Equal(Token, headers["X-Plex-Token"]);
        Assert.Equal("application/json", headers["Accept"]);
        Assert.Equal("sashimi-lists-server1", headers["X-Plex-Client-Identifier"]);
        Assert.Equal("Sashimi", headers["X-Plex-Product"]);
    }

    [Fact]
    public async Task Http_401_is_PlexUnauthorized()
    {
        var (api, handler) = Make();
        handler.Routes.Add(("plex.tv", HttpStatusCode.Unauthorized, ""));

        await Assert.ThrowsAsync<PlexUnauthorizedException>(() => api.GetAccount(Token, CancellationToken.None));
    }

    [Fact]
    public async Task Server_error_names_the_status_but_not_the_url_or_token()
    {
        var (api, handler) = Make();
        handler.Routes.Add(("watchlist", HttpStatusCode.ServiceUnavailable, "upstream says " + Token));

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetWatchlist(Token, CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, e.StatusCode);
        Assert.Contains("503", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, e.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("://", e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindRatingKey_treats_404_as_no_match_and_tries_the_next_id()
    {
        var (api, handler) = Make();
        handler.Routes.Add(("guid=tmdb", HttpStatusCode.NotFound, ""));
        handler.Routes.Add(("guid=imdb", HttpStatusCode.OK, """{"MediaContainer":{"Metadata":[{"ratingKey":"k1"}]}}"""));

        var key = await api.FindRatingKey(Token, WatchlistType.Series, new ExternalIds("1", null, "tt2"), CancellationToken.None);

        Assert.Equal("k1", key);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("type=2", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("guid=tmdb%3A%2F%2F1", handler.Requests[0].Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindRatingKey_with_every_id_unknown_is_null()
    {
        var (api, handler) = Make();
        handler.Routes.Add(("matches", HttpStatusCode.NotFound, ""));

        Assert.Null(await api.FindRatingKey(Token, WatchlistType.Movie, new ExternalIds("1", "2", "tt3"), CancellationToken.None));
    }

    [Fact]
    public async Task GetIds_404_is_no_ids_and_cached()
    {
        var (api, handler) = Make();
        handler.Routes.Add(("library/metadata/gone", HttpStatusCode.NotFound, ""));
        handler.Routes.Add(("library/metadata/k2", HttpStatusCode.OK, """{"MediaContainer":{"Metadata":[{"Guid":[{"id":"tmdb://9"}]}]}}"""));

        Assert.False((await api.GetIds(Token, "gone", CancellationToken.None)).Any);
        Assert.Equal(new ExternalIds("9", null, null), await api.GetIds(Token, "k2", CancellationToken.None));
        Assert.Equal(new ExternalIds("9", null, null), await api.GetIds(Token, "k2", CancellationToken.None));
        Assert.Equal(1, handler.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/k2", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GetWatchlist_reads_every_page()
    {
        var (api, handler) = Make();
        var firstPage = string.Join(",", Enumerable.Range(0, 100).Select(i => "{\"ratingKey\":\"k" + i + "\",\"type\":\"movie\",\"title\":\"t\"}"));
        handler.Routes.Add(("X-Plex-Container-Start=0&", HttpStatusCode.OK, "{\"MediaContainer\":{\"totalSize\":101,\"Metadata\":[" + firstPage + "]}}"));
        handler.Routes.Add(("X-Plex-Container-Start=100&", HttpStatusCode.OK, """{"MediaContainer":{"totalSize":101,"Metadata":[{"ratingKey":"last","type":"show","title":"t"}]}}"""));

        var items = await api.GetWatchlist(Token, CancellationToken.None);

        Assert.Equal(101, items.Count);
        Assert.Equal("last", items[^1].RatingKey);
        Assert.All(handler.Requests, r => Assert.StartsWith("https://discover.provider.plex.tv/library/sections/watchlist/all?", r.Uri.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Add_and_Remove_PUT_the_escaped_ratingKey()
    {
        var (api, handler) = Make();
        handler.Routes.Add(("actions/", HttpStatusCode.OK, ""));

        await api.Add(Token, "a/b", CancellationToken.None);
        await api.Remove(Token, "k1", CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("https://discover.provider.plex.tv/actions/addToWatchlist?ratingKey=a%2Fb", handler.Requests[0].Uri.ToString());
        Assert.Equal("https://discover.provider.plex.tv/actions/removeFromWatchlist?ratingKey=k1", handler.Requests[1].Uri.ToString());
    }
}
