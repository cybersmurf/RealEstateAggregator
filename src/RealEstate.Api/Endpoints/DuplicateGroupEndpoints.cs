using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Api.Services.Duplicates;

namespace RealEstate.Api.Endpoints;

public static class DuplicateGroupEndpoints
{
    public static IEndpointRouteBuilder MapDuplicateGroupEndpoints(this IEndpointRouteBuilder app)
    {
        // Veřejné – sloučená karta duplicit je součást tarifu Zdarma
        app.MapGet("/api/listings/{id:guid}/duplicates", GetDuplicateGroup)
            .WithName("GetDuplicateGroup")
            .WithTags("Listings")
            .WithSummary("Vrátí tutéž nemovitost ve všech zdrojích (primární inzerát + duplikáty, aktivní i stažené).")
            .Produces<DuplicateGroupDto>(200)
            .Produces(404);

        return app;
    }

    private static async Task<IResult> GetDuplicateGroup(
        [FromRoute] Guid id,
        [FromServices] IDuplicateGroupService service,
        CancellationToken ct)
    {
        var group = await service.GetGroupAsync(id, ct);
        return group is null
            ? Results.NotFound(new { message = $"Inzerát {id} nenalezen." })
            : Results.Ok(group);
    }
}
