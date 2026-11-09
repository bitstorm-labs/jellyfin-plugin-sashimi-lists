using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>What a watchlist entry can be. Plex's watchlist holds only these two.</summary>
public enum WatchlistType
{
    Movie,
    Series,
}

/// <summary>Provider ids for a title. Blank ids count as missing.</summary>
public sealed record ExternalIds(string? Tmdb, string? Tvdb, string? Imdb)
{
    /// <summary>True when at least one id is present.</summary>
    [JsonIgnore]
    public bool Any => Has(Tmdb) || Has(Tvdb) || Has(Imdb);

    /// <summary>
    /// True when any id present on both sides is equal (imdb ignores case).
    /// Two sets with no id in common never agree.
    /// </summary>
    public bool Agrees(ExternalIds other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Same(Tmdb, other.Tmdb, StringComparison.Ordinal)
            || Same(Tvdb, other.Tvdb, StringComparison.Ordinal)
            || Same(Imdb, other.Imdb, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Has(string? id) => !string.IsNullOrWhiteSpace(id);

    private static bool Same(string? a, string? b, StringComparison comparison)
        => Has(a) && Has(b) && string.Equals(a!.Trim(), b!.Trim(), comparison);
}

/// <summary>Poster references clients can load from anywhere.</summary>
public static class PosterUrls
{
    /// <summary>The url when it is absolute http(s); otherwise null (relative paths would resolve against the wrong host).</summary>
    public static string? Clean(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? url
            : null;
}

/// <summary>One title on a user's watchlist, stored by external id rather than as a library item.</summary>
public sealed class WatchlistEntry
{
    public Guid Id { get; set; }

    public WatchlistType Type { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? PosterUrl { get; set; }

    public ExternalIds Ids { get; set; } = new(null, null, null);

    public DateTimeOffset AddedAt { get; set; }

    /// <summary>The Plex ratingKey this entry is synced to, once known.</summary>
    public string? PlexRatingKey { get; set; }
}

/// <summary>A user's link to their own plex.tv account. The token never leaves the plugin.</summary>
public sealed class PlexLinkState
{
    /// <summary>
    /// New each time a link is created (not when the same account's token is
    /// refreshed), so a sync that started under an earlier link can't apply to this one.
    /// </summary>
    public Guid LinkId { get; set; }

    public string Token { get; set; } = string.Empty;

    public string AccountUuid { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>plex.tv rejected the token; sync is paused until the user links again.</summary>
    public bool NeedsReconnect { get; set; }

    /// <summary>The first (union-only) sync has run for this account.</summary>
    public bool FirstSyncDone { get; set; }
}

/// <summary>A local change waiting to be pushed to Plex.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(PushAdd), "add")]
[JsonDerivedType(typeof(PushRemove), "remove")]
public abstract record PlexOp;

/// <summary>Add this entry to the user's Plex watchlist.</summary>
public sealed record PushAdd(Guid EntryId) : PlexOp;

/// <summary>Remove this ratingKey from the user's Plex watchlist.</summary>
public sealed record PushRemove(string RatingKey) : PlexOp;

/// <summary>Everything stored for one user.</summary>
public sealed class UserWatchlist
{
    public List<WatchlistEntry> Entries { get; set; } = [];

    public PlexLinkState? Plex { get; set; }

    public bool CardDismissed { get; set; }

    /// <summary>
    /// The user disconnected Plex. Clients skip auto-linking (e.g. on a Requests
    /// sign-in) while this is set; an explicit, successful link clears it.
    /// </summary>
    public bool PlexOptedOut { get; set; }

    /// <summary>The ratingKeys on the user's Plex watchlist at the end of the last successful sync.</summary>
    public HashSet<string> LastPlexSnapshot { get; set; } = new(StringComparer.Ordinal);

    public List<PlexOp> Outbox { get; set; } = [];
}

/// <summary>Per-user watchlist persistence.</summary>
public interface IWatchlistStore
{
    /// <summary>
    /// Reads the user's watchlist, applies <paramref name="change"/>, and saves it,
    /// atomically with respect to other <c>Update</c> calls for the same user.
    /// </summary>
    T Update<T>(Guid userId, Func<UserWatchlist, T> change);

    /// <summary>The user's watchlist; an empty one when nothing is stored.</summary>
    UserWatchlist Read(Guid userId);

    IEnumerable<Guid> AllUsers();

    IEnumerable<Guid> UsersWithPlex();

    void Delete(Guid userId);
}
