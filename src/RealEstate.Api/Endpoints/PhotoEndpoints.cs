using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

public static class PhotoEndpoints
{
    public static IEndpointRouteBuilder MapPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/photos")
            .WithTags("Photos");

        group.MapPost("/bulk-download", BulkDownload)
            .WithName("BulkDownloadPhotos")
            .WithSummary("Stáhne dávku fotek z original_url a uloží je lokálně.")
            .Produces<PhotoDownloadResultDto>(200);

        group.MapGet("/stats", GetStats)
            .WithName("GetPhotoStats")
            .WithSummary("Vrátí statistiku stažených vs. nestažených fotek.")
            .Produces<PhotoDownloadStatsDto>(200);

        return app;
    }

    private static async Task<IResult> BulkDownload(
        [FromQuery] int batchSize = 50,
        [FromServices] IPhotoDownloadService service = default!,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1 || batchSize > 200)
            return Results.Problem(
                title: "Neplatný batchSize",
                detail: "batchSize musí být v rozmezí 1–200.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await service.DownloadBatchAsync(batchSize, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetStats(
        [FromServices] IPhotoDownloadService service = default!,
        CancellationToken cancellationToken = default)
    {
        var stats = await service.GetStatsAsync(cancellationToken);
        return Results.Ok(stats);
    }
}
