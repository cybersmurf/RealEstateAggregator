---
description: 'Guidelines for building REST APIs with ASP.NET'
applyTo: '**/*.cs, **/*.json'
---

# ASP.NET REST API Development

## API Design Fundamentals

- Use REST architectural principles with resource-oriented URLs and appropriate HTTP verb usage.
- Prefer Minimal APIs with `MapGroup` over controller-based APIs for this project.
- Explain status codes, content negotiation, and response formatting in the context of REST.

## Implementing Minimal APIs

- Group related endpoints using `MapGroup()` extension.
- Use `TypedResults` for strongly-typed responses: `TypedResults.Ok(result)`.
- Use `Results<T1, T2>` to represent multiple response types.
- Inject services via `[FromServices]` in endpoint delegates.

## Data Access Patterns

- Use Entity Framework Core with snake_case naming (`UseSnakeCaseNamingConvention()`).
- Use `AsNoTracking()` for read-only queries.
- Implement pagination with `.Skip().Take().ThenBy(x => x.Id)` (tiebreaker for deterministic ordering).
- Avoid N+1 queries with proper `.Include()` / filtered includes.

## Validation and Error Handling

- Use data annotations or FluentValidation for model validation.
- Use global exception handling middleware.
- Return problem details (RFC 9457) for standardized error responses.

## Logging and Monitoring

- Use structured logging with `ILogger<T>`.
- Include scoped logging with meaningful context.
- Implement health checks at `/health`.

## Security

- Protect sensitive endpoints with API key middleware (`X-Api-Key` header).
- Configure CORS properly with `AddCors()` + `UseCors()`.
- Never hardcode secrets – use environment variables or `appsettings.json`.
- Use HTTPS for all web communication.

## CancellationToken

- Always pass `CancellationToken ct` to all async endpoint delegates and service methods.
- Pass CT down to EF Core queries and HTTP client calls.
