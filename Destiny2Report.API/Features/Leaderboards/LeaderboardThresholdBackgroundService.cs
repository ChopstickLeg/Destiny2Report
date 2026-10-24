namespace Destiny2Report.API.Features.Leaderboards;

public sealed class LeaderboardThresholdBackgroundService(
    ILeaderboardService leaderboards,
    ILogger<LeaderboardThresholdBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await leaderboards.RefreshPercentileThresholdsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not refresh leaderboard percentile thresholds; the previous MongoDB cache snapshot will remain active until it expires.");
            }

            try { await Task.Delay(RefreshInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
