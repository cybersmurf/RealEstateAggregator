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

        group.MapGet("/stats", GetStats)
            .WithName("GetListingStats");

        group.MapGet("/my-listings", GetMyListings)
            .WithName("GetMyListings")
            .WithSummary("Vrátí inzeráty tagované uživatelem, seskupené dle stavu");

        group.MapGet("/export.csv", ExportCsv)
            .WithName("ExportListingsCsv")
            .WithSummary("Exportuje výsledky vyhledávání jako CSV soubor (max 5000 záznamů, UTF-8 BOM)")
            .Produces<FileContentResult>(200, "text/csv");

        group.MapGet("/{id:guid}", GetListingById)
            .WithName("GetListingById");

        group.MapPost("/{id:guid}/state", UpdateListingUserState)
            .WithName("UpdateListingUserState");

        // User photo upload endpoints
        group = group.MapUserPhotoEndpoints();

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

    private static async Task<Ok<ListingStatsDto>> GetStats(
        [FromServices] IListingService listingService,
        CancellationToken cancellationToken)
    {
        var result = await listingService.GetStatsAsync(cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Ok<MyListingsSummaryDto>> GetMyListings(
        [FromServices] IListingService listingService,
        CancellationToken cancellationToken)
    {
        var result = await listingService.GetMyListingsAsync(cancellationToken);
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

    private static async Task<IResult> ExportCsv(
        [FromQuery] string? searchText,
        [FromQuery] string? propertyType,
        [FromQuery] string? offerType,
        [FromQuery] decimal? priceMin,
        [FromQuery] decimal? priceMax,
        [FromQuery] double? areaBuiltUpMin,
        [FromQuery] double? areaBuiltUpMax,
        [FromQuery] double? areaLandMin,
        [FromQuery] double? areaLandMax,
        [FromQuery] string? region,
        [FromQuery] string? district,
        [FromQuery] string? municipality,
        [FromQuery] string? userStatus,
        [FromQuery] string? sortBy,
        [FromQuery] bool? sortDescending,
        [FromServices] IListingService listingService,
        CancellationToken cancellationToken)
    {
        var filter = new ListingFilterDto
        {
            SearchText     = searchText,
            PropertyType   = propertyType,
            OfferType      = offerType,
            PriceMin       = priceMin,
            PriceMax       = priceMax,
            AreaBuiltUpMin = areaBuiltUpMin,
            AreaBuiltUpMax = areaBuiltUpMax,
            AreaLandMin    = areaLandMin,
            AreaLandMax    = areaLandMax,
            Region         = region,
            District       = district,
            Municipality   = municipality,
            UserStatus     = userStatus,
            SortBy         = sortBy,
            SortDescending = sortDescending ?? true,
        };

        var csvBytes = await listingService.ExportCsvAsync(filter, cancellationToken);
        var filename = $"inzeraty_{DateTime.Now:yyyyMMdd_HHmm}.csv";

        return Results.File(csvBytes, "text/csv; charset=utf-8", filename);
    }
}
