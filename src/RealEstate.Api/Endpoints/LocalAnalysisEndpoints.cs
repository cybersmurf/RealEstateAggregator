using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

public static class LocalAnalysisEndpoints
{
    public static IEndpointRouteBuilder MapLocalAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/listings/{listingId:guid}/analyze-local")
            .WithTags("LocalAnalysis");

        // POST /api/listings/{id}/analyze-local[?model=qwen3.5:9b]
        // Spustí lokální analýzu (llama3.2-vision + text model) a uloží výsledek do DB
        group.MapPost("", RunAnalysis)
            .WithName("RunLocalAnalysis")
            .WithSummary("Spustí lokální AI analýzu nemovitosti. Volitelně ?model=qwen3.5:9b pro jiný text model.");

        // GET /api/listings/{id}/analyze-local/docx[?model=qwen3.5:9b]
        // Vrátí DOCX soubor nejnovější lokální analýzy (nebo spustí novou)
        group.MapGet("/docx", DownloadDocx)
            .WithName("DownloadLocalAnalysisDocx")
            .WithSummary("Stáhne analýzu jako DOCX soubor (generuje přes pandoc). ?model= pro výběr varianty.");

        return app;
    }

    private static async Task<Results<Ok<LocalAnalysisResultDto>, NotFound<string>, StatusCodeHttpResult>> RunAnalysis(
        Guid listingId,
        [FromQuery] string? model,
        [FromServices] ILocalAnalysisService service,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("LocalAnalysisEndpoints");
        try
        {
            var result = await service.AnalyzeAsync(listingId, model, ct);
            return TypedResults.Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RunAnalysis selhal pro listing {ListingId} model {Model}", listingId, model);
            return TypedResults.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> DownloadDocx(
        Guid listingId,
        [FromQuery] string? model,
        [FromServices] ILocalAnalysisService service,
        CancellationToken ct)
    {
        try
        {
            var bytes = await service.ExportDocxAsync(listingId, model, ct);
            if (bytes is null || bytes.Length == 0)
                return Results.Problem("Nepodařilo se vygenerovat DOCX soubor (je pandoc nainstalován?).",
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var shortId = listingId.ToString("N")[..8];
            return Results.File(
                bytes,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                $"analyza_{shortId}.docx");
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }
}
