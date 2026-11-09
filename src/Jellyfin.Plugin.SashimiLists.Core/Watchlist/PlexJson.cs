using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>One page of the Plex watchlist. <see cref="RawCount"/> counts every row, kept or not.</summary>
public sealed record PlexPage(int? TotalSize, int RawCount, IReadOnlyList<PlexItem> Items);

/// <summary>The Plex watchlist changed while it was being paged; the read is discarded and retried next run.</summary>
public sealed class PlexListChangedException : Exception
{
    public PlexListChangedException()
        : base("The Plex watchlist changed while it was being read.")
    {
    }

    public PlexListChangedException(string message)
        : base(message)
    {
    }

    public PlexListChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Parses plex.tv JSON responses. Throws <see cref="FormatException"/> on a shape it can't use.</summary>
public static class PlexJson
{
    public static PlexAccount ParseAccount(string json)
    {
        using var doc = Parse(json);
        var root = doc.RootElement;
        var uuid = Str(root, "uuid");
        if (string.IsNullOrEmpty(uuid))
        {
            throw new FormatException("plex.tv account response has no uuid.");
        }

        var username = Str(root, "username");
        return new PlexAccount(uuid, string.IsNullOrEmpty(username) ? Str(root, "title") ?? string.Empty : username);
    }

    public static PlexPage ParseWatchlistPage(string json)
    {
        using var doc = Parse(json);
        var container = Container(doc.RootElement);
        int? total = container.TryGetProperty("totalSize", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null;
        var items = new List<PlexItem>();
        var raw = 0;
        foreach (var m in Metadata(container))
        {
            raw++;
            var key = Str(m, "ratingKey");
            WatchlistType? type = Str(m, "type") switch
            {
                "movie" => WatchlistType.Movie,
                "show" => WatchlistType.Series,
                _ => null,
            };
            if (string.IsNullOrEmpty(key) || type is null)
            {
                continue;
            }

            int? year = m.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var yy) ? yy : null;
            items.Add(new PlexItem(key, type.Value, Str(m, "title") ?? string.Empty, year, Str(m, "thumb")));
        }

        return new PlexPage(total, raw, items);
    }

    /// <summary>
    /// Every page of the watchlist, or throws. A list that changes size while it is
    /// paged could skip a title, which a sync would read as a removal, so every page
    /// must report the same <c>totalSize</c> and the rows read must add up to it.
    /// </summary>
    public static async Task<IReadOnlyList<PlexItem>> ReadAllPages(Func<int, CancellationToken, Task<string>> fetchPage, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fetchPage);
        const int MaxPages = 500;
        var items = new List<PlexItem>();
        var start = 0;
        int? firstTotal = null;
        for (var pages = 0; pages < MaxPages; pages++)
        {
            var page = ParseWatchlistPage(await fetchPage(start, ct).ConfigureAwait(false));
            firstTotal ??= page.TotalSize;
            if (page.TotalSize != firstTotal)
            {
                throw new PlexListChangedException();
            }

            items.AddRange(page.Items);
            start += page.RawCount;
            var total = page.TotalSize ?? (page.RawCount < pageSize ? start : int.MaxValue);
            if (page.RawCount == 0 || start >= total)
            {
                if (start != total && page.TotalSize is not null)
                {
                    throw new PlexListChangedException();
                }

                return items;
            }
        }

        throw new PlexListChangedException("The Plex watchlist has more pages than expected.");
    }

    public static ExternalIds ParseIds(string json)
    {
        using var doc = Parse(json);
        string? tmdb = null, tvdb = null, imdb = null;
        var first = Metadata(Container(doc.RootElement)).FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("Guid", out var guids) && guids.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in guids.EnumerateArray())
            {
                var id = Str(g, "id");
                if (id is null)
                {
                    continue;
                }

                tmdb ??= After(id, "tmdb://");
                tvdb ??= After(id, "tvdb://");
                imdb ??= After(id, "imdb://");
            }
        }

        return new ExternalIds(tmdb, tvdb, imdb);
    }

    public static string? ParseFirstRatingKey(string json)
    {
        using var doc = Parse(json);
        var first = Metadata(Container(doc.RootElement)).FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object ? Str(first, "ratingKey") : null;
    }

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new FormatException("plex.tv returned malformed JSON.", e);
        }
    }

    private static JsonElement Container(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("MediaContainer", out var c) && c.ValueKind == JsonValueKind.Object
            ? c
            : throw new FormatException("plex.tv response has no MediaContainer.");

    private static IEnumerable<JsonElement> Metadata(JsonElement container)
        => container.TryGetProperty("Metadata", out var m) && m.ValueKind == JsonValueKind.Array ? m.EnumerateArray().ToList() : [];

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static string? After(string id, string prefix)
        => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && id.Length > prefix.Length
            ? id[prefix.Length..].ToString(CultureInfo.InvariantCulture)
            : null;
}
