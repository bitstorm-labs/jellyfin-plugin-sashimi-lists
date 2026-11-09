namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>Finds a watchlist title in the Jellyfin library. The plugin shell backs this with Jellyfin.</summary>
public interface ILibraryLookup
{
    /// <summary>
    /// The id (N format) of a library item of this type that agrees with
    /// <paramref name="ids"/> and that <paramref name="userId"/> is allowed to see,
    /// or null when there is none.
    /// </summary>
    string? FindVisibleItemId(Guid userId, WatchlistType type, ExternalIds ids);
}

/// <summary>The current time; swapped for a fake in tests.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

/// <summary>The real clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}
