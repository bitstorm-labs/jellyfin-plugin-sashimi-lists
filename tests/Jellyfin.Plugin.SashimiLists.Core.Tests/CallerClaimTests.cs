namespace Jellyfin.Plugin.SashimiLists.Core.Tests;

/// <summary>Reading the caller's user id from Jellyfin's auth claim.</summary>
public class CallerClaimTests
{
    [Fact]
    public void A_user_id_claim_is_read()
    {
        Assert.True(CallerClaim.TryParse("b1a96e6185674e438a0ac75bdddf6e45", out var id));
        Assert.Equal(Guid.Parse("b1a96e61-8567-4e43-8a0a-c75bdddf6e45"), id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    // An API key has no user: its claim is the empty Guid, and UserManager.GetUserById throws on that.
    [InlineData("00000000000000000000000000000000")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void No_user_means_no_caller(string? claim) => Assert.False(CallerClaim.TryParse(claim, out _));
}
