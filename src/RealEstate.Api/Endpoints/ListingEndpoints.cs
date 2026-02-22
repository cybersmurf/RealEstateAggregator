using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Common;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

public static class ListingEndpoints
{
    public static IEndpointRouteBuilder MapListingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/listings")
            .WithTags("Listings");

        group.MapPost("/search", SearchListings)
            .WithName("SearchListings");

        group.MapGet("/{id:guid}", GetListingById)
            .WithName("GetListingById");

        group.MapPost("/{id:guid}/state", UpdateListingUserState)
            .WithName("UpdateListingUserState");

        return app;
    }

    private static async Task<Ok<PagedResultDto<ListingSummaryDto>>> SearchListings(
        [FromBody] ListingFilterDto filter,
        [FromServices] IListingService listingService,
        CancellationToken cancellationToken)
    {
        var result = await listingService.SearchAsync(filter, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ListingDetailDto>, NotFound>> GetListingById(
        Guid id,
        [FromServices] IListingService listingService,
        CancellationToken cancellationToken)
    {
        var listing = await listingService.GetByIdAsync(id, cancellationToken);
        if (listing is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(listing);
    }

    private static async Task<Results<Ok<ListingUserStateDto>, NotFound>> UpdateListingUserState(
        Guid id,
        [FromBody] ListingUserStateUpdateDto request,
        [FromServices] IListingService listingService,
        CancellationToken cancellationToken)
    {
        var state = await listingService.UpdateUserStateAsync(id, request, cancellationToken);
        if (state is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(state);
    }
}
