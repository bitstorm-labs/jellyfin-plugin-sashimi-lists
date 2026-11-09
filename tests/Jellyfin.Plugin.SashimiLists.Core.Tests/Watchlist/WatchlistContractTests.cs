using System.Text.Json;
using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

/// <summary>The HTTP contract three client apps are built against. Field names and status codes must not drift.</summary>
public class WatchlistContractTests
{
    private static readonly Guid EntryId = Guid.Parse("12345678-1234-1234-1234-123456789abc");

    private static string Json(object value) => JsonSerializer.Serialize(value, WatchlistContract.JsonOptions);

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    private static IEnumerable<string> Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name);

    [Fact]
    public void View_serializes_with_contract_field_names()
    {
        var view = new WatchlistViewModel(
            new PlexLinkView("alice", true),
            CardDismissed: true,
            [new EntryView(EntryId, WatchlistType.Series, "Show", 2020, "https://image.example/p.jpg", new ExternalIds("1", "2", "tt3"), new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), "abcdef")]);

        var json = Body(Json(WatchlistContract.ToDto(view)));

        Assert.Equal(["plex", "cardDismissed", "entries"], Names(json));
        Assert.Equal(["username", "needsReconnect"], Names(json.GetProperty("plex")));
        Assert.True(json.GetProperty("plex").GetProperty("needsReconnect").GetBoolean());
        var entry = json.GetProperty("entries")[0];
        Assert.Equal(["id", "type", "title", "year", "posterUrl", "tmdbId", "tvdbId", "imdbId", "addedAt", "itemId"], Names(entry));
        Assert.Equal("123456781234123412341234567890ab".Length, entry.GetProperty("id").GetString()!.Length);
        Assert.Equal(EntryId.ToString("N"), entry.GetProperty("id").GetString());
        Assert.Equal("Series", entry.GetProperty("type").GetString());
        Assert.Equal("abcdef", entry.GetProperty("itemId").GetString());
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), entry.GetProperty("addedAt").GetDateTimeOffset());
    }

    [Fact]
    public void AddedAt_is_whole_seconds_UTC_with_Z()
    {
        // Swift's .iso8601 date decoding rejects fractional seconds and needs a zone.
        var addedAt = new DateTimeOffset(2026, 11, 9, 10, 3, 12, 345, TimeSpan.FromHours(-7)).AddTicks(6789);
        var view = new WatchlistViewModel(null, false, [new EntryView(EntryId, WatchlistType.Movie, "M", null, null, new ExternalIds("1", null, null), addedAt, null)]);

        var json = Body(Json(WatchlistContract.ToDto(view)));

        Assert.Equal("2026-11-09T17:03:12Z", json.GetProperty("entries")[0].GetProperty("addedAt").GetString());
    }

    [Fact]
    public void Unlinked_view_has_null_plex_and_null_optional_fields()
    {
        var view = new WatchlistViewModel(null, false, [new EntryView(EntryId, WatchlistType.Movie, "M", null, null, new ExternalIds("1", null, null), DateTimeOffset.UnixEpoch, null)]);

        var json = Body(Json(WatchlistContract.ToDto(view)));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("plex").ValueKind);
        var entry = json.GetProperty("entries")[0];
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("itemId").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("year").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("tvdbId").ValueKind);
    }

    [Fact]
    public void Add_response_and_conflict_use_contract_names()
    {
        var entry = new EntryView(EntryId, WatchlistType.Movie, "M", null, null, new ExternalIds("1", null, null), DateTimeOffset.UnixEpoch, null);

        Assert.Equal(["entry", "added"], Names(Body(Json(WatchlistContract.ToAddResponse(entry, false)))));
        var (status, body) = WatchlistContract.ForLink(new ConflictsWith("old", "new"));
        Assert.Equal(409, status);
        var conflict = Body(Json(body!));
        Assert.Equal(["username", "newUsername"], Names(conflict));
        Assert.Equal("new", conflict.GetProperty("newUsername").GetString());
    }

    [Fact]
    public void Link_results_map_to_contract_status_codes()
    {
        var (ok, okBody) = WatchlistContract.ForLink(new Linked(new PlexLinkView("alice", false)));
        Assert.Equal(200, ok);
        Assert.Equal(["username", "needsReconnect"], Names(Body(Json(okBody!))));

        // Never 401: clients read any 401 as an expired Jellyfin session.
        var (rejected, rejectedBody) = WatchlistContract.ForLink(new Rejected());
        Assert.Equal(422, rejected);
        Assert.Null(rejectedBody);

        // plex.tv down or unreachable: the server can't answer the question yet.
        var (unavailable, unavailableBody) = WatchlistContract.ForLink(new PlexUnavailable());
        Assert.Equal(503, unavailable);
        Assert.Null(unavailableBody);
    }

    [Fact]
    public void Add_request_is_read_case_insensitively()
    {
        Assert.True(WatchlistContract.TryReadAdd(Body("""{"type":"Movie","title":"Dune","year":2021,"posterUrl":"https://i.example/d.jpg","tmdbId":"438631"}"""), out var camel));
        Assert.Equal(new NewEntry(WatchlistType.Movie, "Dune", 2021, "https://i.example/d.jpg", new ExternalIds("438631", null, null)), camel);

        Assert.True(WatchlistContract.TryReadAdd(Body("""{"Type":"series","Title":"Show","TvdbId":"5","ImdbId":"tt9"}"""), out var pascal));
        Assert.Equal(new NewEntry(WatchlistType.Series, "Show", null, null, new ExternalIds(null, "5", "tt9")), pascal);
    }

    [Theory]
    [InlineData("""{"type":"Episode","title":"x","tmdbId":"1"}""")]
    [InlineData("""{"type":"Movie","title":"x"}""")]
    [InlineData("""{"type":"Movie","title":"x","tmdbId":"  "}""")]
    [InlineData("""{"title":"x","tmdbId":"1"}""")]
    [InlineData("""{"type":"Movie","tmdbId":"1"}""")]
    [InlineData("""{"type":"0","title":"x","tmdbId":"1"}""")]
    [InlineData("""[]""")]
    [InlineData("""{"type":"Movie","title":"x","tmdbId":1}""")]
    public void Add_request_with_other_type_or_no_ids_is_rejected(string json)
    {
        Assert.False(WatchlistContract.TryReadAdd(Body(json), out _));
    }

    [Fact]
    public void Link_request_is_read_case_insensitively()
    {
        Assert.True(WatchlistContract.TryReadLink(Body("""{"token":"abc","replace":true}"""), out var token, out var replace));
        Assert.Equal(("abc", true), (token, replace));
        Assert.True(WatchlistContract.TryReadLink(Body("""{"Token":"abc"}"""), out token, out replace));
        Assert.Equal(("abc", false), (token, replace));
        Assert.False(WatchlistContract.TryReadLink(Body("""{"token":""}"""), out _, out _));
    }
}
