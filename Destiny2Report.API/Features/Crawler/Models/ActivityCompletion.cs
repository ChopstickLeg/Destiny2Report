namespace Destiny2Report.API.Features.Crawler.Models;

public record ActivityCompletion
{
    public string ActivityName { get; init; } = "";
    public DateTime CompletionDate { get; init; }
    public bool? IsContest { get; init; }
    public bool? IsDayOne { get; init; }
    public bool? IsFlawless { get; init; }
    public bool? IsSolo { get; init; }
    public long InstanceId { get; init; }
}
