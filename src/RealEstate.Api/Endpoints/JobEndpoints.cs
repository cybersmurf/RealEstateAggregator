using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Helpers;
using RealEstate.Api.Services.Jobs;

namespace RealEstate.Api.Endpoints;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs").RequireAdmin().WithTags("Jobs");

        group.MapGet("/{id:guid}", (Guid id, [FromServices] IBackgroundJobService jobs)
                => jobs.Get(id) is { } job ? Results.Ok(job) : Results.NotFound())
            .WithName("GetBackgroundJob")
            .WithSummary("Stav úlohy na pozadí (klasifikace fotek, export na Drive, lokální analýza).");

        group.MapGet("", ([FromQuery] Guid? listingId, [FromQuery] bool? active, [FromServices] IBackgroundJobService jobs)
                => Results.Ok(jobs.List(listingId, active ?? false)))
            .WithName("ListBackgroundJobs")
            .WithSummary("Úlohy na pozadí; ?listingId= zúží na inzerát, ?active=true jen běžící.");

        return app;
    }
}
