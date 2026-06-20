using System.Reflection;
using Destiny2Report.API.Features.Crawler;

namespace Destiny2Report.Tests.Features.Crawler;

public sealed class ContestModeLookupTests
{
    private static readonly Type LookupType = typeof(ContestModeOptions).Assembly
        .GetType("Destiny2Report.API.Features.Crawler.ContestModeLookup")
        ?? throw new InvalidOperationException("ContestModeLookup type was not found.");

    [Theory]
    [InlineData("King's Fall: Master", "King's Fall")]
    [InlineData("Vow of the Disciple: Contest: Guided Games", "Vow of the Disciple")]
    [InlineData("  Crota's End: Normal  ", "Crota's End")]
    [InlineData("The Shattered Throne", "The Shattered Throne")]
    public void NormalizeActivityName_removes_known_suffixes_repeatedly(string input, string expected)
    {
        var normalize = LookupType.GetMethod("NormalizeActivityName", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("NormalizeActivityName was not found.");

        Assert.Equal(expected, normalize.Invoke(null, [input]));
    }

    [Fact]
    public void FromOptions_registers_signed_and_unsigned_hash_aliases()
    {
        const long unsignedHash = 4_000_000_000;
        var fromOptions = LookupType.GetMethod("FromOptions", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("FromOptions was not found.");

        var lookup = fromOptions.Invoke(null, [new ContestModeOptions
        {
            Raids =
            [
                new ContestModeActivityWindow
                {
                    ActivityId = unsignedHash,
                    Start = DateTimeOffset.Parse("2024-06-07T17:00:00Z"),
                    End = DateTimeOffset.Parse("2024-06-09T17:00:00Z")
                }
            ]
        }]);

        var raids = (System.Collections.IEnumerable)(LookupType.GetProperty("Raids")!.GetValue(lookup)!);
        var keys = raids.Cast<object>()
            .Select(item => (long)item.GetType().GetProperty("Key")!.GetValue(item)!)
            .ToArray();

        Assert.Contains(unsignedHash, keys);
        Assert.Contains((long)unchecked((int)(uint)unsignedHash), keys);
    }
}
