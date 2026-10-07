namespace RealEstate.Api.Services;

/// <summary>
/// Průběžně dopočítává AI obohacení inzerátů, které ho ještě nemají – v tomto pořadí:
/// shrnutí (veřejně nahrazuje původní popis, proto první), štítky, normalizovaná data,
/// cenový signál a embedding pro sémantické hledání. Do 8. 10. 2026 běželo automaticky jen
/// shrnutí; štítky, normalizace a cenový signál se spouštěly ručně z admin UI a zaostávaly
/// o 5 000–10 000 inzerátů, embedding popisu neplnil nikdo.
///
/// Konfigurace: <c>AiSummary:Enabled</c> (default true), <c>AiSummary:IdleMinutes</c> (default 15).
/// </summary>
public sealed class AiEnrichmentHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AiEnrichmentHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(5);

    /// <summary>Krok obohacení: název do logu, velikost dávky a volání služby.</summary>
    private sealed record Step(string Name, int BatchSize,
        Func<IOllamaTextService, int, CancellationToken, Task<OllamaTextBatchResultDto>> Run);

    private static readonly Step[] Steps =
    [
        new("shrnutí", 10, (s, b, ct) => s.BulkSummaryAsync(b, ct, orderDesc: true)),
        new("štítky", 10, (s, b, ct) => s.BulkSmartTagsAsync(b, ct, orderDesc: true)),
        new("normalizace", 10, (s, b, ct) => s.BulkNormalizeAsync(b, ct, orderDesc: true)),
        new("cenový signál", 10, (s, b, ct) => s.BulkPriceOpinionAsync(b, ct, orderDesc: true)),
        new("embeddingy", 50, (s, b, ct) => s.BulkEmbeddingsAsync(b, ct)),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("AiSummary:Enabled", true))
        {
            logger.LogInformation("AiEnrichmentHostedService je vypnutý (AiSummary:Enabled=false).");
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

        logger.LogInformation("AiEnrichmentHostedService spuštěn ({Steps}, idle {Idle} min).",
            string.Join(", ", Steps.Select(s => $"{s.Name} ×{s.BatchSize}")), idleMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            var busy = false;

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<IOllamaTextService>();

                foreach (var step in Steps)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    try
                    {
                        var result = await step.Run(service, step.BatchSize, stoppingToken);
                        if (result.Processed > 0)
                        {
                            logger.LogInformation("AI {Step}: {Ok}/{Proc} OK, zbývá {Rem}.",
                                step.Name, result.Succeeded, result.Processed, result.RemainingUnprocessed);
                            // Dávka, která celá selhala (poskytovatel dole, nevalidní JSON), nesmí
                            // točit smyčku po 5 s – ta čeká na idle interval.
                            if (result.Succeeded > 0 && result.RemainingUnprocessed > 0)
                                busy = true;
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Ollama nebo cloud může být dole – nespadnout, další krok a pak idle interval
                        logger.LogWarning(ex, "AI {Step}: dávka selhala, další pokus za {Idle} min.", step.Name, idleMinutes);
                    }
                }
            }

            try
            {
                await Task.Delay(busy ? BusyDelay : idleDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
