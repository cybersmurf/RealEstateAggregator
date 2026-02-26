# C# xUnit Testing Best Practices

## Test Structure
- Use `[Fact]` for single-assertion tests, `[Theory]` + `[InlineData]`/`[MemberData]` for parameterized tests.
- Follow **Arrange / Act / Assert** pattern with blank lines separating sections.
- Naming: `MethodName_Scenario_ExpectedBehavior` (e.g., `Search_WithPriceFilter_ReturnsFilteredResults`).

## Assertions
- Use `Assert.Equal(expected, actual)` – expected first.
- Use `Assert.ThrowsAsync<TException>()` for async exception testing.
- Prefer `Assert.NotNull(value)` + dereference over `Assert.True(value != null)`.

## Test Isolation
- Each test must be independent – no shared mutable state.
- Use `IClassFixture<T>` for expensive shared setup (e.g., database context).
- Use constructor injection for dependencies.

## Mocking
- Use Moq or NSubstitute for interfaces.
- Verify only what matters – don't over-specify interactions.
```csharp
var mockRepo = new Mock<IListingRepository>();
mockRepo.Setup(r => r.SearchAsync(It.IsAny<ListingFilter>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new PagedResult<Listing>());
```

## Async Tests
- Always `await` async operations – never `.Result` or `.Wait()`.
- Return `Task` from async test methods, not `void`.

## Project Setup
- Test project: `tests/RealEstate.Tests/RealEstate.Tests.csproj`
- Run: `dotnet test tests/RealEstate.Tests`
