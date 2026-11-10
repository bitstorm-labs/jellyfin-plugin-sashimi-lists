using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SashimiLists;

/// <summary>Settings under Dashboard › Plugins › Sashimi Lists.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// The Requests (Seerr/Jellyseerr) server address the Sashimi apps should use.
    /// Empty means "not set by the server": each device keeps its own address.
    /// </summary>
    public string RequestsUrl { get; set; } = string.Empty;
}
