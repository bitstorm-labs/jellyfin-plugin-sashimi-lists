using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using Jellyfin.Plugin.SashimiLists.Watchlist;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SashimiLists;

/// <summary>Registers the Watchlist services. All singletons: the store's per-user locks must be shared.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IWatchlistStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<FileWatchlistStore>();
            return new FileWatchlistStore(
                Path.Combine(sp.GetRequiredService<IApplicationPaths>().DataPath, "sashimi-lists", "watchlists"),
                (user, e) => logger.LogError(
                    "Watchlist file for user {UserId} was unreadable ({Error}); moved it aside as .corrupt-* and started an empty Watchlist",
                    user.ToString("N"),
                    e.GetType().Name));
        });
        serviceCollection.AddSingleton<IClock, SystemClock>();
        serviceCollection.AddSingleton<JellyfinLibraryLookup>();
        serviceCollection.AddSingleton<ILibraryLookup>(sp => sp.GetRequiredService<JellyfinLibraryLookup>());
        serviceCollection.AddSingleton<IPlexWatchlistApi, PlexTvClient>();
        serviceCollection.AddSingleton<WatchlistService>();
        serviceCollection.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<PlexSync>();
            return new PlexSync(
                sp.GetRequiredService<IWatchlistStore>(),
                sp.GetRequiredService<IPlexWatchlistApi>(),
                sp.GetRequiredService<IClock>(),
                // A count only: titles from a user's Plex list stay out of the logs.
                (user, count) => logger.LogInformation(
                    "Watchlist sync for user {UserId}: {Count} Plex title(s) have no TMDB/TVDB/IMDb id yet; retrying next run",
                    user.ToString("N"),
                    count));
        });
        serviceCollection.AddSingleton<PosterFinder>();
        serviceCollection.AddHostedService<WatchlistPlayedListener>();
    }
}
