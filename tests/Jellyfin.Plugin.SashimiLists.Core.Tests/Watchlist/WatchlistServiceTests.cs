using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

public sealed class WatchlistServiceTests : IDisposable
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly TempStore _temp = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLibrary _library = new();
    private readonly WatchlistService _service;

    public WatchlistServiceTests()
    {
        _service = new WatchlistService(_temp.Store, _library, _clock, new FakePlexApi());
    }

    public void Dispose() => _temp.Dispose();

    private static NewEntry Movie(string title, string? tmdb = null, string? imdb = null, string? tvdb = null)
        => new(WatchlistType.Movie, title, 2024, null, new ExternalIds(tmdb, tvdb, imdb));

    private void Link(Guid user) => _temp.Store.Update(user, w =>
    {
        w.Plex = new PlexLinkState { Token = "t", AccountUuid = "u", Username = "alice", FirstSyncDone = true };
        return 0;
    });

    [Fact]
    public void Add_twice_with_agreeing_ids_returns_existing_and_added_false()
    {
        var first = _service.Add(Alice, Movie("Dune", tmdb: "438631"));
        _clock.Tick();
        var second = _service.Add(Alice, Movie("Dune (2021)", tmdb: null, imdb: "tt1160419") with
        {
            Ids = new ExternalIds("438631", null, "tt1160419"),
        });

        Assert.True(first.Added);
        Assert.False(second.Added);
        Assert.Equal(first.Entry.Id, second.Entry.Id);
        Assert.Equal("Dune", second.Entry.Title);
        Assert.Single(_service.View(Alice).Entries);
    }

    [Fact]
    public void Add_with_same_tmdb_id_but_other_type_is_a_different_title()
    {
        // TMDB movie and TV ids are separate number spaces.
        _service.Add(Alice, Movie("A movie", tmdb: "1399"));
        var show = _service.Add(Alice, new NewEntry(WatchlistType.Series, "A show", 2011, null, new ExternalIds("1399", null, null)));

        Assert.True(show.Added);
        Assert.Equal(2, _service.View(Alice).Entries.Count);
    }

    [Fact]
    public void Add_without_ids_throws()
    {
        Assert.Throws<ArgumentException>(() => _service.Add(Alice, Movie("Home video", tmdb: " ")));
        Assert.Empty(_service.View(Alice).Entries);
    }

    [Fact]
    public void View_orders_newest_first_and_fills_itemId_from_lookup()
    {
        _service.Add(Alice, Movie("Old", tmdb: "1"));
        _clock.Tick();
        _service.Add(Alice, Movie("New", tmdb: "2"));
        _library.Visible[(Alice, "1")] = "item1";

        var view = _service.View(Alice);

        Assert.Equal(["New", "Old"], view.Entries.Select(e => e.Title));
        Assert.Null(view.Entries[0].ItemId);
        Assert.Equal("item1", view.Entries[1].ItemId);
        Assert.Equal(_clock.Now, view.Entries[0].AddedAt);
        Assert.Null(view.Plex);
        Assert.False(view.CardDismissed);
    }

    [Fact]
    public void View_itemId_is_null_when_lookup_hides_it_from_this_user()
    {
        _service.Add(Alice, Movie("Rated R", tmdb: "7"));
        _service.Add(Bob, Movie("Rated R", tmdb: "7"));
        _library.Visible[(Alice, "7")] = "item7"; // Bob can't see it (parental rating / library access)

        Assert.Equal("item7", Assert.Single(_service.View(Alice).Entries).ItemId);
        Assert.Null(Assert.Single(_service.View(Bob).Entries).ItemId);
    }

    [Fact]
    public void Add_while_linked_queues_PushAdd_and_not_when_unlinked()
    {
        var unlinked = _service.Add(Bob, Movie("x", tmdb: "1"));
        Link(Alice);
        var linked = _service.Add(Alice, Movie("y", tmdb: "2"));

        Assert.Empty(_temp.Store.Read(Bob).Outbox);
        Assert.Equal<PlexOp>([new PushAdd(linked.Entry.Id)], _temp.Store.Read(Alice).Outbox);
        Assert.True(unlinked.Added);
    }

    [Fact]
    public void Remove_of_synced_entry_queues_PushRemove_with_its_ratingKey()
    {
        Link(Alice);
        var synced = _service.Add(Alice, Movie("synced", tmdb: "1")).Entry;
        var local = _service.Add(Alice, Movie("local", tmdb: "2")).Entry;
        _temp.Store.Update(Alice, w =>
        {
            w.Outbox.Clear();
            w.Entries.Single(e => e.Id == synced.Id).PlexRatingKey = "rk1";
            return 0;
        });

        _service.Remove(Alice, synced.Id);
        _service.Remove(Alice, local.Id);

        var stored = _temp.Store.Read(Alice);
        Assert.Empty(stored.Entries);
        Assert.Equal<PlexOp>([new PushRemove("rk1")], stored.Outbox);
    }

    [Fact]
    public void Remove_of_missing_id_is_noop()
    {
        Link(Alice);
        var kept = _service.Add(Alice, Movie("kept", tmdb: "1")).Entry;

        _service.Remove(Alice, Guid.NewGuid());

        var stored = _temp.Store.Read(Alice);
        Assert.Equal(kept.Id, Assert.Single(stored.Entries).Id);
        Assert.Equal<PlexOp>([new PushAdd(kept.Id)], stored.Outbox);
    }

    [Theory]
    [InlineData("/Items/abc/Images/Primary")]
    [InlineData("image.tmdb.org/t/p/w500/x.jpg")]
    [InlineData("ftp://image.example/x.jpg")]
    [InlineData("")]
    public void Add_drops_a_posterUrl_that_is_not_absolute_http(string posterUrl)
    {
        var added = _service.Add(Alice, Movie("x", tmdb: "1") with { PosterUrl = posterUrl }).Entry;

        Assert.Null(added.PosterUrl);
        Assert.Null(_temp.Store.Read(Alice).Entries.Single().PosterUrl);
    }

    [Fact]
    public void Add_keeps_an_absolute_https_posterUrl()
    {
        var added = _service.Add(Alice, Movie("x", tmdb: "1") with { PosterUrl = "https://image.tmdb.org/t/p/w500/x.jpg" }).Entry;

        Assert.Equal("https://image.tmdb.org/t/p/w500/x.jpg", added.PosterUrl);
    }

    [Fact]
    public void View_looks_up_the_library_once_for_the_whole_list()
    {
        _service.Add(Alice, Movie("a", tmdb: "1"));
        _service.Add(Alice, Movie("b", tmdb: "2"));
        _service.Add(Alice, Movie("c", tmdb: "3"));
        _library.Visible[(Alice, "2")] = "item2";
        var before = (_library.SingleCalls, _library.BatchCalls);

        var view = _service.View(Alice);

        Assert.Equal((before.SingleCalls, before.BatchCalls + 1), (_library.SingleCalls, _library.BatchCalls));
        Assert.Equal("item2", view.Entries.Single(e => e.Title == "b").ItemId);
        Assert.Null(view.Entries.Single(e => e.Title == "a").ItemId);
    }

    [Fact]
    public async Task AddAsync_of_a_title_already_listed_does_not_look_up_a_poster()
    {
        _service.Add(Alice, Movie("Dune", tmdb: "438631"));
        var lookups = 0;

        var (entry, added) = await _service.AddAsync(Alice, Movie("Dune", tmdb: "438631"), _ =>
        {
            lookups++;
            return Task.FromResult<string?>("https://image.example/p.jpg");
        }, CancellationToken.None);

        Assert.False(added);
        Assert.Equal(0, lookups);
        Assert.Null(entry.PosterUrl);
    }

    [Fact]
    public async Task AddAsync_without_a_poster_fills_it_from_the_source()
    {
        var (entry, added) = await _service.AddAsync(Alice, Movie("Dune", tmdb: "438631"), _ => Task.FromResult<string?>("https://image.example/p.jpg"), CancellationToken.None);

        Assert.True(added);
        Assert.Equal("https://image.example/p.jpg", entry.PosterUrl);
    }

    [Fact]
    public async Task AddAsync_with_a_poster_does_not_ask_the_source()
    {
        var (entry, _) = await _service.AddAsync(Alice, Movie("Dune", tmdb: "438631") with { PosterUrl = "https://image.example/given.jpg" }, _ => throw new InvalidOperationException("asked"), CancellationToken.None);

        Assert.Equal("https://image.example/given.jpg", entry.PosterUrl);
    }

    [Fact]
    public void DismissCard_is_remembered()
    {
        _service.DismissCard(Alice);

        Assert.True(_service.View(Alice).CardDismissed);
    }
}
