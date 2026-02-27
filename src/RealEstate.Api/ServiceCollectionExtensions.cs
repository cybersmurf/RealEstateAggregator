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
    public static IServiceCollection AddRealEstateServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<IListingService, ListingService>();
        services.AddScoped<ISourceService, SourceService>();
        services.AddScoped<IAnalysisService, AnalysisService>();
        services.AddScoped<IScrapingService, ScrapingService>();

        // RAG – embeddingy + chat: Ollama (lokální) → OpenAI → disabled
        var embeddingProvider = config["Embedding:Provider"] ?? "";
        var ollamaBaseUrl = config["Ollama:BaseUrl"];
        var openAiKey = config["OpenAI:ApiKey"];

        if (embeddingProvider.Equals("ollama", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(ollamaBaseUrl) && string.IsNullOrWhiteSpace(openAiKey)))
        {
            services.AddHttpClient("Ollama");
            services.AddSingleton<IEmbeddingService, OllamaEmbeddingService>();
        }
        else
        {
            services.AddSingleton<IEmbeddingService, OpenAIEmbeddingService>();
        }
        services.AddScoped<IRagService, RagService>();

        // Repositories
        services.AddScoped<IListingRepository, ListingRepository>();

        services.AddHttpClient("ScraperApi", (sp, client) =>
        {
            var scraperApiUrl = Environment.GetEnvironmentVariable("SCRAPER_API_BASE_URL") ?? "http://localhost:8001";
            client.BaseAddress = new Uri(scraperApiUrl);
            client.Timeout = TimeSpan.FromMinutes(2);
        });

        // Google Drive export
        services.AddScoped<IGoogleDriveExportService, GoogleDriveExportService>();
        services.AddHttpClient("DrivePhotoDownload", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (compatible; RealEstateAggregator/1.0)");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // OneDrive export
        services.AddScoped<IOneDriveExportService, OneDriveExportService>();
        services.AddHttpClient("OneDriveGraph", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddHttpClient("OneDriveToken", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient("OneDrivePhotoDownload", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (compatible; RealEstateAggregator/1.0)");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // � Photo download service
        services.AddScoped<IPhotoDownloadService, PhotoDownloadService>();        // 🔍 Photo classification service (Ollama Vision)
        services.AddScoped<IPhotoClassificationService, PhotoClassificationService>();
        services.AddHttpClient("OllamaVision", client =>
        {
            // Vision model může trvat 10–60s na fotku – velký timeout
            client.Timeout = TimeSpan.FromMinutes(5);
        });        services.AddHttpClient("PhotoDownload", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (compatible; RealEstateAggregator/1.0; +https://github.com/cybersmurf/RealEstateAggregator)");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // �📍 PostGIS Spatial service
        services.AddScoped<ISpatialService, SpatialService>();

        // 🏛️ ČÚZK/RUIAN Katastr service
        services.AddScoped<ICadastreService, CadastreService>();
        services.AddHttpClient("Ruian", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent",
                "RealEstateAggregator/1.0 (https://github.com/cybersmurf/RealEstateAggregator)");
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddHttpClient("Nominatim", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent",
                "RealEstateAggregator/1.0 (https://github.com/cybersmurf/RealEstateAggregator)");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient("Osrm", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
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
