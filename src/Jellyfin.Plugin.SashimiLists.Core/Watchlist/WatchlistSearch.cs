namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>A movie or series the library search found, as the caller can see it.</summary>
public sealed record LibraryTitle(string ItemId, WatchlistType Type, string Name, int? Year, ExternalIds Ids, string? Overview);

/// <summary>A movie or series the server's remote metadata providers (TMDB, ...) found.</summary>
public sealed record RemoteTitle(WatchlistType Type, string Name, int? Year, ExternalIds Ids, string? PosterUrl, string? Overview);

/// <summary>
/// One search result. <paramref name="ItemId"/> is set for titles on the server
/// (that the caller can see); <paramref name="EntryId"/> when the title is already
/// on the caller's Watchlist.
/// </summary>
public sealed record SearchHit(
    WatchlistType Type,
    string Name,
    int? Year,
    ExternalIds Ids,
    string? PosterUrl,
    string? Overview,
    string? ItemId,
    Guid? EntryId);

/// <summary>The two result groups of a Watchlist search.</summary>
public sealed record SearchResults(IReadOnlyList<SearchHit> OnServer, IReadOnlyList<SearchHit> NotOnServer);

/// <summary>Builds the "On your server" / "Not on server" groups for the Watchlist's ＋ Add search.</summary>
public static class WatchlistSearch
{
    /// <summary>Results per group.</summary>
    public const int GroupLimit = 20;

    /// <summary>Longest query accepted, in characters.</summary>
    public const int MaxQueryLength = 200;

    /// <summary>The trimmed query; false (a 400) when it is missing, blank or too long.</summary>
    public static bool TryReadQuery(string? raw, out string query)
    {
        query = raw?.Trim() ?? string.Empty;
        return query.Length > 0 && query.Length <= MaxQueryLength;
    }

    /// <summary>
    /// Merges library and remote hits. <paramref name="remoteLibraryMatches"/> lines up
    /// with <paramref name="remote"/>: the library item id (visible to the caller) each
    /// remote hit agrees with, or null. A remote hit that matches the library by any
    /// provider id is shown once, under "On your server". Remote hits with no
    /// provider id can't be added, so they are left out.
    /// </summary>
    public static SearchResults Combine(
        IReadOnlyList<LibraryTitle> library,
        IReadOnlyList<RemoteTitle> remote,
        IReadOnlyList<string?> remoteLibraryMatches,
        IReadOnlyList<WatchlistEntry> watchlist,
        int limit = GroupLimit)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(remoteLibraryMatches);
        ArgumentNullException.ThrowIfNull(watchlist);
        if (remoteLibraryMatches.Count != remote.Count)
        {
            throw new ArgumentException("One library match (or null) per remote hit.", nameof(remoteLibraryMatches));
        }

        var onServer = new List<SearchHit>();
        foreach (var title in library)
        {
            if (!onServer.Any(h => h.ItemId == title.ItemId))
            {
                onServer.Add(new SearchHit(title.Type, title.Name, title.Year, title.Ids, null, Blank(title.Overview), title.ItemId, null));
            }
        }

        var notOnServer = new List<SearchHit>();
        for (var i = 0; i < remote.Count; i++)
        {
            var hit = remote[i];
            if (hit.Ids is null || !hit.Ids.Any || string.IsNullOrWhiteSpace(hit.Name))
            {
                continue;
            }

            var match = onServer.FindIndex(h => WatchlistService.SameTitle(h.Type, h.Ids, hit.Type, hit.Ids));
            if (match < 0 && remoteLibraryMatches[i] is { } itemId)
            {
                match = onServer.FindIndex(h => h.ItemId == itemId);
                if (match < 0)
                {
                    onServer.Add(new SearchHit(hit.Type, hit.Name, hit.Year, hit.Ids, PosterUrls.Clean(hit.PosterUrl), Blank(hit.Overview), itemId, null));
                    continue;
                }
            }

            if (match >= 0)
            {
                onServer[match] = Enrich(onServer[match], hit);
                continue;
            }

            var duplicate = notOnServer.FindIndex(h => WatchlistService.SameTitle(h.Type, h.Ids, hit.Type, hit.Ids));
            if (duplicate >= 0)
            {
                notOnServer[duplicate] = Enrich(notOnServer[duplicate], hit);
                continue;
            }

            notOnServer.Add(new SearchHit(hit.Type, hit.Name, hit.Year, hit.Ids, PosterUrls.Clean(hit.PosterUrl), Blank(hit.Overview), null, null));
        }

        return new SearchResults(Finish(onServer, watchlist, limit), Finish(notOnServer, watchlist, limit));
    }

    /// <summary>
    /// a[0], b[0], a[1], b[1], ..., then the rest of the longer list. Remote movie and
    /// series searches each return about a page; this keeps one from filling the group.
    /// </summary>
    public static IReadOnlyList<T> Interleave<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var result = new List<T>(a.Count + b.Count);
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i < a.Count)
            {
                result.Add(a[i]);
            }

            if (i < b.Count)
            {
                result.Add(b[i]);
            }
        }

        return result;
    }

    /// <summary>The ids of both, preferring <paramref name="a"/>'s where both have one.</summary>
    public static ExternalIds Union(ExternalIds a, ExternalIds b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return new ExternalIds(Blank(a.Tmdb) ?? Blank(b.Tmdb), Blank(a.Tvdb) ?? Blank(b.Tvdb), Blank(a.Imdb) ?? Blank(b.Imdb));
    }

    private static SearchHit Enrich(SearchHit hit, RemoteTitle extra) => hit with
    {
        Ids = Union(hit.Ids, extra.Ids),
        PosterUrl = hit.PosterUrl ?? PosterUrls.Clean(extra.PosterUrl),
        Overview = hit.Overview ?? Blank(extra.Overview),
        Year = hit.Year ?? extra.Year,
    };

    private static List<SearchHit> Finish(List<SearchHit> hits, IReadOnlyList<WatchlistEntry> watchlist, int limit)
        => hits.Take(limit)
            .Select(h => h with { EntryId = watchlist.FirstOrDefault(e => WatchlistService.SameTitle(e.Type, e.Ids, h.Type, h.Ids))?.Id })
            .ToList();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
