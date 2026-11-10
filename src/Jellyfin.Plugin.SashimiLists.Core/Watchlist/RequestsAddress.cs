namespace Jellyfin.Plugin.SashimiLists.Core.Watchlist;

/// <summary>The "Requests address" plugin setting: the Seerr/Jellyseerr server the apps should use.</summary>
public static class RequestsAddress
{
    /// <summary>
    /// The configured address, trimmed and without a trailing slash, when it is an
    /// absolute http(s) URL; otherwise null ("not set by the server").
    /// </summary>
    public static string? Normalize(string? configured)
    {
        var value = configured?.Trim().TrimEnd('/');
        return !string.IsNullOrEmpty(value)
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && !string.IsNullOrEmpty(uri.Host)
            ? value
            : null;
    }
}
