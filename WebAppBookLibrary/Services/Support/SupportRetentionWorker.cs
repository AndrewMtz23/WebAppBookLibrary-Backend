namespace WebAppBookLibrary.Services.Support;

public sealed class SupportRetentionWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SupportRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SupportRetention>().PurgeExpiredAsync(clock.GetUtcNow().UtcDateTime, 100, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Support retention batch failed; it will retry on the next interval."); }
        }
    }
}
