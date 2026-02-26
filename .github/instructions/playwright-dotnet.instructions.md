---
description: 'Playwright .NET test generation instructions'
applyTo: '**'
---
# Playwright .NET Test Generation Guidelines

## Test Structure
- Base all page test classes on `PageTest` for built-in browser fixtures.
- Use xUnit `[Fact]` for single-scenario tests, `[Theory]` + `[InlineData]` for parameterized cases.
- Naming convention: `MethodName_Scenario_ExpectedBehavior`.

## Locators
- **Prefer semantic locators** in this order:
  1. `GetByRole()` – buttons, links, headings, form controls
  2. `GetByLabel()` – form inputs
  3. `GetByText()` – readable content
  4. `GetByTestId()` – fallback for dynamic/generated HTML
- **Avoid:** CSS selectors, XPath, or DOM-structure-dependent locators.

```csharp
// GOOD
await Page.GetByRole(AriaRole.Button, new() { Name = "Hledat" }).ClickAsync();
var input = Page.GetByLabel("Cena do");

// BAD
await Page.ClickAsync(".mud-button:nth-child(2)");
```

## Assertions
- Use `Expect()` with auto-retry (never `Assert.True(await ...IsVisibleAsync())`).
```csharp
await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Inzeráty" }))
    .ToBeVisibleAsync();
await Expect(Page.GetByText("Praha")).ToBeVisibleAsync(new() { Timeout = 10_000 });
```

## ARIA Snapshots
- Use `ToMatchAriaSnapshotAsync` to capture the accessible structure of a region:
```csharp
await Expect(Page.Locator(".listings-grid"))
    .ToMatchAriaSnapshotAsync("- list: ...");
```

## File Naming
- Place tests in `tests/RealEstate.PlaywrightTests/`
- Name files `<Feature>Tests.cs` (e.g., `ListingsFilterTests.cs`)
- One class per page/feature under test.

## Network & State
- Intercept and mock external HTTP calls in tests using `Page.RouteAsync()`.
- Use `BrowserContext.StorageStateAsync()` to persist authentication state.
