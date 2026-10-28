namespace Jellyfin.Plugin.SashimiLists.Core;

/// <summary>Who is asking.</summary>
public sealed record Caller(Guid UserId, bool IsAdmin);

/// <summary>
/// What the visibility rule needs to know about one playlist.
/// </summary>
/// <param name="Id">The playlist id.</param>
/// <param name="OwnerUserId">The playlist's creator (<c>Playlist.OwnerUserId</c>).</param>
/// <param name="OpenAccess">Public: any signed-in user may see it.</param>
/// <param name="SharedWith">Users the playlist is shared with.</param>
/// <param name="ServerAllows">
/// Jellyfin's own <c>Playlist.IsVisible(user)</c> for the caller. It adds the
/// parental-rating check on top of owner/shared/public, so a non-admin must pass
/// both. Admins skip it: they can always see a playlist.
/// </param>
public sealed record PlaylistRecord(
    Guid Id,
    Guid OwnerUserId,
    bool OpenAccess,
    IReadOnlyCollection<Guid> SharedWith,
    bool ServerAllows = true);

/// <summary>The response body for one playlist.</summary>
public sealed record OwnerInfo(string OwnerUserId, string OwnerName);

/// <summary>Where playlists and user names come from. The plugin shell backs this with Jellyfin.</summary>
public interface IPlaylistDirectory
{
    /// <summary>The playlist with this id as seen by <paramref name="caller"/>, or null if it doesn't exist or isn't a playlist.</summary>
    PlaylistRecord? FindPlaylist(Guid playlistId, Caller caller);

    /// <summary>The user's name, or null if the user no longer exists.</summary>
    string? FindUserName(Guid userId);
}

public static class PlaylistVisibility
{
    /// <summary>
    /// Admins see everything. Anyone else sees a playlist they own, one shared
    /// with them, or a public (OpenAccess) one, and only if Jellyfin agrees.
    /// </summary>
    public static bool CanSee(Caller caller, PlaylistRecord playlist)
    {
        if (caller.IsAdmin)
        {
            return true;
        }

        var ruleAllows = playlist.OpenAccess
            || (playlist.OwnerUserId != Guid.Empty && playlist.OwnerUserId == caller.UserId)
            || playlist.SharedWith.Contains(caller.UserId);

        return ruleAllows && playlist.ServerAllows;
    }
}

public sealed class PlaylistOwnerService
{
    /// <summary>The batch endpoint's limit on ids per request.</summary>
    public const int MaxBatchIds = 200;

    private readonly IPlaylistDirectory _directory;

    public PlaylistOwnerService(IPlaylistDirectory directory)
    {
        _directory = directory;
    }

    /// <summary>The owner, or null when the playlist doesn't exist, isn't a playlist, or the caller can't see it.</summary>
    public OwnerInfo? GetOwner(Caller caller, Guid playlistId)
    {
        var playlist = _directory.FindPlaylist(playlistId, caller);
        if (playlist is null || !PlaylistVisibility.CanSee(caller, playlist))
        {
            return null;
        }

        return new OwnerInfo(
            playlist.OwnerUserId.ToString("N"),
            playlist.OwnerUserId == Guid.Empty ? string.Empty : _directory.FindUserName(playlist.OwnerUserId) ?? string.Empty);
    }

    /// <summary>
    /// Owners keyed by the playlist id (N format), holding only the playlists the
    /// caller can see. Everything else is left out without saying why.
    /// </summary>
    public Dictionary<string, OwnerInfo> GetOwners(Caller caller, IEnumerable<Guid> playlistIds)
    {
        var result = new Dictionary<string, OwnerInfo>(StringComparer.Ordinal);
        var names = new Dictionary<Guid, string>();
        foreach (var id in playlistIds.Distinct())
        {
            var playlist = _directory.FindPlaylist(id, caller);
            if (playlist is null || !PlaylistVisibility.CanSee(caller, playlist))
            {
                continue;
            }

            if (!names.TryGetValue(playlist.OwnerUserId, out var name))
            {
                name = playlist.OwnerUserId == Guid.Empty
                    ? string.Empty
                    : _directory.FindUserName(playlist.OwnerUserId) ?? string.Empty;
                names[playlist.OwnerUserId] = name;
            }

            result[id.ToString("N")] = new OwnerInfo(playlist.OwnerUserId.ToString("N"), name);
        }

        return result;
    }

    /// <summary>
    /// Parses the batch endpoint's comma-separated <c>ids</c>. Ids that aren't
    /// GUIDs are skipped, like playlists the caller can't see; duplicates count once.
    /// Returns false when there are more than <see cref="MaxBatchIds"/> distinct ids.
    /// </summary>
    public static bool TryParseIds(string? ids, out List<Guid> parsed)
    {
        parsed = [];
        if (string.IsNullOrWhiteSpace(ids))
        {
            return true;
        }

        var seen = new HashSet<Guid>();
        foreach (var part in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var id) && seen.Add(id))
            {
                parsed.Add(id);
            }
        }

        return parsed.Count <= MaxBatchIds;
    }
}
