using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
            // Read from environment variable OR configuration, environment takes precedence
            var scraperApiUrl = Environment.GetEnvironmentVariable("SCRAPER_API_BASE_URL");
            Console.WriteLine($"[DEBUG] SCRAPER_API_BASE_URL from env: {scraperApiUrl}");
            
            if (string.IsNullOrEmpty(scraperApiUrl))
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                scraperApiUrl = configuration["ScraperApi:BaseUrl"] ?? "http://localhost:8001";
                Console.WriteLine($"[DEBUG] ScraperApi:BaseUrl from config: {scraperApiUrl}");
            }
            
            Console.WriteLine($"[DEBUG] Final ScraperApi URL: {scraperApiUrl}");
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
            options.ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        return services;
    }
}
