---
description: 'Blazor component and application patterns'
applyTo: '**/*.razor, **/*.razor.cs, **/*.razor.css'
---

## Blazor Code Style and Structure

- Write idiomatic and efficient Blazor and C# code.
- Follow .NET and Blazor conventions.
- Use Razor Components appropriately for component-based UI development.
- Prefer inline functions for smaller components but separate complex logic into code-behind or service classes.
- Async/await should be used where applicable to ensure non-blocking UI operations.
- **UI Library**: Use MudBlazor 9 as the primary UI stack. Always add explicit type parameters (`<MudChip T="string">`, `<MudCarousel TData="object">`).

## Naming Conventions

- Follow PascalCase for component names, method names, and public members.
- Use camelCase for private fields and local variables.
- Prefix interface names with "I" (e.g., IUserService).

## Blazor and .NET Specific Guidelines

- Utilize Blazor's built-in features for component lifecycle (e.g., OnInitializedAsync, OnParametersSetAsync).
- Use data binding effectively with @bind.
- Leverage Dependency Injection for services in Blazor.
- Structure Blazor components and services following Separation of Concerns.
- Always use the latest version C#, currently C# 13 features like record types, pattern matching, and global usings.

## Error Handling and Validation

- Implement proper error handling for Blazor pages and API calls.
- Use `ISnackbar` (MudBlazor) for user-facing feedback on success/error.
- Implement validation using FluentValidation or DataAnnotations in forms.

## Blazor API and Performance Optimization

- Use asynchronous methods (async/await) for API calls or UI actions that could block the main thread.
- Implement `IDisposable` + `CancellationTokenSource` for HTTP volání – cancel on Dispose().
- Minimize the component render tree by avoiding re-renders unless necessary, using ShouldRender() where appropriate.
- Use EventCallbacks for handling user interactions efficiently.

## State Management

- Use Blazor's built-in Cascading Parameters and EventCallbacks for basic state sharing.
- For filter state persistence use `ProtectedSessionStorage`.

## Security and Authentication

- Implement Authentication and Authorization using ASP.NET Identity or JWT tokens.
- Use HTTPS for all web communication and ensure proper CORS policies are implemented.
