namespace RealEstate.Api.Services.SavedSearches;

/// <summary>
/// Periodické vyhodnocení uložených hledání – pojistka pro případ, že scraper nezavolá
/// POST /api/saved-searches/run. Start 2 minuty po spuštění, interval SavedSearches:IntervalMinutes (výchozí 60).
/// </summary>
public sealed class SavedSearchHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SavedSearchHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = Math.Max(1, configuration.GetValue<int?>("SavedSearches:IntervalMinutes") ?? 60);
        var interval = TimeSpan.FromMinutes(minutes);
        logger.LogInformation("Uložená hledání: periodické vyhodnocení každých {Minutes} min, první běh za {Delay} min",
            minutes, StartupDelay.TotalMinutes);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            using var timer = new PeriodicTimer(interval);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Zastavení aplikace
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var notifier = scope.ServiceProvider.GetRequiredService<ISavedSearchNotifier>();
            var result = await notifier.RunAllAsync(ct);
            logger.LogDebug("Uložená hledání (timer): {@Result}", result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Periodické vyhodnocení uložených hledání selhalo");
        }
    }
}
