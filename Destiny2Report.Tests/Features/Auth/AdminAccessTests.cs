using Destiny2Report.API.Features.Admin;
using Destiny2Report.API.Features.Auth;

namespace Destiny2Report.Tests.Features.Auth;

public sealed class AdminAccessTests
{
    private static readonly AdminOptions Options = new()
    {
        MembershipTypeId = 3,
        MembershipId = 4611686018463095984L
    };

    [Fact]
    public void IsAdmin_requires_the_configured_membership_type_and_id()
    {
        var player = Player(
            new DestinyMembershipResponse(3, 4611686018463095984L, null, null, null, null, 0, [], true));

        Assert.True(AdminAccess.IsAdmin(player, Options));
        Assert.False(AdminAccess.IsAdmin(Player(
            new DestinyMembershipResponse(2, 4611686018463095984L, null, null, null, null, 0, [], true)), Options));
        Assert.False(AdminAccess.IsAdmin(Player(
            new DestinyMembershipResponse(3, 4611686018463095985L, null, null, null, null, 0, [], true)), Options));
    }

    [Fact]
    public void IsAdmin_is_disabled_when_configuration_is_incomplete()
    {
        var player = Player(
            new DestinyMembershipResponse(3, 4611686018463095984L, null, null, null, null, 0, [], true));

        Assert.False(AdminAccess.IsAdmin(player, new AdminOptions()));
    }

    [Fact]
    public void WithAdminAccess_sets_the_response_flag()
    {
        var player = Player(
            new DestinyMembershipResponse(3, 4611686018463095984L, null, null, null, null, 0, [], true));

        Assert.True(AdminAccess.WithAdminAccess(player, Options).IsAdmin);
    }

    private static SignedInPlayerResponse Player(params DestinyMembershipResponse[] memberships) =>
        new(true, null, memberships, memberships.FirstOrDefault());
}
