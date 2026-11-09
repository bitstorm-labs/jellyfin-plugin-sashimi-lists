using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SashimiLists.Watchlist;

/// <summary>
/// Finds a watchlist title in the library as one user sees it. Querying with the
/// user applies their library access and parental limits, so a title they can't
/// see has no item id for them.
/// </summary>
public sealed class JellyfinLibraryLookup : ILibraryLookup
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public JellyfinLibraryLookup(ILibraryManager libraryManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    public string? FindVisibleItemId(Guid userId, WatchlistType type, ExternalIds ids)
        => FindVisibleItem(userId, type, ids)?.Id.ToString("N");

    /// <summary>The library item itself, for callers in the plugin shell.</summary>
    public BaseItem? FindVisibleItem(Guid userId, WatchlistType type, ExternalIds ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var user = _userManager.GetUserById(userId);
        if (user is null || !ids.Any)
        {
            return null;
        }

        var providerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ids.Tmdb))
        {
            providerIds[MetadataProvider.Tmdb.ToString()] = ids.Tmdb.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ids.Tvdb))
        {
            providerIds[MetadataProvider.Tvdb.ToString()] = ids.Tvdb.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ids.Imdb))
        {
            providerIds[MetadataProvider.Imdb.ToString()] = ids.Imdb.Trim();
        }

        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [type == WatchlistType.Movie ? BaseItemKind.Movie : BaseItemKind.Series],
            HasAnyProviderId = providerIds,
            IsVirtualItem = false,
            Recursive = true,
            Limit = 1,
        }).FirstOrDefault();
    }
}
