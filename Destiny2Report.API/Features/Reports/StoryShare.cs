using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Reports;

[BsonIgnoreExtraElements]
public sealed record StoryShare
{
    [BsonId]
    public ObjectId Id { get; init; }

    public string TokenHash { get; init; } = "";

    public int MembershipTypeId { get; init; }

    public long MembershipId { get; init; }

    public DateTime CreatedAtUtc { get; init; }
}

public sealed record CreateStoryShareRequest(int MembershipTypeId, long MembershipId);

public sealed record CreateStoryShareResponse(string Token);

public sealed record StoryShareIdentityResponse(int MembershipTypeId, long MembershipId);
