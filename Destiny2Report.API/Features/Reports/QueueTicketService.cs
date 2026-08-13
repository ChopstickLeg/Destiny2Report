using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using StackExchange.Redis;

namespace Destiny2Report.API.Features.Reports;

public interface IQueueTicketService
{
    Task<string> IssueAsync(int membershipTypeId, long membershipId, CancellationToken cancellationToken);

    Task<bool> ValidateAsync(
        string? ticket,
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken);

    Task<bool> ConsumeAsync(
        string? ticket,
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken);
}

public sealed class QueueTicketService(
    IConnectionMultiplexer redis,
    TimeProvider timeProvider) : IQueueTicketService
{
    private const string SigningKeyName = "QueueTickets:SigningKey:v1";
    private const string NonceKeyPrefix = "QueueTickets:Nonce:v1:";
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim signingKeyLock = new(1, 1);
    private byte[]? signingKey;

    public async Task<string> IssueAsync(
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var key = await GetSigningKeyAsync(cancellationToken).ConfigureAwait(false);
        var expiresAt = timeProvider.GetUtcNow().Add(TicketLifetime);
        var nonce = RandomNumberGenerator.GetBytes(QueueTicketCodec.NonceLength);
        var ticket = QueueTicketCodec.Protect(key, membershipTypeId, membershipId, expiresAt, nonce);

        var stored = await redis.GetDatabase()
            .StringSetAsync(NonceKey(nonce), RedisValue.EmptyString, TicketLifetime, When.NotExists)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!stored)
        {
            throw new InvalidOperationException("Could not reserve the queue ticket nonce.");
        }

        return ticket;
    }

    public async Task<bool> ValidateAsync(
        string? ticket,
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var key = await GetSigningKeyAsync(cancellationToken).ConfigureAwait(false);
        return QueueTicketCodec.TryUnprotect(
            key,
            ticket,
            membershipTypeId,
            membershipId,
            timeProvider.GetUtcNow(),
            out _);
    }

    public async Task<bool> ConsumeAsync(
        string? ticket,
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var key = await GetSigningKeyAsync(cancellationToken).ConfigureAwait(false);
        if (!QueueTicketCodec.TryUnprotect(
                key,
                ticket,
                membershipTypeId,
                membershipId,
                timeProvider.GetUtcNow(),
                out var nonce))
        {
            return false;
        }

        var consumed = await redis.GetDatabase()
            .StringGetDeleteAsync(NonceKey(nonce))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return !consumed.IsNull;
    }

    private async Task<byte[]> GetSigningKeyAsync(CancellationToken cancellationToken)
    {
        if (signingKey is not null)
        {
            return signingKey;
        }

        await signingKeyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (signingKey is not null)
            {
                return signingKey;
            }

            var database = redis.GetDatabase();
            var value = await database.StringGetAsync(SigningKeyName)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (value.IsNull)
            {
                var generated = RandomNumberGenerator.GetBytes(32);
                await database.StringSetAsync(SigningKeyName, generated, when: When.NotExists)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                value = await database.StringGetAsync(SigningKeyName)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            signingKey = (byte[])value!;
            return signingKey;
        }
        finally
        {
            signingKeyLock.Release();
        }
    }

    private static RedisKey NonceKey(ReadOnlySpan<byte> nonce) =>
        $"{NonceKeyPrefix}{Convert.ToHexString(nonce)}";
}

internal static class QueueTicketCodec
{
    internal const int NonceLength = 16;
    private const byte Version = 1;
    private const int PayloadLength = 1 + sizeof(int) + sizeof(long) + sizeof(long) + NonceLength;
    private const int SignatureLength = 32;
    private const int TicketLength = PayloadLength + SignatureLength;

    internal static string Protect(
        ReadOnlySpan<byte> signingKey,
        int membershipTypeId,
        long membershipId,
        DateTimeOffset expiresAt,
        ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != NonceLength)
        {
            throw new ArgumentException($"Queue ticket nonces must be {NonceLength} bytes.", nameof(nonce));
        }

        Span<byte> ticket = stackalloc byte[TicketLength];
        var payload = ticket[..PayloadLength];
        payload[0] = Version;
        BinaryPrimitives.WriteInt32BigEndian(payload[1..], membershipTypeId);
        BinaryPrimitives.WriteInt64BigEndian(payload[5..], membershipId);
        BinaryPrimitives.WriteInt64BigEndian(payload[13..], expiresAt.ToUnixTimeSeconds());
        nonce.CopyTo(payload[21..]);
        HMACSHA256.HashData(signingKey, payload, ticket[PayloadLength..]);
        return WebEncoders.Base64UrlEncode(ticket);
    }

    internal static bool TryUnprotect(
        ReadOnlySpan<byte> signingKey,
        string? ticket,
        int membershipTypeId,
        long membershipId,
        DateTimeOffset now,
        out byte[] nonce)
    {
        nonce = [];
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = WebEncoders.Base64UrlDecode(ticket);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != TicketLength)
        {
            return false;
        }

        var payload = bytes.AsSpan(0, PayloadLength);
        var signature = bytes.AsSpan(PayloadLength, SignatureLength);
        Span<byte> expectedSignature = stackalloc byte[SignatureLength];
        HMACSHA256.HashData(signingKey, payload, expectedSignature);
        if (!CryptographicOperations.FixedTimeEquals(signature, expectedSignature)
            || payload[0] != Version
            || BinaryPrimitives.ReadInt32BigEndian(payload[1..]) != membershipTypeId
            || BinaryPrimitives.ReadInt64BigEndian(payload[5..]) != membershipId
            || BinaryPrimitives.ReadInt64BigEndian(payload[13..]) < now.ToUnixTimeSeconds())
        {
            return false;
        }

        nonce = payload.Slice(21, NonceLength).ToArray();
        return true;
    }
}
