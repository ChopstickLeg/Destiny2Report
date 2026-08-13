using System.Security.Cryptography;
using Destiny2Report.API.Features.Reports;

namespace Destiny2Report.Tests.Features.Reports;

public sealed class QueueTicketCodecTests
{
    private static readonly byte[] SigningKey = SHA256.HashData("queue-ticket-test-key"u8);
    private static readonly byte[] Nonce = Enumerable.Range(0, QueueTicketCodec.NonceLength)
        .Select(value => (byte)value)
        .ToArray();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-13T04:00:00Z");

    [Fact]
    public void Ticket_is_bound_to_membership_and_preserves_nonce()
    {
        var ticket = QueueTicketCodec.Protect(SigningKey, 3, 4611686018463095984L, Now.AddMinutes(10), Nonce);

        var valid = QueueTicketCodec.TryUnprotect(
            SigningKey,
            ticket,
            3,
            4611686018463095984L,
            Now,
            out var nonce,
            out var expiresAt);

        Assert.True(valid);
        Assert.Equal(Nonce, nonce);
        Assert.Equal(Now.AddMinutes(10), expiresAt);
        Assert.False(QueueTicketCodec.TryUnprotect(SigningKey, ticket, 2, 4611686018463095984L, Now, out _, out _));
        Assert.False(QueueTicketCodec.TryUnprotect(SigningKey, ticket, 3, 99, Now, out _, out _));
    }

    [Fact]
    public void Ticket_rejects_expiry_and_tampering()
    {
        var ticket = QueueTicketCodec.Protect(SigningKey, 3, 42, Now.AddMinutes(1), Nonce);
        var tampered = ticket[..^1] + (ticket[^1] == 'A' ? "B" : "A");

        Assert.False(QueueTicketCodec.TryUnprotect(SigningKey, ticket, 3, 42, Now.AddMinutes(1), out _, out _));
        Assert.False(QueueTicketCodec.TryUnprotect(SigningKey, tampered, 3, 42, Now, out _, out _));
        Assert.False(QueueTicketCodec.TryUnprotect(SigningKey, "not-a-ticket", 3, 42, Now, out _, out _));
    }
}
