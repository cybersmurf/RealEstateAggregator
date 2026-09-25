namespace RealEstate.Api.Services;

/// <summary>
/// Průběžně generuje AI shrnutí pro inzeráty, které ho ještě nemají.
/// Shrnutí nahrazuje veřejně původní popis (autorská práva zdrojů), takže musí vznikat
/// automaticky bez ručního spouštění z admin UI.
///
/// Konfigurace: <c>AiSummary:Enabled</c> (default true), <c>AiSummary:IdleMinutes</c> (default 15).
/// </summary>
public sealed class AiSummaryHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AiSummaryHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(5);
    private const int BatchSize = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("AiSummary:Enabled", true))
        {
            logger.LogInformation("AiSummaryHostedService je vypnutý (AiSummary:Enabled=false).");
            return;
        }

        var idleMinutes = Math.Max(1, configuration.GetValue("AiSummary:IdleMinutes", 15));
        var idleDelay = TimeSpan.FromMinutes(idleMinutes);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        logger.LogInformation("AiSummaryHostedService spuštěn (batch {Batch}, idle {Idle} min).", BatchSize, idleMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = idleDelay;

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IOllamaTextService>();

                var result = await service.BulkSummaryAsync(BatchSize, stoppingToken, orderDesc: true);

                if (result.Processed > 0 && result.RemainingUnprocessed > 0)
                    delay = BusyDelay;

                if (result.Processed > 0)
                    logger.LogInformation(
                        "AI shrnutí: {Ok}/{Proc} OK, zbývá {Rem}.",
                        result.Succeeded, result.Processed, result.RemainingUnprocessed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Ollama může být dole – nespadnout, zkusit po idle intervalu
                logger.LogWarning(ex, "AI shrnutí: dávka selhala, další pokus za {Idle} min.", idleMinutes);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
