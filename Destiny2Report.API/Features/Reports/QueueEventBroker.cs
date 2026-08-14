using System.Collections.Concurrent;
using System.Threading.Channels;
using Destiny2Report.API.Features.Crawler;
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
    private readonly bool _ownsRedis;
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _pump;

    public QueueEventBroker(IConnectionMultiplexer redis, bool ownsRedis = false)
    {
        _redis = redis;
        _ownsRedis = ownsRedis;
    }

    public QueueEventSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;

        lock (_gate)
        {
            _pump ??= PumpAsync();
        }

        return new QueueEventSubscription(channel.Reader, () => Remove(id, channel));
    }

    private void Remove(Guid id, Channel<string> channel)
    {
        if (_subscribers.TryRemove(new KeyValuePair<Guid, Channel<string>>(id, channel)))
        {
            channel.Writer.TryComplete();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            var queue = await _redis
                .GetSubscriber()
                .SubscribeAsync(RedisChannel.Literal(CrawlerQueue.EventsChannelName))
                .ConfigureAwait(false);

            while (!_shutdown.IsCancellationRequested)
            {
                var message = await queue.ReadAsync(_shutdown.Token).ConfigureAwait(false);
                var payload = message.Message.ToString();
                foreach (var subscriber in _subscribers.Values)
                {
                    subscriber.Writer.TryWrite(payload);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch
        {
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Writer.TryComplete(new InvalidOperationException("Queue event subscription stopped."));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryComplete();
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

    internal QueueEventSubscription(ChannelReader<string> reader, Action dispose)
    {
        Reader = reader;
        _dispose = dispose;
    }

    public ChannelReader<string> Reader { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dispose();
        }
    }
}
