using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

public sealed class WatchlistStoreTests : IDisposable
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sashimi-watchlist-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static WatchlistEntry Entry(string title, string? tmdb = null, string? tvdb = null, string? imdb = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = WatchlistType.Movie,
        Title = title,
        Year = 2024,
        PosterUrl = "https://image.example/p.jpg",
        Ids = new ExternalIds(tmdb, tvdb, imdb),
        AddedAt = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero),
        PlexRatingKey = "rk-" + title,
    };

    [Fact]
    public void Update_then_Read_round_trips_entries_link_and_outbox()
    {
        var store = new FileWatchlistStore(_dir);
        var entry = Entry("Dune", tmdb: "438631", imdb: "tt1160419");

        store.Update(Alice, w =>
        {
            w.Entries.Add(entry);
            w.Plex = new PlexLinkState { Token = "tok", AccountUuid = "uuid-1", Username = "alice", NeedsReconnect = true, FirstSyncDone = true };
            w.CardDismissed = true;
            w.LastPlexSnapshot.Add("rk-1");
            w.Outbox.Add(new PushAdd(entry.Id));
            w.Outbox.Add(new PushRemove("rk-9"));
            return 0;
        });

        // A fresh store instance reads only what is on disk.
        var read = new FileWatchlistStore(_dir).Read(Alice);

        var e = Assert.Single(read.Entries);
        Assert.Equal(entry.Id, e.Id);
        Assert.Equal(WatchlistType.Movie, e.Type);
        Assert.Equal("Dune", e.Title);
        Assert.Equal(2024, e.Year);
        Assert.Equal("https://image.example/p.jpg", e.PosterUrl);
        Assert.Equal(new ExternalIds("438631", null, "tt1160419"), e.Ids);
        Assert.Equal(entry.AddedAt, e.AddedAt);
        Assert.Equal("rk-Dune", e.PlexRatingKey);
        Assert.NotNull(read.Plex);
        Assert.Equal("tok", read.Plex!.Token);
        Assert.Equal("uuid-1", read.Plex.AccountUuid);
        Assert.Equal("alice", read.Plex.Username);
        Assert.True(read.Plex.NeedsReconnect);
        Assert.True(read.Plex.FirstSyncDone);
        Assert.True(read.CardDismissed);
        Assert.Equal(["rk-1"], read.LastPlexSnapshot);
        Assert.Equal<PlexOp>([new PushAdd(entry.Id), new PushRemove("rk-9")], read.Outbox);
    }

    [Fact]
    public void Read_of_unknown_user_is_empty_watchlist()
    {
        var read = new FileWatchlistStore(_dir).Read(Bob);

        Assert.Empty(read.Entries);
        Assert.Null(read.Plex);
        Assert.False(read.CardDismissed);
        Assert.Empty(read.LastPlexSnapshot);
        Assert.Empty(read.Outbox);
        Assert.Empty(new FileWatchlistStore(_dir).AllUsers());
    }

    [Fact]
    public void Concurrent_updates_for_one_user_lose_nothing()
    {
        var store = new FileWatchlistStore(_dir);

        Parallel.For(0, 100, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            store.Update(Alice, w =>
            {
                w.Entries.Add(Entry("t" + i, tmdb: i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return 0;
            }));

        Assert.Equal(100, new FileWatchlistStore(_dir).Read(Alice).Entries.Count);
    }

    [Fact]
    public void UsersWithPlex_lists_only_linked_users()
    {
        var store = new FileWatchlistStore(_dir);
        store.Update(Alice, w =>
        {
            w.Plex = new PlexLinkState { Token = "t", AccountUuid = "u", Username = "alice" };
            return 0;
        });
        store.Update(Bob, w =>
        {
            w.Entries.Add(Entry("x", tmdb: "1"));
            return 0;
        });

        Assert.Equal([Alice], store.UsersWithPlex());
        Assert.Equal(new[] { Alice, Bob }.OrderBy(g => g), store.AllUsers().OrderBy(g => g));

        store.Delete(Bob);
        Assert.Equal([Alice], store.AllUsers());
    }

    [Fact]
    public void Files_holding_Plex_tokens_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix permissions only.
        }

        // A directory that already exists with loose permissions is tightened too.
        Directory.CreateDirectory(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.SetUnixFileMode(_dir, (UnixFileMode)0b111_101_101); // 0755 regardless of umask

        var store = new FileWatchlistStore(_dir);
        store.Update(Alice, w =>
        {
            w.Plex = new PlexLinkState { Token = "secret", AccountUuid = "u", Username = "alice" };
            return 0;
        });

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, Alice.ToString("N") + ".json")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
    }

    [Fact]
    public void A_new_directory_is_created_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var nested = Path.Combine(_dir, "watchlists");
        new FileWatchlistStore(nested).Update(Bob, w =>
        {
            w.CardDismissed = true;
            return 0;
        });

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(nested));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(nested, Bob.ToString("N") + ".json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"Entries\":[{\"Id\":")]
    [InlineData("not json")]
    public void Unreadable_file_is_moved_aside_reported_and_reads_as_empty(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, Alice.ToString("N") + ".json");
        File.WriteAllText(path, content);
        var reported = new List<Guid>();
        var store = new FileWatchlistStore(_dir, (user, _) => reported.Add(user));

        var read = store.Read(Alice);

        Assert.Empty(read.Entries);
        Assert.Null(read.Plex);
        Assert.Equal([Alice], reported);
        Assert.False(File.Exists(path));
        Assert.Equal(content, File.ReadAllText(Assert.Single(Directory.GetFiles(_dir, Alice.ToString("N") + ".json.corrupt-*"))));
    }

    [Fact]
    public void One_unreadable_file_does_not_hide_other_linked_users()
    {
        var store = new FileWatchlistStore(_dir, (_, _) => { });
        store.Update(Bob, w =>
        {
            w.Plex = new PlexLinkState { Token = "t", AccountUuid = "u", Username = "bob" };
            return 0;
        });
        File.WriteAllText(Path.Combine(_dir, Alice.ToString("N") + ".json"), "{");

        Assert.Equal([Bob], store.UsersWithPlex());
        Assert.Equal([Bob], store.AllUsers());
    }

    [Fact]
    public void A_file_from_before_the_opt_out_flag_reads_as_not_opted_out()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, Alice.ToString("N") + ".json"), """{"Entries":[],"Plex":null,"CardDismissed":true,"LastPlexSnapshot":[],"Outbox":[]}""");

        var read = new FileWatchlistStore(_dir).Read(Alice);

        Assert.True(read.CardDismissed);
        Assert.False(read.PlexOptedOut);
    }

    [Fact]
    public void ExternalIds_Agrees_on_any_shared_id_and_not_on_disjoint()
    {
        var a = new ExternalIds("100", null, "tt0001");

        Assert.True(a.Agrees(new ExternalIds("100", "5", null)));
        Assert.True(a.Agrees(new ExternalIds(null, null, "TT0001")));
        Assert.False(a.Agrees(new ExternalIds("200", null, "tt0002")));
        Assert.False(a.Agrees(new ExternalIds(null, "5", null)));
        Assert.False(new ExternalIds(null, null, null).Agrees(new ExternalIds(null, null, null)));
        Assert.True(a.Any);
        Assert.False(new ExternalIds(null, " ", "").Any);
    }
}
