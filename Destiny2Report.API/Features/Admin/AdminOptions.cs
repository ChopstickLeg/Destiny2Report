namespace Destiny2Report.API.Features.Admin;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    public int MembershipTypeId { get; set; }
    public long MembershipId { get; set; }
}
