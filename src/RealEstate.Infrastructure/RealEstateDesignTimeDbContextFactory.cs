using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;
using Pgvector.EntityFrameworkCore;

namespace RealEstate.Infrastructure;

/// <summary>
/// Design-time factory pro DbContext, používá se pro EF migrations bez DI
/// </summary>
public class RealEstateDesignTimeDbContextFactory : IDesignTimeDbContextFactory<RealEstateDbContext>
{
    public RealEstateDbContext CreateDbContext(string[] args)
    {
        var connectionString = "Host=localhost;Port=5432;Database=realestate_dev;Username=postgres;Password=dev";
        
        var optionsBuilder = new DbContextOptionsBuilder<RealEstateDbContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.UseVector();
        });

        return new RealEstateDbContext(optionsBuilder.Options);
    }
}
