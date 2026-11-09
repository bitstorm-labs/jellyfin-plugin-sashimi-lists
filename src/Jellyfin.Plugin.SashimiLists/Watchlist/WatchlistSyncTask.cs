using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SashimiLists.Watchlist;

/// <summary>Every few minutes: forget deleted users' watchlists and sync linked users with Plex.</summary>
public class WatchlistSyncTask : IScheduledTask
{
    private readonly IWatchlistStore _store;
    private readonly PlexSync _sync;
    private readonly IUserManager _userManager;
    private readonly ILogger<WatchlistSyncTask> _logger;

    public WatchlistSyncTask(IWatchlistStore store, PlexSync sync, IUserManager userManager, ILogger<WatchlistSyncTask> logger)
    {
        _store = store;
        _sync = sync;
        _userManager = userManager;
        _logger = logger;
    }

    public string Name => "Sync Watchlists with Plex";

    public string Key => "SashimiListsWatchlistSync";

    public string Description => "Syncs each linked user's Sashimi Watchlist with their Plex watchlist, and removes the Watchlists of deleted users.";

    public string Category => "Sashimi Lists";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => WatchlistMaintenance.Run(
            _store,
            user => _userManager.GetUserById(user) is not null,
            _sync.SyncUser,
            // Type name only: messages from HTTP failures are kept out of logs on principle.
            (user, e) => _logger.LogWarning("Watchlist sync failed for user {UserId}: {Error}", user.ToString("N"), e.GetType().Name),
            cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(5).Ticks,
        },
    ];
}
