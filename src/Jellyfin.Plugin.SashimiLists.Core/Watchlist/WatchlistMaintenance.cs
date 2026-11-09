namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>One pass of the scheduled task: forget deleted users, sync linked ones.</summary>
public static class WatchlistMaintenance
{
    /// <summary>
    /// Deletes the watchlist of every user Jellyfin no longer has, then syncs each
    /// linked user. One user's failure goes to <paramref name="onError"/> and the
    /// rest carry on; cancellation stops the pass.
    /// </summary>
    public static async Task Run(
        IWatchlistStore store,
        Func<Guid, bool> userExists,
        Func<Guid, CancellationToken, Task> sync,
        Action<Guid, Exception> onError,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(userExists);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(onError);

        foreach (var user in store.AllUsers())
        {
            if (!userExists(user))
            {
                store.Delete(user);
            }
        }

        foreach (var user in store.UsersWithPlex())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await sync(user, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                onError(user, e);
            }
        }
    }
}
