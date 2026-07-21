using Destiny2Report.Tests.TestSupport;

namespace Destiny2Report.Tests.Features.Crawler;

public sealed class CrawlerTriumphTests
{
    [Fact]
    public void FirstNonBlank_prefers_completion_record_display_value()
    {
        var result = (string)CrawlerReflection.Invoke(
            "FirstNonBlank",
            "Completion record description",
            "Presentation node description")!;

        Assert.Equal("Completion record description", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FirstNonBlank_falls_back_to_presentation_node_display_value(string? completionRecordValue)
    {
        var result = (string)CrawlerReflection.Invoke(
            "FirstNonBlank",
            completionRecordValue,
            "Presentation node description")!;

        Assert.Equal("Presentation node description", result);
    }
}
