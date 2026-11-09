using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.SashimiLists.Watchlist;

/// <summary>
/// A user's own plex.tv watchlist. The token travels only in the
/// <c>X-Plex-Token</c> header, so no URL, log line or exception carries it.
/// </summary>
public sealed class PlexTvClient : IPlexWatchlistApi
{
    private const int PageSize = 100;
    private const string Discover = "https://discover.provider.plex.tv";
    private const string Metadata = "https://metadata.provider.plex.tv";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _clientIdentifier;

    // A ratingKey's external ids never change.
    private readonly ConcurrentDictionary<string, ExternalIds> _idCache = new(StringComparer.Ordinal);

    public PlexTvClient(IHttpClientFactory httpClientFactory, IServerApplicationHost applicationHost)
    {
        _httpClientFactory = httpClientFactory;
        _clientIdentifier = "sashimi-lists-" + applicationHost.SystemId;
    }

    public async Task<PlexAccount> GetAccount(string token, CancellationToken ct)
        => PlexJson.ParseAccount(await Send(HttpMethod.Get, "https://plex.tv/api/v2/user", token, ct).ConfigureAwait(false));

    public Task<IReadOnlyList<PlexItem>> GetWatchlist(string token, CancellationToken ct)
        => PlexJson.ReadAllPages(
            (start, c) => Send(
                HttpMethod.Get,
                Discover + "/library/sections/watchlist/all?includeCollections=1&includeExternalMedia=1"
                    + "&X-Plex-Container-Start=" + start.ToString(CultureInfo.InvariantCulture)
                    + "&X-Plex-Container-Size=" + PageSize.ToString(CultureInfo.InvariantCulture),
                token,
                c),
            PageSize,
            ct);

    public async Task<ExternalIds> GetIds(string token, string ratingKey, CancellationToken ct)
    {
        if (_idCache.TryGetValue(ratingKey, out var cached))
        {
            return cached;
        }

        var ids = PlexJson.ParseIds(await Send(HttpMethod.Get, Metadata + "/library/metadata/" + Uri.EscapeDataString(ratingKey), token, ct).ConfigureAwait(false));
        _idCache[ratingKey] = ids;
        return ids;
    }

    public async Task<string?> FindRatingKey(string token, WatchlistType type, ExternalIds ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var plexType = type == WatchlistType.Movie ? "1" : "2";
        foreach (var (scheme, id) in new[] { ("tmdb", ids.Tmdb), ("tvdb", ids.Tvdb), ("imdb", ids.Imdb) })
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var url = Metadata + "/library/metadata/matches?type=" + plexType
                + "&guid=" + Uri.EscapeDataString(scheme + "://" + id.Trim());
            var key = PlexJson.ParseFirstRatingKey(await Send(HttpMethod.Get, url, token, ct).ConfigureAwait(false));
            if (key is not null)
            {
                return key;
            }
        }

        return null;
    }

    public Task Add(string token, string ratingKey, CancellationToken ct)
        => Send(HttpMethod.Put, Discover + "/actions/addToWatchlist?ratingKey=" + Uri.EscapeDataString(ratingKey), token, ct);

    public Task Remove(string token, string ratingKey, CancellationToken ct)
        => Send(HttpMethod.Put, Discover + "/actions/removeFromWatchlist?ratingKey=" + Uri.EscapeDataString(ratingKey), token, ct);

    private async Task<string> Send(HttpMethod method, string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Plex-Token", token);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Plex-Client-Identifier", _clientIdentifier);
        request.Headers.Add("X-Plex-Product", "Sashimi");

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new PlexUnauthorizedException();
        }

        if (!response.IsSuccessStatusCode)
        {
            // Status only: never the URL or headers.
            throw new HttpRequestException(
                "plex.tv returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".",
                null,
                response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
