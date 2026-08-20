using System.Diagnostics.Metrics;

namespace Destiny2Report.API.Observability;

/// <summary>Process-local pressure indicators for public queue SSE streams.</summary>
public sealed class QueueStreamMetrics
{
    private readonly UpDownCounter<long> _activeStreams = AppTelemetry.Meter.CreateUpDownCounter<long>(
        "destiny2report.queue.sse.active_streams",
        unit: "{stream}");
    private readonly Counter<long> _droppedBrokerMessages = AppTelemetry.Meter.CreateCounter<long>(
        "destiny2report.queue.sse.broker_dropped_messages",
        unit: "{message}");
    private long _activeStreamCount;
    private long _droppedBrokerMessageCount;

    public IDisposable TrackStream()
    {
        Interlocked.Increment(ref _activeStreamCount);
        _activeStreams.Add(1);
        return new StreamLease(this);
    }

    public void RecordDroppedBrokerMessage()
    {
        Interlocked.Increment(ref _droppedBrokerMessageCount);
        _droppedBrokerMessages.Add(1);
    }

    public QueueStreamMetricsSnapshot GetSnapshot(int brokerSubscribers) => new(
        Math.Max(0, Interlocked.Read(ref _activeStreamCount)),
        brokerSubscribers,
        Interlocked.Read(ref _droppedBrokerMessageCount));

    private void ReleaseStream()
    {
        Interlocked.Decrement(ref _activeStreamCount);
        _activeStreams.Add(-1);
    }

    private sealed class StreamLease(QueueStreamMetrics owner) : IDisposable
    {
        private QueueStreamMetrics? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseStream();
    }
}

public sealed record QueueStreamMetricsSnapshot(
    long ActiveStreams,
    int BrokerSubscribers,
    long DroppedBrokerMessages);
