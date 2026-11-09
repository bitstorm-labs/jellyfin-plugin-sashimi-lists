using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SashimiLists.Watchlist;

/// <summary>
/// Drops finished titles off the Watchlist: a movie once played, a show once the
/// user has no unplayed regular episodes left.
/// </summary>
public sealed class WatchlistPlayedListener : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly WatchlistService _service;
    private readonly IClock _clock;
    private readonly ILogger<WatchlistPlayedListener> _logger;

    public WatchlistPlayedListener(
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        WatchlistService service,
        IClock clock,
        ILogger<WatchlistPlayedListener> logger)
    {
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _service = service;
        _clock = clock;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.Item is not (Movie or Episode) || e.UserData is null)
        {
            return;
        }

        var save = e.SaveReason switch
        {
            UserDataSaveReason.PlaybackFinished => PlayedSave.PlaybackFinished,
            UserDataSaveReason.TogglePlayed => PlayedSave.MarkedPlayed,
            UserDataSaveReason.UpdateUserData => PlayedSave.UserDataUpdate,
            _ => PlayedSave.Other,
        };
        DateTimeOffset? lastPlayed = e.UserData.LastPlayedDate is { } last
            ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc))
            : null;
        if (DropOff.FinishedAt(save, e.UserData.Played, lastPlayed, _clock.Now) is not { } playedAt)
        {
            return;
        }

        var userId = e.UserId;
        var item = e.Item;

        // Off the event thread: the episode check queries the library.
        _ = Task.Run(() =>
        {
            try
            {
                Handle(userId, item, playedAt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Watchlist drop-off check failed: {Error}", ex.GetType().Name);
            }
        });
    }

    private void Handle(Guid userId, BaseItem item, DateTimeOffset playedAt)
    {
        switch (item)
        {
            case Movie movie:
                _service.MarkFinished(userId, WatchlistType.Movie, IdsOf(movie), playedAt);
                break;
            case Episode { Series: { } series }:
                var ids = IdsOf(series);
                if (!ids.Any || _userManager.GetUserById(userId) is not { } user)
                {
                    return;
                }

                var unplayedLeft = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    AncestorIds = [series.Id],
                    IsPlayed = false,
                    IsVirtualItem = false,
                    ParentIndexNumberNotEquals = 0,
                    Recursive = true,
                    Limit = 1,
                }).Count > 0;
                if (!unplayedLeft)
                {
                    _service.MarkFinished(userId, WatchlistType.Series, ids, playedAt);
                }

                break;
        }
    }

    private static ExternalIds IdsOf(BaseItem item) => new(
        item.GetProviderId(MetadataProvider.Tmdb),
        item.GetProviderId(MetadataProvider.Tvdb),
        item.GetProviderId(MetadataProvider.Imdb));
}
