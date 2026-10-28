using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SashimiLists;

/// <summary>Read-only playlist-owner lookups for Sashimi clients. No settings.</summary>
public class SashimiListsPlugin : BasePlugin<BasePluginConfiguration>
{
    public SashimiListsPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public override string Name => "Sashimi Lists";

    public override Guid Id => Guid.Parse("d95b4484-62cb-4873-92c5-66a65b86b420");

    public override string Description =>
        "Tells signed-in users who owns a playlist they can see, so shared lists "
        + "keep their creator even after Jellyfin drops a playlist's shares.";
}
