using Destiny2Report.API.Features.Auth;

namespace Destiny2Report.API.Features.Admin;

public static class AdminAccess
{
    public static bool IsAdmin(SignedInPlayerResponse player, AdminOptions options)
    {
        return player.SignedIn
            && options.MembershipTypeId > 0
            && options.MembershipId > 0
            && player.DestinyMemberships.Any(membership =>
                membership.MembershipType == options.MembershipTypeId
                && membership.MembershipId == options.MembershipId);
    }

    public static SignedInPlayerResponse WithAdminAccess(
        SignedInPlayerResponse player,
        AdminOptions options) => player with { IsAdmin = IsAdmin(player, options) };
}
