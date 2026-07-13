using System.Reflection;
using Destiny2Report.API.Features.Crawler;

namespace Destiny2Report.Tests.Features.Crawler;

public sealed class ConquestLookupTests
{
    private static readonly Type LookupType = typeof(ConquestOptions).Assembly
        .GetType("Destiny2Report.API.Features.Crawler.ConquestLookup")
        ?? throw new InvalidOperationException("ConquestLookup type was not found.");

    [Theory]
    [InlineData("2025-12-02T16:59:59Z", "Edge of Fate Conquest")]
    [InlineData("2025-12-02T17:00:00Z", "Renegades Conquest")]
    [InlineData("2026-01-01T00:00:00Z", "Renegades Conquest")]
    public void GetName_selects_name_by_completion_time(string completedAt, string expected)
    {
        var lookup = CreateLookup(123);

        Assert.Equal(expected, GetName(lookup, 123, 0, DateTimeOffset.Parse(completedAt)));
    }

    [Fact]
    public void GetName_matches_director_hash_and_signed_hash_alias()
    {
        const long unsignedHash = 4_000_000_000;
        var lookup = CreateLookup(unsignedHash);
        var signedHash = unchecked((int)(uint)unsignedHash);

        Assert.Equal(
            "Edge of Fate Conquest",
            GetName(lookup, 0, signedHash, DateTimeOffset.Parse("2025-11-01T00:00:00Z")));
    }

    private static object CreateLookup(long activityId)
    {
        var fromOptions = LookupType.GetMethod("FromOptions", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("FromOptions was not found.");

        return fromOptions.Invoke(null, [new ConquestOptions
        {
            Activities =
            [
                new ConquestActivityOptions
                {
                    ActivityId = activityId,
                    EdgeOfFateName = "Edge of Fate Conquest",
                    RenegadesName = "Renegades Conquest"
                }
            ]
        }])!;
    }

    private static string? GetName(object lookup, long referenceId, long directorHash, DateTimeOffset completedAt)
    {
        var getName = LookupType.GetMethod("GetName", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("GetName was not found.");

        return (string?)getName.Invoke(lookup, [referenceId, directorHash, completedAt]);
    }
}
