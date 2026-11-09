namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>One title on a Plex watchlist. The list call carries no external ids.</summary>
public sealed record PlexItem(string RatingKey, WatchlistType Type, string Title, int? Year, string? Thumb);

/// <summary>The plex.tv account a token belongs to.</summary>
public sealed record PlexAccount(string Uuid, string Username);

/// <summary>plex.tv rejected the token (HTTP 401). Carries no token or URL.</summary>
public sealed class PlexUnauthorizedException : Exception
{
    public PlexUnauthorizedException()
        : base("plex.tv rejected the token.")
    {
    }

    public PlexUnauthorizedException(string message)
        : base(message)
    {
    }

    public PlexUnauthorizedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A user's own plex.tv watchlist. The plugin shell backs this with plex.tv.</summary>
public interface IPlexWatchlistApi
{
    Task<PlexAccount> GetAccount(string token, CancellationToken ct);

    /// <summary>Every movie and show on the watchlist, all pages, or throws. Never a partial list.</summary>
    Task<IReadOnlyList<PlexItem>> GetWatchlist(string token, CancellationToken ct);

    Task<ExternalIds> GetIds(string token, string ratingKey, CancellationToken ct);

    /// <summary>The Plex ratingKey for a title with these ids, or null when Plex doesn't know it.</summary>
    Task<string?> FindRatingKey(string token, WatchlistType type, ExternalIds ids, CancellationToken ct);

    Task Add(string token, string ratingKey, CancellationToken ct);

    Task Remove(string token, string ratingKey, CancellationToken ct);
}
