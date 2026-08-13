using System.Net;
using Destiny2Report.API.Features.Reports;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Destiny2Report.Tests.Features.Reports;

public sealed class TurnstileVerifierTests
{
    [Fact]
    public async Task Accepts_expected_action_and_hostname_and_forwards_client_ip()
    {
        string? requestBody = null;
        var verifier = CreateVerifier(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {
                  "success": true,
                  "hostname": "destiny-2.report",
                  "action": "queue_report",
                  "error-codes": []
                }
                """);
        });

        var valid = await verifier.VerifyAsync("valid-token", "203.0.113.42", CancellationToken.None);

        Assert.True(valid);
        Assert.Contains("response=valid-token", requestBody);
        Assert.Contains("remoteip=203.0.113.42", requestBody);
    }

    [Theory]
    [InlineData(false, "queue_report", "destiny-2.report")]
    [InlineData(true, "different_action", "destiny-2.report")]
    [InlineData(true, "queue_report", "attacker.example")]
    public async Task Rejects_failed_or_mismatched_verification(
        bool success,
        string action,
        string hostname)
    {
        var verifier = CreateVerifier(_ => Task.FromResult(JsonResponse($$"""
            {
              "success": {{success.ToString().ToLowerInvariant()}},
              "hostname": "{{hostname}}",
              "action": "{{action}}",
              "error-codes": ["invalid-input-response"]
            }
            """)));

        Assert.False(await verifier.VerifyAsync("token", null, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_missing_token_without_calling_siteverify(string? token)
    {
        var called = false;
        var verifier = CreateVerifier(_ =>
        {
            called = true;
            return Task.FromResult(JsonResponse("{}"));
        });

        Assert.False(await verifier.VerifyAsync(token, null, CancellationToken.None));
        Assert.False(called);
    }

    private static TurnstileVerifier CreateVerifier(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new DelegateHandler(handler));
        var options = Options.Create(new TurnstileOptions
        {
            SecretKey = "test-secret",
            AllowedHostnames = ["destiny-2.report"]
        });
        return new TurnstileVerifier(
            httpClient,
            options,
            NullLogger<TurnstileVerifier>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
