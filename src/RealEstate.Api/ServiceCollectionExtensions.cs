using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using RealEstate.Api.Services;
using RealEstate.Domain.Repositories;
using RealEstate.Infrastructure;
using RealEstate.Infrastructure.Repositories;

namespace RealEstate.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRealEstateServices(this IServiceCollection services)
    {
        services.AddScoped<IListingService, ListingService>();
        services.AddScoped<ISourceService, SourceService>();
        services.AddScoped<IAnalysisService, AnalysisService>();
        services.AddScoped<IScrapingService, ScrapingService>();

        // Repositories
        services.AddScoped<IListingRepository, ListingRepository>();

        services.AddHttpClient("ScraperApi", (sp, client) =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var scraperApiUrl = configuration["ScraperApi:BaseUrl"] ?? "http://localhost:8001";
            client.BaseAddress = new Uri(scraperApiUrl);
            client.Timeout = TimeSpan.FromMinutes(2);
        });

        return services;
    }

    public static IServiceCollection AddRealEstateDb(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("RealEstate")
            ?? throw new InvalidOperationException("Connection string 'RealEstate' not found.");

        services.AddDbContext<RealEstateDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                // 🔥 Enable pgvector support in EF Core
                npgsqlOptions.UseVector();
            });
            
            // 🔥 Enable snake_case naming convention for PostgreSQL
            options.UseSnakeCaseNamingConvention();
        });

        return services;
    }
}
