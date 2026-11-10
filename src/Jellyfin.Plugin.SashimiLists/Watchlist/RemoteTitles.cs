using System.Reflection;
using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SashimiLists.Watchlist;

/// <summary>
/// Searches and describes titles through the server's own remote metadata
/// providers (TMDB, plus any others the admin enabled), on behalf of ordinary
/// users: Jellyfin's /Items/RemoteSearch is admin-only. Nothing here touches the
/// library. Provider failures mean fewer results, never an error.
/// </summary>
public sealed class RemoteTitles
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _config;
    private readonly IServerApplicationPaths _paths;
    private readonly IServiceProvider _services;
    private readonly ILogger<RemoteTitles> _logger;

    public RemoteTitles(
        IProviderManager providerManager,
        IServerConfigurationManager config,
        IServerApplicationPaths paths,
        IServiceProvider services,
        ILogger<RemoteTitles> logger)
    {
        _providerManager = providerManager;
        _config = config;
        _paths = paths;
        _services = services;
        _logger = logger;
    }

    /// <summary>Movies and series matching <paramref name="query"/>, interleaved.</summary>
    public async Task<IReadOnlyList<RemoteTitle>> Search(string query, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var movies = SearchType<Movie, MovieInfo>(new MovieInfo { Name = query }, WatchlistType.Movie, timeout.Token);
        var series = SearchType<Series, SeriesInfo>(new SeriesInfo { Name = query }, WatchlistType.Series, timeout.Token);
        await Task.WhenAll(movies, series).ConfigureAwait(false);
        return WatchlistSearch.Interleave(await movies.ConfigureAwait(false), await series.ConfigureAwait(false));
    }

    /// <summary>What the providers know about one title; a source with no name when they know nothing.</summary>
    public async Task<TitleDetailsSource> Describe(WatchlistType type, ExternalIds ids, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var token = timeout.Token;
        var (item, provider) = type == WatchlistType.Movie
            ? await Metadata<Movie, MovieInfo>(new MovieInfo(), ids, token).ConfigureAwait(false)
            : await Metadata<Series, SeriesInfo>(new SeriesInfo(), ids, token).ConfigureAwait(false);

        var known = item is null ? ids : WatchlistSearch.Union(IdsOf(item), ids);
        var images = Images(type, known, provider, token);
        var extras = TmdbExtras(type, known.Tmdb, token);
        var searchHit = item is null ? FindById(type, ids, token) : Task.FromResult<RemoteTitle?>(null);
        await Task.WhenAll(images, extras, searchHit).ConfigureAwait(false);
        var (poster, backdrop) = await images.ConfigureAwait(false);
        var (runtimeMinutes, seasonCount) = await extras.ConfigureAwait(false);
        var hit = await searchHit.ConfigureAwait(false);

        if (item is null)
        {
            // No metadata provider answered; a search by id may still know the basics.
            return new TitleDetailsSource(
                hit?.Name, hit?.Year, hit?.Overview, poster ?? hit?.PosterUrl, backdrop, runtimeMinutes, seasonCount,
                null, null, null, null, hit is null ? ids : WatchlistSearch.Union(hit.Ids, ids));
        }

        int? itemRuntime = item.RunTimeTicks is > 0 ? (int)Math.Round(TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes) : null;
        return new TitleDetailsSource(
            item.Name,
            item.ProductionYear ?? item.PremiereDate?.Year,
            item.Overview,
            poster,
            backdrop,
            type == WatchlistType.Movie ? itemRuntime ?? runtimeMinutes : runtimeMinutes,
            seasonCount,
            item.OfficialRating,
            item.Genres,
            item.CommunityRating,
            provider,
            known);
    }

    private async Task<IReadOnlyList<RemoteTitle>> SearchType<TItem, TLookup>(TLookup info, WatchlistType type, CancellationToken ct)
        where TItem : BaseItem, new()
        where TLookup : ItemLookupInfo
    {
        try
        {
            var results = await _providerManager.GetRemoteSearchResults<TItem, TLookup>(
                new RemoteSearchQuery<TLookup> { SearchInfo = info },
                ct).ConfigureAwait(false);
            return results.Where(r => !string.IsNullOrWhiteSpace(r.Name)).Select(r => ToRemote(type, r)).ToList();
        }
        catch (Exception e)
        {
            _logger.LogWarning("Watchlist remote {Type} search failed ({Error})", type, e.GetType().Name);
            return [];
        }
    }

    private async Task<RemoteTitle?> FindById(WatchlistType type, ExternalIds ids, CancellationToken ct)
    {
        var hits = type == WatchlistType.Movie
            ? await SearchType<Movie, MovieInfo>(WithIds(new MovieInfo(), ids), type, ct).ConfigureAwait(false)
            : await SearchType<Series, SeriesInfo>(WithIds(new SeriesInfo(), ids), type, ct).ConfigureAwait(false);
        return hits.FirstOrDefault(h => h.Ids.Agrees(ids));
    }

    private async Task<(BaseItem? Item, string? Provider)> Metadata<TItem, TLookup>(TLookup info, ExternalIds ids, CancellationToken ct)
        where TItem : BaseItem, IHasLookupInfo<TLookup>, new()
        where TLookup : ItemLookupInfo, new()
    {
        WithIds(info, ids);
        var providers = _providerManager.GetMetadataProviders<TItem>(Dummy<TItem>(ids), new LibraryOptions())
            .OfType<IRemoteMetadataProvider<TItem, TLookup>>();
        foreach (var provider in providers)
        {
            try
            {
                var result = await provider.GetMetadata(info, ct).ConfigureAwait(false);
                if (result.HasMetadata && result.Item is { } item && !string.IsNullOrWhiteSpace(item.Name))
                {
                    return (item, provider.Name);
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning("Watchlist details: {Provider} failed ({Error})", provider.Name, e.GetType().Name);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Poster and backdrop, from the provider that gave the metadata when it has
    /// images (so "Details from TMDB" is true of the artwork too, and providers that
    /// can't handle a stand-in item, such as TheTVDB's, aren't asked and don't log
    /// errors); otherwise from any image provider.
    /// </summary>
    private async Task<(string? Poster, string? Backdrop)> Images(WatchlistType type, ExternalIds ids, string? provider, CancellationToken ct)
    {
        try
        {
            BaseItem item = type == WatchlistType.Movie ? Dummy<Movie>(ids) : Dummy<Series>(ids);
            var images = provider is null
                ? []
                : (await _providerManager.GetAvailableRemoteImages(item, new RemoteImageQuery(provider), ct).ConfigureAwait(false)).ToList();
            if (images.Count == 0)
            {
                images = (await _providerManager.GetAvailableRemoteImages(item, new RemoteImageQuery(string.Empty), ct).ConfigureAwait(false)).ToList();
            }

            string? First(ImageType kind) => images.Where(i => i.Type == kind).Select(i => PosterUrls.Clean(i.Url)).FirstOrDefault(u => u is not null);
            return (First(ImageType.Primary), First(ImageType.Backdrop));
        }
        catch (Exception e)
        {
            _logger.LogDebug("Watchlist details: no remote images ({Error})", e.GetType().Name);
            return (null, null);
        }
    }

    /// <summary>
    /// Runtime (movies) and season count (series) from Jellyfin's built-in TMDB
    /// client. Its metadata providers fetch both but don't copy them onto the item,
    /// and the client isn't part of the plugin API, so this is reached by name and
    /// is best effort: any mismatch after a Jellyfin update just leaves both unknown.
    /// The client caches TMDB responses, so this reuses the provider's fetch.
    /// </summary>
    private async Task<(int? RuntimeMinutes, int? SeasonCount)> TmdbExtras(WatchlistType type, string? tmdbId, CancellationToken ct)
    {
        if (!int.TryParse(tmdbId, out var id))
        {
            return (null, null);
        }

        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "MediaBrowser.Providers");
            var clientType = assembly?.GetType("MediaBrowser.Providers.Plugins.Tmdb.TmdbClientManager", throwOnError: false);
            var client = clientType is null ? null : _services.GetService(clientType);
            var method = clientType?.GetMethod(
                type == WatchlistType.Movie ? "GetMovieAsync" : "GetSeriesAsync",
                BindingFlags.Public | BindingFlags.Instance,
                [typeof(int), typeof(string), typeof(string), typeof(string), typeof(CancellationToken)]);
            if (client is null || method is null)
            {
                return (null, null);
            }

            var language = _config.Configuration.PreferredMetadataLanguage;
            var country = _config.Configuration.MetadataCountryCode;
            // The same image-language list the TMDB providers pass, so a cache miss here caches what they'd fetch.
            var imageLanguages = assembly!.GetType("MediaBrowser.Providers.Plugins.Tmdb.TmdbUtils", throwOnError: false)
                ?.GetMethod("GetImageLanguagesParam", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string)])
                ?.Invoke(null, [language, country]) as string;
            if (method.Invoke(client, [id, language, imageLanguages, country, ct]) is not Task task)
            {
                return (null, null);
            }

            await task.ConfigureAwait(false);
            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            int? Read(string name) => result?.GetType().GetProperty(name)?.GetValue(result) is int value && value > 0 ? value : null;
            return type == WatchlistType.Movie ? (Read("Runtime"), null) : (null, Read("NumberOfSeasons"));
        }
        catch (Exception e)
        {
            _logger.LogDebug("Watchlist details: no TMDB runtime/season count ({Error})", e.GetType().Name);
            return (null, null);
        }
    }

    /// <summary>
    /// A throwaway item to ask providers about, built the way Jellyfin's own
    /// RemoteSearch builds one when there is no library item. Never saved.
    /// </summary>
    private TItem Dummy<TItem>(ExternalIds ids)
        where TItem : BaseItem, new()
    {
        var item = new TItem
        {
            Path = Path.Combine(_paths.InternalMetadataPath, "dummy"),
            ParentId = Guid.NewGuid(),
        };
        item.SetParent(new Folder());
        SetIds(item, ids);
        return item;
    }

    private TLookup WithIds<TLookup>(TLookup info, ExternalIds ids)
        where TLookup : ItemLookupInfo
    {
        // Some providers (TheTVDB's) dereference Name even when an id is given.
        info.Name ??= string.Empty;
        info.MetadataLanguage = _config.Configuration.PreferredMetadataLanguage;
        info.MetadataCountryCode = _config.Configuration.MetadataCountryCode;
        SetIds(info, ids);
        return info;
    }

    private static void SetIds(IHasProviderIds target, ExternalIds ids)
    {
        if (!string.IsNullOrWhiteSpace(ids.Tmdb))
        {
            target.SetProviderId(MetadataProvider.Tmdb, ids.Tmdb.Trim());
        }

        if (!string.IsNullOrWhiteSpace(ids.Tvdb))
        {
            target.SetProviderId(MetadataProvider.Tvdb, ids.Tvdb.Trim());
        }

        if (!string.IsNullOrWhiteSpace(ids.Imdb))
        {
            target.SetProviderId(MetadataProvider.Imdb, ids.Imdb.Trim());
        }
    }

    private static ExternalIds IdsOf(IHasProviderIds item) => new(
        item.GetProviderId(MetadataProvider.Tmdb),
        item.GetProviderId(MetadataProvider.Tvdb),
        item.GetProviderId(MetadataProvider.Imdb));

    private static RemoteTitle ToRemote(WatchlistType type, RemoteSearchResult r)
        => new(type, r.Name!.Trim(), r.ProductionYear ?? r.PremiereDate?.Year, IdsOf(r), r.ImageUrl, r.Overview);
}
