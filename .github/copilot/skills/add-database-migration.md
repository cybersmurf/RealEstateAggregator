# Add Database Migration Skill

## Description
Create and apply EF Core database migration for RealEstateAggregator.

## Usage
```bash
gh copilot suggest "add database migration for user favorites"
```

## Steps

1. **Modify entity in Domain/Entities/**
   - Add properties to existing entity or create new entity
   - Follow snake_case convention (EFCore.NamingConventions handles mapping)
   - Example:
     ```csharp
     public class UserFavorite
     {
         public Guid Id { get; set; }
         public Guid UserId { get; set; }
         public Guid ListingId { get; set; }
         public DateTime CreatedAt { get; set; }
         
         // Navigation properties
         public Listing Listing { get; set; } = null!;
     }
     ```

2. **Update DbContext (if new entity)**
   ```csharp
   // src/RealEstate.Infrastructure/RealEstateDbContext.cs
   public DbSet<UserFavorite> UserFavorites { get; set; } = null!;
   
   protected override void OnModelCreating(ModelBuilder modelBuilder)
   {
       base.OnModelCreating(modelBuilder);
       
       modelBuilder.Entity<UserFavorite>(entity =>
       {
           entity.ToTable("user_favorites", "re_realestate");
           
           entity.HasKey(e => e.Id);
           
           entity.HasOne(e => e.Listing)
               .WithMany()
               .HasForeignKey(e => e.ListingId)
               .OnDelete(DeleteBehavior.Cascade);
           
           entity.HasIndex(e => new { e.UserId, e.ListingId })
               .IsUnique();
       });
   }
   ```

3. **Create migration**
   ```bash
   cd src/RealEstate.Infrastructure
   
   dotnet ef migrations add AddUserFavorites \
     --startup-project ../RealEstate.Api \
     --context RealEstateDbContext
   ```

4. **Review generated migration**
   - Check `Migrations/{Timestamp}_AddUserFavorites.cs`
   - Verify column names are snake_case
   - Verify indexes and constraints

5. **Apply migration**
   ```bash
   # Development
   dotnet ef database update \
     --startup-project ../RealEstate.Api \
     --context RealEstateDbContext
   
   # Production (via Docker)
   docker-compose up -d postgres
   docker-compose exec api dotnet ef database update
   ```

6. **Verify in database**
   ```bash
   docker exec -it realestate-db psql -U postgres -d realestate_dev
   ```
   ```sql
   \d re_realestate.user_favorites
   SELECT * FROM re_realestate.user_favorites LIMIT 1;
   ```

## Checklist
- [ ] Entity added/modified in RealEstate.Domain
- [ ] DbContext updated with DbSet and OnModelCreating config
- [ ] Migration created with descriptive name
- [ ] Generated migration reviewed (snake_case columns)
- [ ] Migration applied to local database
- [ ] Database structure verified with \d command
- [ ] Indexes created for foreign keys and common queries

## Common Patterns

### Enum Converter (Czech ↔ English)
```csharp
modelBuilder.Entity<Listing>()
    .Property(l => l.PropertyType)
    .HasConversion(
        v => v.ToString(),  // Enum → string
        v => Enum.Parse<PropertyType>(v)  // string → Enum
    );
```

### JSON Column (PostgreSQL)
```csharp
modelBuilder.Entity<Listing>()
    .Property(l => l.Metadata)
    .HasColumnType("jsonb");
```

### Timestamp with Default
```csharp
modelBuilder.Entity<Listing>()
    .Property(l => l.CreatedAt)
    .HasDefaultValueSql("now()");
```

### Composite Index
```csharp
entity.HasIndex(e => new { e.SourceId, e.ExternalId })
    .IsUnique();
```

## Related Files
- `src/RealEstate.Domain/Entities/`
- `src/RealEstate.Infrastructure/RealEstateDbContext.cs`
- `src/RealEstate.Infrastructure/Migrations/`
- `scripts/init-db.sql`

## Troubleshooting

**Error:** "Build failed"
- Solution: Ensure all projects compile (`dotnet build`)

**Error:** "Could not find DbContext"
- Solution: Verify `--startup-project` points to RealEstate.Api

**Error:** "Columns are PascalCase instead of snake_case"
- Solution: Check `UseSnakeCaseNamingConvention()` is called in DbContext

**Error:** "Migration already exists"
- Solution: Remove last migration: `dotnet ef migrations remove`
