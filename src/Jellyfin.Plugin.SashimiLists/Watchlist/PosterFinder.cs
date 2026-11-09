using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SashimiLists.Watchlist;

/// <summary>
/// A remote (provider) poster for a title on the server, so its tile keeps artwork
/// if the file is later deleted. Best effort: any failure means no poster.
/// </summary>
public sealed class PosterFinder
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly JellyfinLibraryLookup _lookup;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<PosterFinder> _logger;

    public PosterFinder(JellyfinLibraryLookup lookup, IProviderManager providerManager, ILogger<PosterFinder> logger)
    {
        _lookup = lookup;
        _providerManager = providerManager;
        _logger = logger;
    }

    public async Task<string?> Find(Guid userId, WatchlistType type, ExternalIds ids, CancellationToken ct)
    {
        try
        {
            var item = _lookup.FindVisibleItem(userId, type, ids);
            if (item is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var images = await _providerManager.GetAvailableRemoteImages(
                item,
                new RemoteImageQuery(string.Empty) { ImageType = ImageType.Primary },
                timeout.Token).ConfigureAwait(false);
            return images.Select(i => PosterUrls.Clean(i.Url)).FirstOrDefault(u => u is not null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("No remote poster for a watchlist title ({Error})", e.GetType().Name);
            return null;
        }
    }
}
