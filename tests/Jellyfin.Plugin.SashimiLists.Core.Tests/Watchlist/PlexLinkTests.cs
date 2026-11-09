using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

public sealed class PlexLinkTests : IDisposable
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private readonly TempStore _temp = new();
    private readonly FakePlexApi _plex = new();
    private readonly WatchlistService _service;

    public PlexLinkTests()
    {
        _service = new WatchlistService(_temp.Store, new FakeLibrary(), new FakeClock(), _plex);
    }

    public void Dispose() => _temp.Dispose();

    private UserWatchlist Stored => _temp.Store.Read(Alice);

    private void Existing(string uuid, string username, bool needsReconnect = false) => _temp.Store.Update(Alice, w =>
    {
        w.Plex = new PlexLinkState { Token = "old-token", AccountUuid = uuid, Username = username, NeedsReconnect = needsReconnect, FirstSyncDone = true };
        w.LastPlexSnapshot.Add("k-old");
        w.Outbox.Add(new PushRemove("k-old"));
        return 0;
    });

    [Fact]
    public async Task Link_with_bad_token_is_Rejected_and_stores_nothing()
    {
        var result = await _service.LinkPlex(Alice, "bad-token", replace: false, CancellationToken.None);

        Assert.IsType<Rejected>(result);
        Assert.Null(Stored.Plex);
        Assert.False(File.Exists(Path.Combine(_temp.Directory, Alice.ToString("N") + ".json")));
    }

    [Fact]
    public async Task Link_new_user_is_Linked_and_needs_a_first_sync()
    {
        var result = await _service.LinkPlex(Alice, FakePlexApi.Token, replace: false, CancellationToken.None);

        Assert.Equal(new PlexLinkView("alice", false), Assert.IsType<Linked>(result).Link);
        Assert.Equal(FakePlexApi.Token, Stored.Plex!.Token);
        Assert.Equal("uuid-alice", Stored.Plex.AccountUuid);
        Assert.False(Stored.Plex.FirstSyncDone);
    }

    [Fact]
    public async Task Link_to_different_account_without_replace_is_ConflictsWith_current_and_new_username()
    {
        Existing("uuid-other", "someone-else");

        var result = await _service.LinkPlex(Alice, FakePlexApi.Token, replace: false, CancellationToken.None);

        var conflict = Assert.IsType<ConflictsWith>(result);
        Assert.Equal("someone-else", conflict.Username);
        Assert.Equal("alice", conflict.NewUsername);
        Assert.Equal("old-token", Stored.Plex!.Token);
        Assert.Equal("uuid-other", Stored.Plex.AccountUuid);
    }

    [Fact]
    public async Task Link_same_account_refreshes_token_and_clears_needsReconnect_keeping_FirstSyncDone()
    {
        Existing("uuid-alice", "alice", needsReconnect: true);

        var result = await _service.LinkPlex(Alice, FakePlexApi.Token, replace: false, CancellationToken.None);

        Assert.Equal(new PlexLinkView("alice", false), Assert.IsType<Linked>(result).Link);
        var stored = Stored;
        Assert.Equal(FakePlexApi.Token, stored.Plex!.Token);
        Assert.False(stored.Plex.NeedsReconnect);
        Assert.True(stored.Plex.FirstSyncDone);
        Assert.Equal(["k-old"], stored.LastPlexSnapshot);
        Assert.Equal<PlexOp>([new PushRemove("k-old")], stored.Outbox);
    }

    [Fact]
    public async Task Link_new_account_with_replace_resets_first_sync()
    {
        Existing("uuid-other", "someone-else");
        _service.Add(Alice, new NewEntry(WatchlistType.Movie, "Kept", null, null, new ExternalIds("1", null, null)));

        var result = await _service.LinkPlex(Alice, FakePlexApi.Token, replace: true, CancellationToken.None);

        Assert.IsType<Linked>(result);
        var stored = Stored;
        Assert.Equal("uuid-alice", stored.Plex!.AccountUuid);
        Assert.Equal("alice", stored.Plex.Username);
        Assert.False(stored.Plex.FirstSyncDone);
        Assert.False(stored.Plex.NeedsReconnect);
        Assert.Empty(stored.LastPlexSnapshot);
        Assert.Empty(stored.Outbox); // the old account's pending ops must not reach the new one
        Assert.Single(stored.Entries);
    }

    [Fact]
    public async Task Unlink_keeps_entries()
    {
        await _service.LinkPlex(Alice, FakePlexApi.Token, replace: false, CancellationToken.None);
        _service.Add(Alice, new NewEntry(WatchlistType.Movie, "Kept", null, null, new ExternalIds("1", null, null)));
        _temp.Store.Update(Alice, w =>
        {
            w.LastPlexSnapshot.Add("k1");
            return 0;
        });

        _service.UnlinkPlex(Alice);

        var stored = Stored;
        Assert.Null(stored.Plex);
        Assert.Empty(stored.Outbox);
        Assert.Empty(stored.LastPlexSnapshot);
        Assert.Equal("Kept", Assert.Single(stored.Entries).Title);
        Assert.Null(_service.View(Alice).Plex);
    }

    public static TheoryData<Exception> PlexDown() => new()
    {
        new HttpRequestException("plex.tv returned HTTP 503.", null, System.Net.HttpStatusCode.ServiceUnavailable),
        new HttpRequestException("Connection refused"),
        new TaskCanceledException("timeout"),
        new FormatException("garbled"),
    };

    [Theory]
    [MemberData(nameof(PlexDown))]
    public async Task Link_while_plex_tv_is_unreachable_is_Unavailable_and_stores_nothing(Exception failure)
    {
        _plex.AccountFailure = failure;

        var result = await _service.LinkPlex(Alice, FakePlexApi.Token, replace: false, CancellationToken.None);

        Assert.IsType<PlexUnavailable>(result);
        Assert.Null(Stored.Plex);
    }

    [Fact]
    public async Task Link_cancelled_by_the_caller_is_not_reported_as_Unavailable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _plex.AccountFailure = new TaskCanceledException("caller went away", null, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.LinkPlex(Alice, FakePlexApi.Token, replace: false, cts.Token));
    }
}
