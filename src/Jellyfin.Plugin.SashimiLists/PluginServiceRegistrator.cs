using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using Jellyfin.Plugin.SashimiLists.Watchlist;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SashimiLists;

/// <summary>Registers the Watchlist services. All singletons: the store's per-user locks must be shared.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IWatchlistStore>(sp => new FileWatchlistStore(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().DataPath, "sashimi-lists", "watchlists")));
        serviceCollection.AddSingleton<IClock, SystemClock>();
        serviceCollection.AddSingleton<JellyfinLibraryLookup>();
        serviceCollection.AddSingleton<ILibraryLookup>(sp => sp.GetRequiredService<JellyfinLibraryLookup>());
        serviceCollection.AddSingleton<IPlexWatchlistApi, PlexTvClient>();
        serviceCollection.AddSingleton<WatchlistService>();
        serviceCollection.AddSingleton<PlexSync>();
        serviceCollection.AddSingleton<PosterFinder>();
        serviceCollection.AddHostedService<WatchlistPlayedListener>();
    }
}
