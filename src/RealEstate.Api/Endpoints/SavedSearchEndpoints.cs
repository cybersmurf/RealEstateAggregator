using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.SavedSearches;
using RealEstate.Api.Helpers;
using RealEstate.Api.Services.SavedSearches;
using RealEstate.Domain.Entities;

namespace RealEstate.Api.Endpoints;

/// <summary>
/// Uložená hledání (tarif Hledač+). Skupina vrací 401 bez přihlášení a 402 bez tarifu.
/// Vyhodnocení (/run) volá scraper hlavním klíčem po každém scrapu – jen admin.
/// </summary>
public static class SavedSearchEndpoints
{
    public static IEndpointRouteBuilder MapSavedSearchEndpoints(this IEndpointRouteBuilder app)
    {
        // /run je mimo skupinu s RequirePlan – jinak by filtr skupiny hlásil 402 dřív než RequireAdmin
        app.MapPost("/api/saved-searches/run", RunAll)
            .WithTags("SavedSearches")
            .WithName("RunSavedSearches")
            .WithSummary("Vyhodnotí všechna aktivní uložená hledání a rozešle upozornění (volá scraper po scrapu).")
            .RequireAdmin();

        var group = app.MapGroup("/api/saved-searches")
            .WithTags("SavedSearches")
            .RequirePlan(UserPlans.Hledac);

        group.MapGet("", List)
            .WithName("ListSavedSearches")
            .WithSummary("Uložená hledání přihlášeného uživatele včetně aktuálního počtu shod.");

        group.MapPost("", Create)
            .WithName("CreateSavedSearch")
            .WithSummary("Založí uložené hledání (limit: Hledač 10, Profi 50).");

        group.MapGet("/{id:guid}", Get)
            .WithName("GetSavedSearch");

        group.MapPut("/{id:guid}", Update)
            .WithName("UpdateSavedSearch");

        group.MapDelete("/{id:guid}", Delete)
            .WithName("DeleteSavedSearch");

        group.MapGet("/{id:guid}/notifications", ListNotifications)
            .WithName("ListSavedSearchNotifications")
            .WithSummary("Posledních 50 odeslaných upozornění k hledání.");

        group.MapPost("/{id:guid}/test", SendTest)
            .WithName("SendSavedSearchTest")
            .WithSummary("Pošle zkušební zprávu do zapnutých kanálů a vrátí, které fungují.");

        return app;
    }

    private static async Task<IResult> List([FromServices] ISavedSearchService service, CancellationToken ct) =>
        Results.Ok(await service.ListAsync(ct));

    private static async Task<IResult> Get(Guid id, [FromServices] ISavedSearchService service, CancellationToken ct)
    {
        var dto = await service.GetAsync(id, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> Create(
        [FromBody] SavedSearchUpsertDto request, [FromServices] ISavedSearchService service, CancellationToken ct)
    {
        var (result, error) = await service.CreateAsync(request, ct);
        return error is null
            ? Results.Created($"/api/saved-searches/{result!.Id}", result)
            : Results.Problem(title: "Saved search rejected", detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> Update(
        Guid id, [FromBody] SavedSearchUpsertDto request, [FromServices] ISavedSearchService service, CancellationToken ct)
    {
        var (result, error) = await service.UpdateAsync(id, request, ct);
        if (result is not null) return Results.Ok(result);
        return error is null
            ? Results.NotFound()
            : Results.Problem(title: "Saved search rejected", detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> Delete(Guid id, [FromServices] ISavedSearchService service, CancellationToken ct) =>
        await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> ListNotifications(Guid id, [FromServices] ISavedSearchService service, CancellationToken ct)
    {
        var items = await service.ListNotificationsAsync(id, ct);
        return items is null ? Results.NotFound() : Results.Ok(items);
    }

    private static async Task<IResult> SendTest(Guid id, [FromServices] ISavedSearchService service, CancellationToken ct)
    {
        var result = await service.SendTestAsync(id, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> RunAll([FromServices] ISavedSearchNotifier notifier, CancellationToken ct) =>
        Results.Ok(await notifier.RunAllAsync(ct));
}
