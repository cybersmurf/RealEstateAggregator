using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Overview;
using RealEstate.Api.Helpers;
using RealEstate.Api.Services.Auth;
using RealEstate.Api.Services.Overview;

namespace RealEstate.Api.Endpoints;

/// <summary>/api/overview – data pro stránku /prehled (Dnes, Kandidáti, Prohlídky) a pro ranní úlohu přes MCP.</summary>
public static class OverviewEndpoints
{
    public static IEndpointRouteBuilder MapOverviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/overview").WithTags("Overview").RequireAuth();

        group.MapGet("/digest", async ([FromServices] IOverviewService svc, CancellationToken ct) =>
            await svc.GetLatestDigestAsync(ct) is { } d ? Results.Ok(d) : Results.NoContent()).WithName("GetLatestDigest");
        group.MapPost("/digest", async ([FromBody] SaveDigestDto dto, [FromServices] IOverviewService svc, CancellationToken ct) =>
            Results.Ok(await svc.SaveDigestAsync(dto, ct))).RequireAdmin().WithName("SaveDigest");

        group.MapGet("/candidates", async ([FromServices] IOverviewService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetCandidatesAsync(ct))).WithName("GetCandidates");

        group.MapGet("/votes", async ([FromQuery] Guid? listingId, [FromServices] IOverviewService svc, CancellationToken ct) =>
            listingId is { } id ? Results.Ok(await svc.GetVotesAsync(id, ct)) : Results.Ok(await svc.GetAllVotesAsync(ct))).WithName("GetVotes");
        group.MapPut("/votes/{listingId:guid}", async (Guid listingId, [FromBody] VoteRequestDto dto, [FromServices] IOverviewService svc, [FromServices] ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.CanWriteWorkspace && !user.IsAdmin) return Results.Forbid();
            try { return Results.Ok(await svc.VoteAsync(listingId, dto, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).WithName("Vote");

        group.MapPut("/ranks", async ([FromBody] List<RankItemDto> items, [FromServices] IOverviewService svc, [FromServices] ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.CanWriteWorkspace && !user.IsAdmin) return Results.Forbid();
            await svc.SaveRanksAsync(items, ct);
            return Results.NoContent();
        }).WithName("SaveRanks");

        group.MapGet("/visits", async ([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromServices] IOverviewService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetVisitsAsync(from, to, ct))).WithName("GetVisits");
        group.MapGet("/visits/{id:guid}", async (Guid id, [FromServices] IOverviewService svc, CancellationToken ct) =>
            await svc.GetVisitAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound()).WithName("GetVisit");
        group.MapPost("/visits", async ([FromBody] VisitUpsertDto dto, [FromServices] IOverviewService svc, [FromServices] ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.CanWriteWorkspace && !user.IsAdmin) return Results.Forbid();
            try { return Results.Ok(await svc.CreateVisitAsync(dto, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).WithName("CreateVisit");
        group.MapPut("/visits/{id:guid}", async (Guid id, [FromBody] VisitUpsertDto dto, [FromServices] IOverviewService svc, [FromServices] ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.CanWriteWorkspace && !user.IsAdmin) return Results.Forbid();
            return await svc.UpdateVisitAsync(id, dto, ct) is { } v ? Results.Ok(v) : Results.NotFound();
        }).WithName("UpdateVisit");
        group.MapPost("/visits/{id:guid}/done", async (Guid id, [FromBody] VisitDoneDto? dto, [FromServices] IOverviewService svc, [FromServices] ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.CanWriteWorkspace && !user.IsAdmin) return Results.Forbid();
            return await svc.CompleteVisitAsync(id, dto?.Note, ct) is { } v ? Results.Ok(v) : Results.NotFound();
        }).WithName("CompleteVisit");
        group.MapDelete("/visits/{id:guid}", async (Guid id, [FromServices] IOverviewService svc, [FromServices] ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.CanWriteWorkspace && !user.IsAdmin) return Results.Forbid();
            return await svc.DeleteVisitAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        }).WithName("DeleteVisit");

        return app;
    }
}

public sealed record VisitDoneDto(string? Note);
