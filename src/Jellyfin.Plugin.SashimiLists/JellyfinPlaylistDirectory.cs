using Jellyfin.Plugin.SashimiLists.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using User = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.SashimiLists;

/// <summary>Reads playlists and user names straight from Jellyfin. Never writes.</summary>
internal sealed class JellyfinPlaylistDirectory : IPlaylistDirectory
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly User? _callerUser;

    public JellyfinPlaylistDirectory(ILibraryManager libraryManager, IUserManager userManager, User? callerUser)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _callerUser = callerUser;
    }

    public PlaylistRecord? FindPlaylist(Guid playlistId, Caller caller)
    {
        if (_libraryManager.GetItemById(playlistId) is not Playlist playlist)
        {
            return null;
        }

        return new PlaylistRecord(
            playlist.Id,
            playlist.OwnerUserId,
            playlist.OpenAccess,
            (playlist.Shares ?? []).Select(s => s.UserId).ToArray(),
            ServerAllows: _callerUser is not null && playlist.IsVisible(_callerUser));
    }

    public string? FindUserName(Guid userId) => _userManager.GetUserById(userId)?.Username;
}
