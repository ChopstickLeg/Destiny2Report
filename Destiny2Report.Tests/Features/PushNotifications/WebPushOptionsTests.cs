using Destiny2Report.API.Features.PushNotifications;

namespace Destiny2Report.Tests.Features.PushNotifications;

public sealed class WebPushOptionsTests
{
    [Fact]
    public void Enabled_requires_all_VAPID_settings()
    {
        Assert.False(new WebPushOptions().Enabled);
        Assert.False(new WebPushOptions
        {
            Subject = "mailto:admin@example.com",
            PublicKey = "public"
        }.Enabled);
        Assert.True(new WebPushOptions
        {
            Subject = "mailto:admin@example.com",
            PublicKey = "public",
            PrivateKey = "private"
        }.Enabled);
    }
}
