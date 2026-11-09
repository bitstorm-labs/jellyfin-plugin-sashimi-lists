using Jellyfin.Plugin.SashimiLists.Core.Watchlist;

namespace Jellyfin.Plugin.SashimiLists.Core.Tests.Watchlist;

public sealed class WatchlistMaintenanceTests : IDisposable
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Gone = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005");

    private readonly TempStore _temp = new();

    public void Dispose() => _temp.Dispose();

    private void Seed(Guid user, bool linked) => _temp.Store.Update(user, w =>
    {
        w.CardDismissed = true;
        if (linked)
        {
            w.Plex = new PlexLinkState { Token = "t", AccountUuid = user.ToString(), Username = "u" };
        }

        return 0;
    });

    [Fact]
    public async Task Deleted_users_lose_their_file_and_linked_users_are_synced()
    {
        Seed(Alice, linked: true);
        Seed(Bob, linked: false);
        Seed(Gone, linked: true);
        var synced = new List<Guid>();

        await WatchlistMaintenance.Run(
            _temp.Store,
            userExists: u => u != Gone,
            sync: (u, _) =>
            {
                synced.Add(u);
                return Task.CompletedTask;
            },
            onError: (_, e) => throw e,
            CancellationToken.None);

        Assert.Equal([Alice], synced);
        Assert.Equal(new[] { Alice, Bob }.Order(), _temp.Store.AllUsers().Order());
    }

    [Fact]
    public async Task One_users_failure_does_not_stop_the_others()
    {
        Seed(Alice, linked: true);
        Seed(Bob, linked: true);
        var synced = new List<Guid>();
        var failed = new List<(Guid, Type)>();

        await WatchlistMaintenance.Run(
            _temp.Store,
            userExists: _ => true,
            sync: (u, _) =>
            {
                synced.Add(u);
                return u == Alice ? Task.FromException(new HttpRequestException("503")) : Task.CompletedTask;
            },
            onError: (u, e) => failed.Add((u, e.GetType())),
            CancellationToken.None);

        Assert.Equal(new[] { Alice, Bob }.Order(), synced.Order());
        Assert.Equal([(Alice, typeof(HttpRequestException))], failed);
    }

    [Fact]
    public async Task Cancellation_stops_the_run()
    {
        Seed(Alice, linked: true);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WatchlistMaintenance.Run(
            _temp.Store, _ => true, (_, ct) => Task.FromCanceled(ct), (_, e) => throw e, cts.Token));
    }
}
