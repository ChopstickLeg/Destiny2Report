using System.Reflection;
using D2Report.BungieClient;
using Destiny2Report.API.Features.PlayerSearch;

namespace Destiny2Report.Tests.Features.PlayerSearch;

public sealed class PlayerSearchHandlersBusinessLogicTests
{
    [Fact]
    public void SelectMemberships_returns_every_public_membership_when_cross_save_is_disabled()
    {
        UserInfoCard[] memberships =
        [
            Membership(type: 2, id: 101, crossSaveOverride: 0),
            Membership(type: 3, id: 202, crossSaveOverride: 0)
        ];

        var selected = SelectMemberships(memberships);

        Assert.Equal([101L, 202L], selected.Select(membership => membership.MembershipId));
    }

    [Fact]
    public void SelectMemberships_returns_only_the_cross_save_primary()
    {
        UserInfoCard[] memberships =
        [
            Membership(type: 2, id: 101, crossSaveOverride: 3),
            Membership(type: 3, id: 202, crossSaveOverride: 3)
        ];

        var selected = SelectMemberships(memberships);

        var primary = Assert.Single(selected);
        Assert.Equal(202, primary.MembershipId);
        Assert.Equal(3, primary.MembershipType);
    }

    [Fact]
    public void SelectMemberships_excludes_private_and_invalid_memberships()
    {
        UserInfoCard[] memberships =
        [
            Membership(type: 2, id: 101, crossSaveOverride: 0, isPublic: false),
            Membership(type: 0, id: 202, crossSaveOverride: 0),
            Membership(type: 3, id: 0, crossSaveOverride: 0),
            Membership(type: 3, id: 303, crossSaveOverride: 0)
        ];

        var selected = SelectMemberships(memberships);

        Assert.Equal(303, Assert.Single(selected).MembershipId);
    }

    private static UserInfoCard Membership(int type, long id, int crossSaveOverride, bool isPublic = true) =>
        new()
        {
            MembershipType = type,
            MembershipId = id,
            CrossSaveOverride = crossSaveOverride,
            IsPublic = isPublic
        };

    private static IReadOnlyList<UserInfoCard> SelectMemberships(IEnumerable<UserInfoCard>? memberships)
    {
        var method = typeof(PlayerSearchHandlers).GetMethod(
            "SelectMemberships",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IReadOnlyList<UserInfoCard>>(method.Invoke(null, [memberships]));
    }
}
