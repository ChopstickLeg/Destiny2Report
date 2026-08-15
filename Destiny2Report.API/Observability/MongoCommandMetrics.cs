using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace Destiny2Report.API.Observability;

/// <summary>Captures MongoDB command results without adding work to request paths.</summary>
public sealed class MongoCommandMetrics
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(1);
    private readonly Histogram<double> _duration = AppTelemetry.Meter.CreateHistogram<double>(
        "destiny2report.mongodb.command.duration",
        unit: "ms");
    private readonly Counter<long> _failures = AppTelemetry.Meter.CreateCounter<long>(
        "destiny2report.mongodb.command.failures",
        unit: "{command}");
    private readonly ConcurrentQueue<CommandSample> _recentSamples = new();
    private long _completedCommands;
    private long _failedCommands;

    public void Configure(MongoClientSettings settings)
    {
        settings.ClusterConfigurator = cluster =>
        {
            cluster.Subscribe<CommandSucceededEvent>(command => Record(command.CommandName, command.Duration, false));
            cluster.Subscribe<CommandFailedEvent>(command => Record(command.CommandName, command.Duration, true));
        };
    }

    public MongoCommandMetricsSnapshot GetSnapshot()
    {
        var cutoff = DateTimeOffset.UtcNow - RecentWindow;
        while (_recentSamples.TryPeek(out var sample) && sample.AtUtc < cutoff)
        {
            _recentSamples.TryDequeue(out _);
        }

        var recent = _recentSamples.ToArray();
        return new MongoCommandMetricsSnapshot(
            Interlocked.Read(ref _completedCommands),
            Interlocked.Read(ref _failedCommands),
            recent.Length,
            recent.Length == 0 ? null : recent.Average(sample => sample.DurationMilliseconds));
    }

    private void Record(string commandName, TimeSpan duration, bool failed)
    {
        var tags = new TagList { { "db.operation.name", commandName } };
        _duration.Record(duration.TotalMilliseconds, tags);
        _recentSamples.Enqueue(new CommandSample(DateTimeOffset.UtcNow, duration.TotalMilliseconds));
        Interlocked.Increment(ref _completedCommands);
        if (failed)
        {
            Interlocked.Increment(ref _failedCommands);
            _failures.Add(1, tags);
        }
    }

    private sealed record CommandSample(DateTimeOffset AtUtc, double DurationMilliseconds);
}

public sealed record MongoCommandMetricsSnapshot(
    long CompletedCommands,
    long FailedCommands,
    int RecentSampleCount,
    double? RecentAverageDurationMilliseconds);
