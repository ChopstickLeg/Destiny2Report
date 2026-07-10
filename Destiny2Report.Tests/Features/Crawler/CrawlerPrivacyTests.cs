using System.Reflection;
using D2Report.BungieClient;
using Destiny2Report.Tests.TestSupport;

namespace Destiny2Report.Tests.Features.Crawler;

public sealed class CrawlerPrivacyTests
{
    [Fact]
    public void EnsurePublicActivityHistoryResponse_throws_activity_history_error_for_privacy_response()
    {
        var response = new Response62
        {
            ErrorCode = 1665,
            ErrorStatus = "DestinyPrivacyRestriction",
            Message = "The user's activity history is private.",
            Response = new DestinyActivityHistoryResults()
        };

        var exception = Assert.Throws<TargetInvocationException>(() =>
            CrawlerReflection.Invoke("EnsurePublicActivityHistoryResponse", response, "GetActivityHistory:1:0"));
        var inner = Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerException);

        Assert.Contains("GetActivityHistory:1:0", inner.Message);
        Assert.Contains("activity history is not public", inner.Message);
    }

    [Fact]
    public void EnsurePublicActivityHistoryResponse_allows_success_response()
    {
        var response = new Response62
        {
            ErrorCode = 1,
            ErrorStatus = "Success",
            Message = "Ok",
            Response = new DestinyActivityHistoryResults()
        };

        CrawlerReflection.Invoke("EnsurePublicActivityHistoryResponse", response, "GetActivityHistory:1:0");
    }

    [Fact]
    public void IsPrivateProfileException_recognizes_activity_history_api_privacy_error()
    {
        var exception = new ApiException(
            "GetActivityHistory failed",
            500,
            """{"ErrorCode":1665,"ErrorStatus":"DestinyPrivacyRestriction","Message":"The user's activity history is private."}""",
            new Dictionary<string, IEnumerable<string>>(),
            new InvalidOperationException());

        var result = (bool)CrawlerReflection.Invoke("IsPrivateProfileException", exception)!;

        Assert.True(result);
    }
}
