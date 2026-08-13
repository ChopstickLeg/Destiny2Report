using Destiny2Report.API.Features.Reports;
using Destiny2Report.API.Features.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Destiny2Report.Tests.Features.Reports;

public sealed class QueueAdmissionOptionsTests
{
    [Fact]
    public void IsBlocked_MatchesConfiguredBungieMembershipId()
    {
        var options = new QueueAdmissionOptions
        {
            BlockedBungieMembershipIds = " 123,456789 "
        };

        Assert.True(options.IsBlocked(123));
        Assert.True(options.IsBlocked(456789));
        Assert.False(options.IsBlocked(456));
        Assert.True(options.HasValidBlockedBungieMembershipIds());
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("123,-1")]
    [InlineData("0")]
    public void HasValidBlockedBungieMembershipIds_RejectsInvalidConfiguration(string value)
    {
        var options = new QueueAdmissionOptions { BlockedBungieMembershipIds = value };

        Assert.False(options.HasValidBlockedBungieMembershipIds());
    }

    [Fact]
    public async Task ResolveIdentityAsync_RejectsConfiguredBungieAccount()
    {
        const long blockedMembershipId = 123456789;
        var sessionStore = new StubSessionStore(new AuthSession(
            "access-token",
            "refresh-token",
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow.AddDays(1)));
        var service = new QueueAdmissionService(
            Options.Create(new QueueAdmissionOptions
            {
                Enabled = true,
                BlockedBungieMembershipIds = blockedMembershipId.ToString()
            }),
            sessionStore,
            new StubBungieAuthService(blockedMembershipId),
            new StubQuotaStore(),
            TimeProvider.System,
            NullLogger<QueueAdmissionService>.Instance);
        var context = new DefaultHttpContext();

        var result = await service.ResolveIdentityAsync(
            context.Request,
            context.Response,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(blockedMembershipId, result.BungieMembershipId);
        Assert.Equal(QueueAdmissionFailure.AccountBlocked, result.Failure);
    }

    private sealed class StubSessionStore(AuthSession? session) : IAuthSessionStore
    {
        public Task<AuthSession?> GetAsync(HttpRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(session);

        public Task CreateAsync(
            HttpResponse response,
            BungieOAuthTokenResponse tokens,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdateAsync(
            HttpRequest request,
            AuthSession updatedSession,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(
            HttpRequest request,
            HttpResponse response,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubBungieAuthService(long membershipId) : IBungieAuthService
    {
        public Task<BungieOAuthTokenResponse> ExchangeCodeAsync(
            BungieOAuthCodeRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BungieOAuthTokenResponse> RefreshTokenAsync(
            string refreshToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SignedInPlayerResponse> GetCurrentUserAsync(
            string accessToken,
            CancellationToken cancellationToken) => Task.FromResult(new SignedInPlayerResponse(
                true,
                new BungieNetUserResponse(membershipId, null, null, null, null, null),
                [],
                null));
    }

    private sealed class StubQuotaStore : IQueueAdmissionQuotaStore
    {
        public Task<QueueAdmissionDecision> ReserveAsync(
            long bungieMembershipId,
            bool isNewReport,
            CancellationToken cancellationToken) => Task.FromResult(new QueueAdmissionDecision());

        public Task CompleteAsync(
            QueueAdmissionReservation reservation,
            bool keepCharge,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
