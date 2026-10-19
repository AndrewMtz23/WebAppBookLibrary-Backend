namespace WebAppBookLibrary.Services.Reviews;

public sealed class ReviewRetentionWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReviewRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ReviewRetention>().PurgeExpiredAsync(clock.GetUtcNow().UtcDateTime, 100, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Review retention batch failed; it will retry on the next interval."); }
        }
    }
}
