using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Helpers;
using RealEstate.Api.Services;
using RealEstate.Api.Services.Jobs;

namespace RealEstate.Api.Endpoints;

public static class InspectionComparisonEndpoints
{
    public static IEndpointRouteBuilder MapInspectionComparisonEndpoints(this IEndpointRouteBuilder app)
    {
        // Jen správce: drahé (obrazový model) a pracuje s fotkami z prohlídek vlastníka
        var group = app.MapGroup("/api")
            .WithTags("Inspection comparison")
            .RequireAdmin();

        group.MapPost("/listings/{id:guid}/compare-inspection", Compare)
            .WithName("CompareInspectionPhotos")
            .WithSummary("Porovná fotky z inzerátu s fotkami z prohlídky a uloží nálezy + zprávu do analýz.");

        group.MapGet("/listings/{id:guid}/inspection-comparison", Get)
            .WithName("GetInspectionComparison")
            .Produces<List<PhotoComparisonDto>>(200);

        group.MapGet("/inspection-comparisons/summary", Summary)
            .WithName("GetInspectionComparisonSummary")
            .WithSummary("Co se mezi inzeráty a skutečností liší nejčastěji – přes všechny porovnané domy.")
            .Produces<InspectionFindingsSummaryDto>(200);

        return app;
    }

    private static async Task<IResult> Compare(
        Guid id,
        [FromQuery] bool force = false,
        [FromQuery] bool wait = true,
        [FromServices] IBackgroundJobService jobs = default!,
        CancellationToken cancellationToken = default)
    {
        // Klasifikace stovek fotek z prohlídky trvá minuty – běží jako úloha na pozadí
        var jobId = jobs.Enqueue("inspection-compare", id, async (sp, ct) =>
            await sp.GetRequiredService<IInspectionComparisonService>().CompareAsync(id, force, ct));
        if (!wait)
            return Results.Accepted($"/api/jobs/{jobId}", new { jobId });
        var job = await jobs.WaitAsync(jobId, TimeSpan.FromMinutes(45), cancellationToken);
        return Results.Ok(job.Result);
    }

    private static async Task<IResult> Get(
        Guid id,
        [FromServices] IInspectionComparisonService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await service.GetAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> Summary(
        [FromServices] IInspectionComparisonService service,
        CancellationToken cancellationToken)
        => Results.Ok(await service.GetSummaryAsync(cancellationToken));
}
