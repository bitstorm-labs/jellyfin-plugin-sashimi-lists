using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SashimiLists.Watchlist;

/// <summary>Reads and deletes a user's own playlists for the one-time Watchlist merge.</summary>
public sealed class JellyfinOldPlaylists : IOldWatchlistPlaylists
{
    private readonly ILibraryManager _libraryManager;

    public JellyfinOldPlaylists(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public IReadOnlyList<OldPlaylist> FindOwned(Guid userId, Func<string, bool> isWatchlistName)
    {
        ArgumentNullException.ThrowIfNull(isWatchlistName);
        // Owned only: a playlist shared with the user, or a public one, isn't theirs to merge and delete.
        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Playlist],
                Recursive = true,
            })
            .OfType<Playlist>()
            .Where(p => p.OwnerUserId.Equals(userId) && isWatchlistName(p.Name))
            .OrderBy(p => p.DateCreated)
            .Select(p => new OldPlaylist(p.Id, p.GetManageableItems().Select(t => ToItem(t.Item2)).ToList()))
            .ToList();
    }

    public void Delete(Guid playlistId)
    {
        if (_libraryManager.GetItemById(playlistId) is not Playlist playlist)
        {
            return;
        }

        // As DELETE /Items/{id} does, so the playlist folder goes too and no scan brings it back.
        _libraryManager.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = true }, true);
    }

    private static PlaylistItem ToItem(BaseItem item) => item switch
    {
        Movie => new PlaylistItem(Id(item), PlaylistItemKind.Movie, Info(item), null),
        Series => new PlaylistItem(Id(item), PlaylistItemKind.Series, Info(item), null),
        Season season => new PlaylistItem(Id(item), PlaylistItemKind.Season, Info(item), season.Series is { } s ? Info(s) : null),
        Episode episode => new PlaylistItem(Id(item), PlaylistItemKind.Episode, Info(item), episode.Series is { } s ? Info(s) : null),
        _ => new PlaylistItem(Id(item), PlaylistItemKind.Other, Info(item), null),
    };

    private static string Id(BaseItem item) => item.Id.ToString("N");

    private static TitleInfo Info(BaseItem item) => new(
        item.Name ?? string.Empty,
        item.ProductionYear,
        new ExternalIds(
            item.GetProviderId(MetadataProvider.Tmdb),
            item.GetProviderId(MetadataProvider.Tvdb),
            item.GetProviderId(MetadataProvider.Imdb)));
}
