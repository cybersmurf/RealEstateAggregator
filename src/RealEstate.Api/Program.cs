using Microsoft.EntityFrameworkCore;
using RealEstate.Api;
using RealEstate.Api.Endpoints;
using RealEstate.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Zvýšínmá limit tela požadavku pro upload fotek z prohlídky (výchozí 30 MB nestací)
builder.WebHost.ConfigureKestrel(opts => opts.Limits.MaxRequestBodySize = 500_000_000);

// ─── API Key ──────────────────────────────────────────────────────────────────
// Načteme z prostředí, fallback na výchozí dev hodnotu.
// V produkci nastavit: API_KEY=<tajný klíč>
var apiKey = Environment.GetEnvironmentVariable("API_KEY") ?? "dev-key-change-me";

// Override connection string and scraper API base URL from environment variables
var dbHost = Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost";
var dbPort = Environment.GetEnvironmentVariable("DB_PORT") ?? "5432";
var dbName = Environment.GetEnvironmentVariable("DB_NAME") ?? "realestate_dev";
var dbUser = Environment.GetEnvironmentVariable("DB_USER") ?? "postgres";
var dbPassword = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "dev";
var scraperApiBaseUrl = Environment.GetEnvironmentVariable("SCRAPER_API_BASE_URL") ?? "http://localhost:8001";

Console.WriteLine($"[STARTUP] SCRAPER_API_BASE_URL={scraperApiBaseUrl}");

var connectionString = $"Host={dbHost};Port={dbPort};Database={dbName};Username={dbUser};Password={dbPassword}";
builder.Configuration["ConnectionStrings:RealEstate"] = connectionString;

// Ensure environment variable takes precedence for all sources
if (!string.IsNullOrEmpty(scraperApiBaseUrl))
{
    builder.Configuration["ScraperApi:BaseUrl"] = scraperApiBaseUrl;
}

builder.Services
    .AddEndpointsApiExplorer()
    .AddSwaggerGen();

// ─── CORS ─────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:5002",   // Blazor App dev
                "http://realestate-app:8080") // Docker
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// Custom services
builder.Services.AddRealEstateDb(builder.Configuration);
builder.Services.AddRealEstateServices(builder.Configuration);
builder.Services.AddStorageService(builder.Configuration);


var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    var skipMigrations = string.Equals(
        Environment.GetEnvironmentVariable("SKIP_EF_MIGRATIONS"),
        "true",
        StringComparison.OrdinalIgnoreCase);

    if (!skipMigrations)
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RealEstateDbContext>();
        
        // 🔥 Use EnsureCreatedAsync instead of MigrateAsync to avoid column naming conflicts
        // This creates the database schema from scratch if needed
        await dbContext.Database.EnsureCreatedAsync();
        
        await DbInitializer.SeedAsync(dbContext);
    }
}
else
{
    app.UseHttpsRedirection();
}

// Enable static files for local storage serving
app.UseStaticFiles();

app.UseCors();

// ─── Endpoints ────────────────────────────────────────────────────────────────
// Health check – veřejně přístupný (používá Docker healthcheck a monitoring)
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
    .WithName("Health")
    .AllowAnonymous();

app.MapListingEndpoints();
app.MapSourceEndpoints();
app.MapAnalysisEndpoints();
app.MapExportEndpoints();
app.MapDriveAuthEndpoints();
app.MapOneDriveAuthEndpoints();
app.MapRagEndpoints();

// ─── Scraping endpoints – chráněno API klíčem ─────────────────────────────────
app.MapScrapingEndpoints()
    .AddEndpointFilter(async (ctx, next) =>
    {
        if (!ctx.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var providedKey)
            || providedKey != apiKey)
        {
            return Results.Problem(
                title: "Unauthorized",
                detail: "Platný API klíč je vyžadován v hlavičce X-Api-Key.",
                statusCode: StatusCodes.Status401Unauthorized);
        }
        return await next(ctx);
    });

app.Run();
