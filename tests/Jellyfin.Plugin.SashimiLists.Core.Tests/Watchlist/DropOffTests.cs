using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

public sealed class DropOffTests : IDisposable
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private readonly TempStore _temp = new();
    private readonly FakeClock _clock = new();
    private readonly WatchlistService _service;

    public DropOffTests()
    {
        _service = new WatchlistService(_temp.Store, new FakeLibrary(), _clock, new FakePlexApi());
    }

    public void Dispose() => _temp.Dispose();

    private void Linked() => _temp.Store.Update(Alice, w =>
    {
        w.Plex = new PlexLinkState { Token = "t", AccountUuid = "u", Username = "alice", FirstSyncDone = true };
        return 0;
    });

    [Fact]
    public void Finished_after_added_drops_off_and_queues_PushRemove()
    {
        Linked();
        var entry = _service.Add(Alice, new NewEntry(WatchlistType.Series, "Show", 2020, null, new ExternalIds("10", "20", null))).Entry;
        _temp.Store.Update(Alice, w =>
        {
            w.Outbox.Clear();
            w.Entries.Single().PlexRatingKey = "k-show";
            return 0;
        });

        _service.MarkFinished(Alice, WatchlistType.Series, new ExternalIds(null, "20", null), entry.AddedAt.AddHours(1));

        var stored = _temp.Store.Read(Alice);
        Assert.Empty(stored.Entries);
        Assert.Equal<PlexOp>([new PushRemove("k-show")], stored.Outbox);
    }

    [Fact]
    public void Finished_before_added_stays()
    {
        // A rewatch: the user played it last year and added it again today.
        var entry = _service.Add(Alice, new NewEntry(WatchlistType.Movie, "Movie", 2020, null, new ExternalIds("10", null, null))).Entry;

        _service.MarkFinished(Alice, WatchlistType.Movie, new ExternalIds("10", null, null), entry.AddedAt.AddDays(-365));
        _service.MarkFinished(Alice, WatchlistType.Movie, new ExternalIds("10", null, null), entry.AddedAt);

        Assert.Single(_temp.Store.Read(Alice).Entries);
    }

    [Fact]
    public void Finished_other_title_or_type_leaves_entry()
    {
        var entry = _service.Add(Alice, new NewEntry(WatchlistType.Movie, "Movie", 2020, null, new ExternalIds("10", null, null))).Entry;

        _service.MarkFinished(Alice, WatchlistType.Movie, new ExternalIds("11", null, null), entry.AddedAt.AddHours(1));
        _service.MarkFinished(Alice, WatchlistType.Series, new ExternalIds("10", null, null), entry.AddedAt.AddHours(1));

        Assert.Single(_temp.Store.Read(Alice).Entries);
    }

    [Fact]
    public void Finished_for_user_without_a_watchlist_writes_no_file()
    {
        _service.MarkFinished(Alice, WatchlistType.Movie, new ExternalIds("10", null, null), _clock.Now);

        Assert.Empty(_temp.Store.AllUsers());
    }

    [Fact]
    public void ShouldRemove_only_for_plays_after_the_add()
    {
        var e = new WatchlistEntry { AddedAt = _clock.Now };

        Assert.True(DropOff.ShouldRemove(e, _clock.Now.AddSeconds(1)));
        Assert.False(DropOff.ShouldRemove(e, _clock.Now));
        Assert.False(DropOff.ShouldRemove(e, _clock.Now.AddSeconds(-1)));
    }
}
