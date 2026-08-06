using System.Reflection;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Auth;

namespace Destiny2Report.Tests.Features.Auth;

public sealed class BungieAuthMembershipTests
{
    [Fact]
    public void Signed_in_player_has_no_implicit_primary_without_cross_save()
    {
        var data = UserMemberships(primaryMembershipId: null);

        var player = ToSignedInPlayerResponse(data);

        Assert.Equal(2, player.DestinyMemberships.Count);
        Assert.Null(player.PrimaryDestinyMembership);
    }

    [Fact]
    public void Signed_in_player_uses_bungies_cross_save_primary()
    {
        var data = UserMemberships(primaryMembershipId: 202);

        var player = ToSignedInPlayerResponse(data);

        Assert.Equal(202, player.PrimaryDestinyMembership?.MembershipId);
        Assert.Equal(3, player.PrimaryDestinyMembership?.MembershipType);
    }

    private static UserMembershipData UserMemberships(long? primaryMembershipId) =>
        new()
        {
            PrimaryMembershipId = primaryMembershipId,
            DestinyMemberships =
            [
                Membership(type: 2, id: 101),
                Membership(type: 3, id: 202)
            ]
        };

    private static GroupUserInfoCard Membership(int type, long id) =>
        new()
        {
            MembershipType = type,
            MembershipId = id,
            DisplayName = $"Profile {id}",
            BungieGlobalDisplayName = "Guardian",
            ApplicableMembershipTypes = [type],
            IsPublic = true
        };

    private static SignedInPlayerResponse ToSignedInPlayerResponse(UserMembershipData data)
    {
        var method = typeof(BungieAuthService).GetMethod(
            "ToSignedInPlayerResponse",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return Assert.IsType<SignedInPlayerResponse>(method.Invoke(null, [data]));
    }
}
