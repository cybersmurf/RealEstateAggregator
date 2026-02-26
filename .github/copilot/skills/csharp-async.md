# C# Async/Await Best Practices

## Method Naming & Signatures
- All async methods must end with `Async` suffix.
- Return `Task` (no return value) or `Task<T>` (with return value).
- Use `ValueTask<T>` only when the method frequently completes synchronously.
- Always accept `CancellationToken ct` as the last parameter and pass it down.

## Avoiding Common Pitfalls
- **Never** use `.Result` or `.Wait()` – causes deadlocks in Blazor/ASP.NET contexts.
- **Never** use `async void` – exceptions are unobservable. Use `async Task` instead.
- Do NOT use `Task.Run()` to offload CPU work unless explicitly needed.

## Parallelism
- Use `Task.WhenAll()` for independent concurrent operations:
```csharp
var (sources, count) = await (
    repo.GetSourcesAsync(ct),
    repo.GetCountAsync(ct)
).WhenAll(); // or use tuple deconstruct with Task.WhenAll
```
- Use `SemaphoreSlim` to limit concurrency in loops.

## ConfigureAwait
- Use `ConfigureAwait(false)` in library/infrastructure code (not Blazor component code).

## Streaming
- Return `IAsyncEnumerable<T>` for large datasets instead of `List<T>`.

## CancellationToken in Blazor
- In Blazor components, use `CancellationTokenSource` tied to `IDisposable.Dispose()`:
```csharp
private readonly CancellationTokenSource _cts = new();
public void Dispose() => _cts.Cancel();
// In methods:
await service.SearchAsync(filter, _cts.Token);
```
