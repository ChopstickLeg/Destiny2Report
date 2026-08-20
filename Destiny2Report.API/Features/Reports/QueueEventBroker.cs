using System.Collections.Concurrent;
using System.Threading.Channels;
using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Observability;
using StackExchange.Redis;

namespace Destiny2Report.API.Features.Reports;

/// <summary>
/// Maintains one Redis pub/sub subscription per API process and fans events out
/// to the active SSE requests. A subscription per browser request causes Redis
/// to deliver the entire fleet's event traffic repeatedly.
/// </summary>
public sealed class QueueEventBroker : IAsyncDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<QueueEventBroker> _logger;
    private readonly QueueStreamMetrics _metrics;
    private readonly bool _ownsRedis;
    private readonly ConcurrentDictionary<Guid, SubscriberChannel> _subscribers = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _pump;

    public QueueEventBroker(
        IConnectionMultiplexer redis,
        ILogger<QueueEventBroker> logger,
        QueueStreamMetrics metrics,
        bool ownsRedis = false)
    {
        _redis = redis;
        _logger = logger;
        _metrics = metrics;
        _ownsRedis = ownsRedis;
    }

    public QueueEventSubscription Subscribe()
    {
        var channel = new SubscriberChannel(_metrics);
        var id = Guid.NewGuid();
        _subscribers[id] = channel;

        lock (_gate)
        {
            _pump ??= PumpAsync();
        }

        return new QueueEventSubscription(channel, () => Remove(id, channel));
    }

    public int SubscriberCount => _subscribers.Count;

    private void Remove(Guid id, SubscriberChannel channel)
    {
        if (_subscribers.TryRemove(new KeyValuePair<Guid, SubscriberChannel>(id, channel)))
        {
            channel.Complete();
        }
    }

    private async Task PumpAsync()
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var queue = await _redis
                    .GetSubscriber()
                    .SubscribeAsync(RedisChannel.Literal(CrawlerQueue.EventsChannelName))
                    .ConfigureAwait(false);

                retryDelay = TimeSpan.FromSeconds(1);
                while (!_shutdown.IsCancellationRequested)
                {
                    var message = await queue.ReadAsync(_shutdown.Token).ConfigureAwait(false);
                    var payload = message.Message.ToString();
                    foreach (var subscriber in _subscribers.Values)
                    {
                        subscriber.Write(payload);
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Queue event Redis subscription failed; retrying in {RetryDelay}.",
                    retryDelay);

                try
                {
                    await Task.Delay(retryDelay, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }

                retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Complete();
        }

        var pump = _pump;
        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
        if (_ownsRedis)
        {
            await _redis.CloseAsync().ConfigureAwait(false);
            _redis.Dispose();
        }
    }
}

public sealed class QueueEventSubscription : IDisposable
{
    private readonly Action _dispose;
    private int _disposed;

    private readonly SubscriberChannel _channel;

    internal QueueEventSubscription(SubscriberChannel channel, Action dispose)
    {
        _channel = channel;
        _dispose = dispose;
    }

    public ValueTask<string> ReadAsync(CancellationToken cancellationToken) => _channel.ReadAsync(cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dispose();
        }
    }
}

internal sealed class SubscriberChannel
{
    private const int Capacity = 256;
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(Capacity)
    {
        SingleWriter = true,
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly QueueStreamMetrics _metrics;

    public SubscriberChannel(QueueStreamMetrics metrics) => _metrics = metrics;

    public void Write(string payload)
    {
        if (_channel.Writer.TryWrite(payload))
        {
            return;
        }

        // Retain fresh status over stale progress and expose the overload to telemetry.
        if (_channel.Reader.TryRead(out _))
        {
            _metrics.RecordDroppedBrokerMessage();
        }

        if (!_channel.Writer.TryWrite(payload))
        {
            _metrics.RecordDroppedBrokerMessage();
        }
    }

    public ValueTask<string> ReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    public void Complete() => _channel.Writer.TryComplete();
}
