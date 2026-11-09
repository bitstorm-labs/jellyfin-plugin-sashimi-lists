using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>
/// The Watchlist HTTP contract, consumed verbatim by the Apple, Android and Roku
/// clients. JSON is camelCase even though Jellyfin's own API is PascalCase, so the
/// names are pinned here rather than left to the server's serializer settings.
/// </summary>
public static class WatchlistContract
{
    /// <summary>Serializer options for every Watchlist response.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static WatchlistViewDto ToDto(WatchlistViewModel view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new WatchlistViewDto(
            view.Plex is null ? null : ToDto(view.Plex),
            view.CardDismissed,
            view.Entries.Select(ToDto).ToList(),
            view.PlexOptedOut);
    }

    public static PlexLinkDto ToDto(PlexLinkView link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return new PlexLinkDto(link.Username, link.NeedsReconnect);
    }

    public static WatchlistEntryDto ToDto(EntryView e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new WatchlistEntryDto(
            e.Id.ToString("N"),
            e.Type.ToString(),
            e.Title,
            e.Year,
            e.PosterUrl,
            e.Ids.Tmdb,
            e.Ids.Tvdb,
            e.Ids.Imdb,
            FormatTime(e.AddedAt),
            e.ItemId);
    }

    /// <summary>
    /// ISO-8601 in UTC with whole seconds and a <c>Z</c>: the strictest common
    /// parser (Swift's <c>.iso8601</c>) rejects fractional seconds.
    /// </summary>
    public static string FormatTime(DateTimeOffset time)
        => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static AddItemResponseDto ToAddResponse(EntryView entry, bool added) => new(ToDto(entry), added);

    /// <summary>
    /// Status and body for <c>POST /SashimiLists/Watchlist/Plex</c>. A rejected
    /// token is 422, never 401: clients read any 401 as an expired Jellyfin session.
    /// plex.tv unreachable or failing is 503.
    /// </summary>
    public static (int Status, object? Body) ForLink(LinkResult result) => result switch
    {
        Linked linked => (200, ToDto(linked.Link)),
        ConflictsWith conflict => (409, new LinkConflictDto(conflict.Username, conflict.NewUsername)),
        Rejected => (422, null),
        PlexUnavailable => (503, null),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    /// <summary>
    /// Reads a <c>POST Items</c> body, matching property names case-insensitively.
    /// False (a 400) for anything but Movie/Series, a missing title, or no external id.
    /// </summary>
    public static bool TryReadAdd(JsonElement body, out NewEntry entry)
    {
        entry = null!;
        if (body.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = String(body, "type");
        var title = String(body, "title");
        if (string.IsNullOrWhiteSpace(title) || !TryParseType(type, out var watchlistType))
        {
            return false;
        }

        if (!TryString(body, "tmdbId", out var tmdb) || !TryString(body, "tvdbId", out var tvdb) || !TryString(body, "imdbId", out var imdb))
        {
            return false;
        }

        var ids = new ExternalIds(Blank(tmdb), Blank(tvdb), Blank(imdb));
        if (!ids.Any)
        {
            return false;
        }

        int? year = Find(body, "year") is { ValueKind: JsonValueKind.Number } y && y.TryGetInt32(out var parsedYear) ? parsedYear : null;
        entry = new NewEntry(watchlistType, title.Trim(), year, String(body, "posterUrl"), ids);
        return true;
    }

    /// <summary>Reads a <c>POST Plex</c> body (<c>{ token, replace }</c>), case-insensitively.</summary>
    public static bool TryReadLink(JsonElement body, out string token, out bool replace)
    {
        token = string.Empty;
        replace = false;
        if (body.ValueKind != JsonValueKind.Object || String(body, "token") is not { Length: > 0 } t || string.IsNullOrWhiteSpace(t))
        {
            return false;
        }

        token = t;
        replace = Find(body, "replace") is { ValueKind: JsonValueKind.True };
        return true;
    }

    private static bool TryParseType(string? value, out WatchlistType type)
    {
        type = default;
        if (string.Equals(value, "Movie", StringComparison.OrdinalIgnoreCase))
        {
            type = WatchlistType.Movie;
            return true;
        }

        if (string.Equals(value, "Series", StringComparison.OrdinalIgnoreCase))
        {
            type = WatchlistType.Series;
            return true;
        }

        return false;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonElement? Find(JsonElement body, string name)
    {
        foreach (var property in body.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? String(JsonElement body, string name)
        => Find(body, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>Absent or null is fine; present but not a string is a malformed body.</summary>
    private static bool TryString(JsonElement body, string name, out string? value)
    {
        value = null;
        switch (Find(body, name))
        {
            case null:
            case { ValueKind: JsonValueKind.Null }:
                return true;
            case { ValueKind: JsonValueKind.String } s:
                value = s.GetString();
                return true;
            default:
                return false;
        }
    }
}

public sealed record WatchlistViewDto(
    [property: JsonPropertyName("plex")] PlexLinkDto? Plex,
    [property: JsonPropertyName("cardDismissed")] bool CardDismissed,
    [property: JsonPropertyName("entries")] IReadOnlyList<WatchlistEntryDto> Entries,
    [property: JsonPropertyName("plexOptedOut")] bool PlexOptedOut);

public sealed record PlexLinkDto(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("needsReconnect")] bool NeedsReconnect);

public sealed record WatchlistEntryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("posterUrl")] string? PosterUrl,
    [property: JsonPropertyName("tmdbId")] string? TmdbId,
    [property: JsonPropertyName("tvdbId")] string? TvdbId,
    [property: JsonPropertyName("imdbId")] string? ImdbId,
    [property: JsonPropertyName("addedAt")] string AddedAt,
    [property: JsonPropertyName("itemId")] string? ItemId);

public sealed record AddItemResponseDto(
    [property: JsonPropertyName("entry")] WatchlistEntryDto Entry,
    [property: JsonPropertyName("added")] bool Added);

public sealed record LinkConflictDto(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("newUsername")] string NewUsername);
