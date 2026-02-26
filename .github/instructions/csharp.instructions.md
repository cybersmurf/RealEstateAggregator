---
description: 'Guidelines for building C# applications'
applyTo: '**/*.cs'
---

# C# Development

## C# Instructions
- Always use the latest version C#, currently C# 12/.NET 10 features (primary constructors, collection expressions, pattern matching).
- Write clear and concise comments for each function.

## General Instructions
- Make only high confidence suggestions when reviewing code changes.
- Write code with good maintainability practices, including comments on why certain design decisions were made.
- Handle edge cases and write clear exception handling.

## Naming Conventions

- Follow PascalCase for component names, method names, and public members.
- Use camelCase for private fields and local variables.
- Prefix interface names with "I" (e.g., IUserService).

## Formatting

- Apply code-formatting style defined in `.editorconfig`.
- Prefer file-scoped namespace declarations and single-line using directives.
- Use pattern matching and switch expressions wherever possible.
- Use `nameof` instead of string literals when referring to member names.
- Ensure that XML doc comments are created for any public APIs.

## Project Architecture

- Use Minimal APIs with MapGroup for endpoint organization.
- Use primary constructors for dependency injection: `public sealed class MyService(IRepo repo, ILogger<MyService> logger)`.
- Use record types for DTOs.
- Use EF Core with snake_case naming convention (`UseSnakeCaseNamingConvention()`).
- **NEVER use AutoMapper** – use manual mapping.

## Enum Conversions in EF Core

- **NEVER use `Enum.Parse()`** in `HasConversion` lambda – it fails in EF Core expression trees.
- Use switch expressions instead:
  ```csharp
  v == "House" ? PropertyType.House
     : v == "Apartment" ? PropertyType.Apartment
     : PropertyType.Other
  ```
- DB stores English values: "House", "Apartment", "Sale", "Rent", "Auction".

## Nullable Reference Types

- Declare variables non-nullable, and check for `null` at entry points.
- Always use `is null` or `is not null` instead of `== null` or `!= null`.
- Trust the C# null annotations and don't add null checks when the type system says a value cannot be null.

## Testing

- Always include test cases for critical paths of the application.
- Do not emit "Act", "Arrange" or "Assert" comments.
- Use xUnit with `[Fact]` and `[Theory]` + `[InlineData]`.
- Copy existing style in nearby files for test method names and capitalization.
