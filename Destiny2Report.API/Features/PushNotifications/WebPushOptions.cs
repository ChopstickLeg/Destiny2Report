namespace Destiny2Report.API.Features.PushNotifications;

public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    public string Subject { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";

    public bool Enabled => !string.IsNullOrWhiteSpace(Subject)
        && !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(PrivateKey);
}
