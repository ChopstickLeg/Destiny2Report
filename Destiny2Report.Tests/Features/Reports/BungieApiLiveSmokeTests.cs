namespace Destiny2Report.Tests.Features.Reports;

public sealed class BungieApiLiveSmokeTests
{
    [Fact(Skip = "Live Bungie API smoke test placeholder. Enable manually after adding API credentials and deciding which real network checks should run outside normal CI.")]
    public void Real_membership_available_for_manual_integration_test_design()
    {
        const int membershipTypeId = 1;
        const long membershipId = 4611686018463095984;

        Assert.Equal(1, membershipTypeId);
        Assert.Equal(4611686018463095984, membershipId);
    }
}
