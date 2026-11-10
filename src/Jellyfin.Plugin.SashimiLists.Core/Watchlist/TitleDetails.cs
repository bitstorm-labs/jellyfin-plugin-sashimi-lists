using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>
/// What the server's metadata providers returned for a title, unfiltered. The
/// plugin shell fills this from Jellyfin; <see cref="TitleDetailsRules.Build"/>
/// cleans it up.
/// </summary>
public sealed record TitleDetailsSource(
    string? Name,
    int? Year,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    int? RuntimeMinutes,
    int? SeasonCount,
    string? OfficialRating,
    IReadOnlyList<string>? Genres,
    float? CommunityRating,
    string? ProviderName,
    ExternalIds Ids);

/// <summary>
/// The details page of a title. Null members are unknown and left out of the JSON.
/// <paramref name="ItemId"/> is set when the title is on the server (visible to the
/// caller); <paramref name="EntryId"/> when it is on the caller's Watchlist.
/// </summary>
public sealed record TitleDetails(
    WatchlistType Type,
    string Name,
    int? Year,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    int? RuntimeMinutes,
    int? SeasonCount,
    string? OfficialRating,
    IReadOnlyList<string> Genres,
    double? CommunityRating,
    string? Source,
    ExternalIds Ids,
    string? ItemId,
    Guid? EntryId);

/// <summary>Rules for the details endpoint.</summary>
public static partial class TitleDetailsRules
{
    /// <summary>
    /// Reads <c>type</c> (<c>movie</c> or <c>series</c>, any case) and the ids. TMDB
    /// and TVDB ids are numbers, IMDb ids look like <c>tt123</c>. False (a 400) when
    /// the type is anything else, no id is given, or one is malformed.
    /// </summary>
    public static bool TryReadQuery(string? type, string? tmdbId, string? tvdbId, string? imdbId, out WatchlistType parsed, out ExternalIds ids)
    {
        ids = new ExternalIds(null, null, null);
        if (string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase))
        {
            parsed = WatchlistType.Movie;
        }
        else if (string.Equals(type, "series", StringComparison.OrdinalIgnoreCase))
        {
            parsed = WatchlistType.Series;
        }
        else
        {
            parsed = default;
            return false;
        }

        var tmdb = Blank(tmdbId);
        var tvdb = Blank(tvdbId);
        var imdb = Blank(imdbId);
        if ((tmdb is not null && !Number().IsMatch(tmdb))
            || (tvdb is not null && !Number().IsMatch(tvdb))
            || (imdb is not null && !Imdb().IsMatch(imdb)))
        {
            return false;
        }

        ids = new ExternalIds(tmdb, tvdb, imdb);
        return ids.Any;
    }

    /// <summary>
    /// Cleans up what the providers gave: blanks, zeros and non-http(s) image URLs
    /// become unknown, genres are de-duplicated, the rating is rounded to one
    /// decimal, runtime is kept for movies and season count for series only.
    /// <paramref name="requested"/> fills ids the provider didn't return. Null when
    /// the providers didn't even know the title's name (a 404).
    /// </summary>
    public static TitleDetails? Build(
        WatchlistType type,
        TitleDetailsSource source,
        string? itemId,
        IReadOnlyList<WatchlistEntry> watchlist,
        ExternalIds? requested = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(watchlist);
        var name = Blank(source.Name);
        if (name is null)
        {
            return null;
        }

        var ids = requested is null ? source.Ids : WatchlistSearch.Union(source.Ids, requested);
        var genres = (source.Genres ?? [])
            .Select(Blank)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        double? rating = source.CommunityRating > 0f ? Math.Round((double)source.CommunityRating.Value, 1, MidpointRounding.AwayFromZero) : null;
        return new TitleDetails(
            type,
            name,
            source.Year is > 0 ? source.Year : null,
            Blank(source.Overview),
            PosterUrls.Clean(source.PosterUrl),
            PosterUrls.Clean(source.BackdropUrl),
            type == WatchlistType.Movie && source.RuntimeMinutes is > 0 ? source.RuntimeMinutes : null,
            type == WatchlistType.Series && source.SeasonCount is > 0 ? source.SeasonCount : null,
            Blank(source.OfficialRating),
            genres,
            rating,
            SourceLabel(source.ProviderName),
            ids,
            itemId,
            watchlist.FirstOrDefault(e => WatchlistService.SameTitle(e.Type, e.Ids, type, ids))?.Id);
    }

    /// <summary>The provider's name as people know it ("TheMovieDb" is "TMDB").</summary>
    public static string? SourceLabel(string? providerName)
    {
        var name = Blank(providerName);
        if (name is null)
        {
            return null;
        }

        if (string.Equals(name, "TheMovieDb", StringComparison.OrdinalIgnoreCase))
        {
            return "TMDB";
        }

        return string.Equals(name, "The Open Movie Database", StringComparison.OrdinalIgnoreCase) ? "OMDb" : name;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[0-9]{1,12}$")]
    private static partial Regex Number();

    [GeneratedRegex("^tt[0-9]{1,12}$", RegexOptions.IgnoreCase)]
    private static partial Regex Imdb();
}
