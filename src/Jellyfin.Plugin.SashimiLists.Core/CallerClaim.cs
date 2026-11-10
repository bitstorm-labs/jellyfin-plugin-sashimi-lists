namespace Jellyfin.Plugin.SashimiLists.Core;

/// <summary>The signed-in user behind a request, from Jellyfin's <c>Jellyfin-UserId</c> claim.</summary>
public static class CallerClaim
{
    /// <summary>
    /// The user id in the claim. False when it is missing, malformed, or the empty
    /// Guid (an API key with no user), so callers never pass it to
    /// <c>UserManager.GetUserById</c>, which throws on an empty Guid.
    /// </summary>
    public static bool TryParse(string? claim, out Guid userId)
        => Guid.TryParse(claim, out userId) && userId != Guid.Empty;
}
