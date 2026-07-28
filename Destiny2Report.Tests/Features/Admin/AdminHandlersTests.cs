using Destiny2Report.API.Features.Admin;
using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Reports;
using Microsoft.AspNetCore.Http.HttpResults;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Destiny2Report.Tests.Features.Admin;

public sealed class AdminHandlersTests
{
    [Fact]
    public async Task Queue_crawls_validates_the_entire_batch_before_enqueueing()
    {
        var queue = new RecordingCrawlerJobQueue();

        var result = await AdminHandlers.QueueCrawls(
            [
                new AdminCrawlerQueueItem(3, 42),
                new AdminCrawlerQueueItem(0, 99)
            ],
            queue,
            CancellationToken.None);

        Assert.IsType<BadRequest<Microsoft.AspNetCore.Mvc.ProblemDetails>>(result);
        Assert.Empty(queue.Requests);
    }

    [Fact]
    public void Mongo_flush_targets_the_idle_scheduler_source_queue()
    {
        var filter = AdminHandlers.BuildMongoQueuedReportFilter();
        var rendered = filter.Render(new RenderArgs<DestinyReport>(
            BsonSerializer.LookupSerializer<DestinyReport>(),
            BsonSerializer.SerializerRegistry));

        Assert.Equal(DestinyReport.CrawlStateQueued, rendered["CrawlState"].AsString);
        Assert.False(rendered["QueuedInRedis"].AsBoolean);
    }

    [Fact]
    public void Redis_flush_targets_legacy_reports_dispatched_to_redis()
    {
        var filter = AdminHandlers.BuildRedisQueuedReportFilter();
        var rendered = filter.Render(new RenderArgs<DestinyReport>(
            BsonSerializer.LookupSerializer<DestinyReport>(),
            BsonSerializer.SerializerRegistry));

        Assert.Equal(DestinyReport.CrawlStateQueued, rendered["CrawlState"].AsString);
        Assert.True(rendered["QueuedInRedis"].AsBoolean);
    }

    [Fact]
    public void Queue_flushed_reports_become_terminal_and_unleased()
    {
        var update = AdminHandlers.BuildQueueFlushedReportUpdate();
        var rendered = update.Render(new RenderArgs<DestinyReport>(
            BsonSerializer.LookupSerializer<DestinyReport>(),
            BsonSerializer.SerializerRegistry));
        var set = rendered["$set"].AsBsonDocument;

        Assert.Equal(DestinyReport.CrawlStateFailed, set["CrawlState"].AsString);
        Assert.False(set["QueuedInRedis"].AsBoolean);
        Assert.True(set["LeaseExpiresAtUtc"].IsBsonNull);
        Assert.Equal("", set["LeaseOwner"].AsString);
    }

    private sealed class RecordingCrawlerJobQueue : ICrawlerJobQueue
    {
        public List<(int MembershipTypeId, long MembershipId, bool ForceFullCrawl)> Requests { get; } = [];

        public Task<ReportQueueResponse> EnqueueAsync(
            int membershipTypeId,
            long membershipId,
            bool forceFullCrawl,
            CancellationToken cancellationToken)
        {
            Requests.Add((membershipTypeId, membershipId, forceFullCrawl));
            return Task.FromResult(new ReportQueueResponse(
                Guid.NewGuid().ToString("N"),
                membershipTypeId,
                membershipId,
                CrawlJob.StateQueued,
                DateTimeOffset.UtcNow));
        }
    }
}
