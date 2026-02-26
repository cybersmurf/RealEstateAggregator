# .NET Best Practices

## Modern C# Features
- Use **primary constructors** (C# 12) for service classes.
- Use **record types** for DTOs and value objects.
- Use **pattern matching**, switch expressions, and `is not null` checks.
- Use **collection expressions** `[1, 2, 3]` and `..` spread operator.
- Use `required` modifier on record properties when mandatory.

## SOLID Principles
- **Single Responsibility:** One class, one reason to change.
- **Open/Closed:** Extend via composition/interfaces, not modification.
- **Dependency Inversion:** Depend on abstractions (`IListingService`), not implementations.

## Dependency Injection
- Register services in `ServiceCollectionExtensions.cs` extension methods.
- Lifetimes: `Singleton` (stateless), `Scoped` (per-request), `Transient` (cheap + stateless).
- Never inject `IServiceProvider` directly – use proper DI instead.

## Logging
- Use structured logging: `logger.LogInformation("Found {Count} listings for {Query}", count, query)`.
- Never concatenate strings in log messages.
- Inject `ILogger<T>` via constructor.

## XML Documentation
- Add `/// <summary>` on all public APIs, especially interfaces.

## Error Handling
- Use `Result<T>` pattern or domain exceptions for expected failures.
- Use `ProblemDetails` for HTTP error responses in Minimal APIs.
- Log unexpected exceptions at `Error` level with full context.
