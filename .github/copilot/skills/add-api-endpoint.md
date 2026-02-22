# Add API Endpoint Skill

## Description
Add a new Minimal API endpoint to RealEstate.Api with full implementation (DTO, endpoint, service method).

## Usage
```bash
gh copilot suggest "add api endpoint for getting top N listings by price"
```

## Steps

1. **Create DTO in Contracts/** (if needed)
   - Location: `src/RealEstate.Api/Contracts/{Feature}/`
   - Pattern: Use `record` types
   - Example:
     ```csharp
     public record GetTopListingsDto(
         int Count = 10,
         PropertyType? PropertyType = null
     );
     ```

2. **Add endpoint in Endpoints/{Feature}Endpoints.cs**
   - Use existing endpoint group
   - Follow pattern:
     ```csharp
     group.MapGet("/top", GetTopListings).WithName("GetTopListings");
     
     private static async Task<Ok<List<ListingSummaryDto>>> GetTopListings(
         [FromQuery] int count,
         [FromServices] IListingService service,
         CancellationToken ct)
     {
         var result = await service.GetTopListingsAsync(count, ct);
         return TypedResults.Ok(result);
     }
     ```

3. **Add method to I{Feature}Service interface**
   - Location: `src/RealEstate.Api/Services/I{Feature}Service.cs`
   - Include CancellationToken parameter
   - Use async Task<T> return type

4. **Implement in {Feature}Service**
   - Location: `src/RealEstate.Api/Services/{Feature}Service.cs`
   - Use EF Core with AsNoTracking() for read operations
   - Example:
     ```csharp
     public async Task<List<ListingSummaryDto>> GetTopListingsAsync(
         int count, 
         CancellationToken ct)
     {
         return await context.Listings
             .AsNoTracking()
             .OrderByDescending(l => l.Price)
             .Take(count)
             .Select(l => new ListingSummaryDto(
                 l.Id,
                 l.Title,
                 l.Price,
                 l.LocationText
             ))
             .ToListAsync(ct);
     }
     ```

5. **Test the endpoint**
   ```bash
   curl http://localhost:5001/api/listings/top?count=5 | jq
   ```

## Checklist
- [ ] DTO created with appropriate validation attributes
- [ ] Endpoint added to endpoint group with .WithName()
- [ ] Interface method added to I{Feature}Service
- [ ] Service implementation uses async/await
- [ ] EF Core query uses AsNoTracking() if read-only
- [ ] CancellationToken passed to async methods
- [ ] Endpoint tested with curl or Swagger

## Related Files
- `src/RealEstate.Api/Endpoints/ListingEndpoints.cs`
- `src/RealEstate.Api/Services/ListingService.cs`
- `src/RealEstate.Api/Contracts/Listings/`
