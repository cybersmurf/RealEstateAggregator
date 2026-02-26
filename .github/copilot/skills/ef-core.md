# EF Core Best Practices

## Queries
- Use `AsNoTracking()` for read-only queries to improve performance.
- Project to DTOs using `.Select()` – avoid fetching full entities when not needed.
- Use `.AnyAsync()` instead of `.CountAsync() > 0` for existence checks.
- Pagination with `Skip((page - 1) * pageSize).Take(pageSize)` + tiebreaker `.ThenBy(x => x.Id)`.

## Configuration
- Use `IEntityTypeConfiguration<T>` per entity instead of `OnModelCreating` bloat.
- Register via `modelBuilder.ApplyConfigurationsFromAssembly(typeof(RealEstateDbContext).Assembly)`.
- Use `UseSnakeCaseNamingConvention()` (EFCore.NamingConventions package).

## Enum Conversions (CRITICAL for this project)
- Always use switch expression in `HasConversion` – never `Enum.Parse()` (broken in EF Core expression trees).
```csharp
.HasConversion(
    v => v.ToString(),
    v => v == "House" ? PropertyType.House
       : v == "Apartment" ? PropertyType.Apartment
       : PropertyType.Other);
```

## Relationships & Loading
- Default to explicit loading; use `Include()` only when needed.
- Use filtered includes: `.Include(l => l.Photos.Where(p => p.IsActive))`.
- Avoid N+1: load related data in one query or use a `JOIN`-based projection.

## Migrations
```bash
dotnet ef migrations add MigrationName --project src/RealEstate.Infrastructure
dotnet ef database update --project src/RealEstate.Api
```

## Unit Testing
- Use `InMemoryDatabase` provider for fast unit tests.
- Use a real PostgreSQL test database (Docker) for integration tests involving spatial/vector queries.
