using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
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

    public IReadOnlyList<string?> FindVisibleItemIds(Guid userId, IReadOnlyList<(WatchlistType Type, ExternalIds Ids)> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var result = new string?[titles.Count];
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return result;
        }

        // One query per type for every id on the list, then match locally.
        foreach (var type in new[] { WatchlistType.Movie, WatchlistType.Series })
        {
            var wanted = Enumerable.Range(0, titles.Count).Where(i => titles[i].Type == type && titles[i].Ids.Any).ToList();
            if (wanted.Count == 0)
            {
                continue;
            }

            var providerIds = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            void Collect(MetadataProvider provider, Func<ExternalIds, string?> pick)
            {
                var values = wanted.Select(i => pick(titles[i].Ids)).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.Ordinal).ToArray();
                if (values.Length > 0)
                {
                    providerIds[provider.ToString()] = values;
                }
            }

            Collect(MetadataProvider.Tmdb, ids => ids.Tmdb);
            Collect(MetadataProvider.Tvdb, ids => ids.Tvdb);
            Collect(MetadataProvider.Imdb, ids => ids.Imdb);

            var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [type == WatchlistType.Movie ? BaseItemKind.Movie : BaseItemKind.Series],
                HasAnyProviderIds = providerIds,
                IsVirtualItem = false,
                Recursive = true,
            }).Select(item => (item.Id, Ids: IdsOf(item))).ToList();

            foreach (var i in wanted)
            {
                var match = items.FirstOrDefault(item => item.Ids.Agrees(titles[i].Ids));
                result[i] = match.Ids is null ? null : match.Id.ToString("N");
            }
        }

        return result;
    }

    /// <summary>
    /// Movies and series whose name matches <paramref name="query"/>, as the user
    /// sees the library (access and parental limits apply), best match first.
    /// </summary>
    public IReadOnlyList<LibraryTitle> Search(Guid userId, string query, int limit)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return [];
        }

        return _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                SearchTerm = query,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                IsVirtualItem = false,
                Recursive = true,
                Limit = limit,
            })
            .Select(item => new LibraryTitle(
                item.Id.ToString("N"),
                item is Series ? WatchlistType.Series : WatchlistType.Movie,
                item.Name ?? string.Empty,
                item.ProductionYear,
                IdsOf(item),
                item.Overview))
            .ToList();
    }

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

    private static ExternalIds IdsOf(BaseItem item) => new(
        item.GetProviderId(MetadataProvider.Tmdb),
        item.GetProviderId(MetadataProvider.Tvdb),
        item.GetProviderId(MetadataProvider.Imdb));
}
