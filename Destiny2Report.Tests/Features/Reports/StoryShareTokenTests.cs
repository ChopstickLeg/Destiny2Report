using Destiny2Report.API.Features.Reports;
using Destiny2Report.API.Features.Auth;
using System.Reflection;

namespace Destiny2Report.Tests.Features.Reports;

public sealed class StoryShareTokenTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("membership-3-4611686018467000000")]
    [InlineData("5cAFpklO0tjpaO6yfK8yP9xGShNnWRWb8g_HrQDBWj!")]
    public void IsValidStoryShareToken_rejects_invalid_tokens(string? token)
    {
        Assert.False(IsValid(token));
    }

    [Fact]
    public void IsValidStoryShareToken_accepts_256_bit_base64url_tokens()
    {
        Assert.True(IsValid(
            "5cAFpklO0tjpaO6yfK8yP9xGShNnWRWb8g_HrQDBWjs"));
    }

    [Fact]
    public void HashStoryShareToken_is_stable_without_storing_the_token()
    {
        const string token = "5cAFpklO0tjpaO6yfK8yP9xGShNnWRWb8g_HrQDBWjs";

        var hash = Hash(token);

        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(token, hash, StringComparison.Ordinal);
        Assert.Equal(hash, Hash(token));
    }

    [Fact]
    public void OwnsStoryMembership_requires_an_exact_membership_owned_by_the_signed_in_player()
    {
        var player = new SignedInPlayerResponse(
            SignedIn: true,
            BungieNetUser: null,
            DestinyMemberships:
            [
                new DestinyMembershipResponse(
                    MembershipType: 3,
                    MembershipId: 4611686018467000000,
                    DisplayName: null,
                    BungieGlobalDisplayName: null,
                    BungieGlobalDisplayNameCode: null,
                    IconPath: null,
                    CrossSaveOverride: 0,
                    ApplicableMembershipTypes: [],
                    IsPublic: true)
            ],
            PrimaryDestinyMembership: null);

        Assert.True(Owns(player, 3, 4611686018467000000));
        Assert.False(Owns(player, 2, 4611686018467000000));
        Assert.False(Owns(player, 3, 4611686018467000001));
        Assert.False(Owns(player with { SignedIn = false }, 3, 4611686018467000000));
    }

    private static bool IsValid(string? token) =>
        (bool)InvokeHandler("IsValidStoryShareToken", token)!;

    private static string Hash(string token) =>
        (string)InvokeHandler("HashStoryShareToken", token)!;

    private static bool Owns(SignedInPlayerResponse player, int membershipTypeId, long membershipId) =>
        (bool)InvokeHandler("OwnsStoryMembership", player, membershipTypeId, membershipId)!;

    private static object? InvokeHandler(string methodName, params object?[] arguments)
    {
        var method = typeof(ReportHandlers).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return method.Invoke(null, arguments);
    }
}
