using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SashimiLists;

/// <summary>Playlist-owner lookups and the per-user Watchlist for Sashimi clients. One setting: the Requests address.</summary>
public class SashimiListsPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public SashimiListsPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>The loaded plugin, for reading its configuration.</summary>
    public static SashimiListsPlugin? Instance { get; private set; }

    public override string Name => "Sashimi Lists";

    public override Guid Id => Guid.Parse("d95b4484-62cb-4873-92c5-66a65b86b420");

    public override string Description =>
        "Tells signed-in users who owns a playlist they can see, so shared lists "
        + "keep their creator even after Jellyfin drops a playlist's shares, and keeps "
        + "each user's Watchlist, which can hold titles not on the server and can sync "
        + "with their own Plex watchlist.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.configPage.html",
        },
    ];
}
