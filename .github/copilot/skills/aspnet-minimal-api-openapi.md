# ASP.NET Minimal API & OpenAPI Best Practices

## Endpoint Organization
- Group related endpoints with `MapGroup()`:
```csharp
var listings = app.MapGroup("/api/listings").WithTags("Listings");
listings.MapPost("/search", SearchListings).WithName("SearchListings");
listings.MapGet("/{id:guid}", GetListing).WithName("GetListing");
```

## TypedResults
- Always use `TypedResults` (not `Results`) for strongly-typed return types:
```csharp
private static async Task<Results<Ok<PagedResultDto<ListingSummaryDto>>, BadRequest<string>>> SearchListings(
    [FromBody] ListingFilterDto filter,
    [FromServices] IListingService service,
    CancellationToken ct)
{
    if (filter.PageSize > 100) return TypedResults.BadRequest("PageSize max 100");
    var result = await service.SearchAsync(filter, ct);
    return TypedResults.Ok(result);
}
```

## Request/Response DTOs
- Use `record` types for immutable DTOs.
- Use `[Description("...")]` attributes for OpenAPI documentation.
- Validate using `IEndpointFilter` or `FluentValidation`.

## OpenAPI Metadata
```csharp
.WithName("OperationId")
.WithSummary("Brief description")
.WithDescription("Detailed description")
.Produces<PagedResultDto<ListingSummaryDto>>(200)
.ProducesProblem(400)
```

## CancellationToken
- Every endpoint handler must accept and forward `CancellationToken ct`.

## Error Handling
- Register `ProblemDetailsService` in DI.
- Return `TypedResults.Problem(...)` for unexpected errors.
- Use `IExceptionHandler` for global unhandled exception mapping.
