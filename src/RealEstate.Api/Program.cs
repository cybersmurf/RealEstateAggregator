using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using RealEstate.Api;
using RealEstate.Api.Endpoints;
using RealEstate.Api.Helpers;
using RealEstate.Api.Services.Auth;
using RealEstate.Infrastructure;
using Serilog;
using Serilog.Formatting.Compact;

// Bootstrap logger pro zachycení chyb při startu (před konfigurací DI)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Spouštění RealEstate API …");

var builder = WebApplication.CreateBuilder(args);

// ─── Serilog ──────────────────────────────────────────────────────────────────
builder.Host.UseSerilog((ctx, services, config) =>
{
    config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithProcessId()
        .Enrich.WithThreadId();

    if (ctx.HostingEnvironment.IsDevelopment())
        config.WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    else
        config.WriteTo.Console(new CompactJsonFormatter());
});

// Kestrel: zvýšen limit těla požadavku pro upload fotek z prohlídky (výchozí 30 MB nestačí)
// 150 fotek × ~6 MB = ~900 MB → nastavujeme 1 GB pro jistotu
builder.WebHost.ConfigureKestrel(opts => opts.Limits.MaxRequestBodySize = 1_000_000_000);

// FormOptions: multipart body limit (výchozí 128 MB nestačí pro 150 fotek)
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(opts =>
{
    opts.MultipartBodyLengthLimit = 1_000_000_000; // 1 GB
    opts.ValueCountLimit           = 2_048;
});

// ─── API Key ──────────────────────────────────────────────────────────────────
// Načteme z prostředí, fallback na výchozí dev hodnotu.
// Mimo Development nastavit: API_KEY=<tajný klíč>
const string DefaultDevApiKey = "dev-key-change-me";
var apiKey = Environment.GetEnvironmentVariable("API_KEY") ?? DefaultDevApiKey;

// Výchozí dev klíč je povolený POUZE v Development.
// Pozor: podmínka záměrně NENÍ IsProduction() – nasazení běží s ASPNETCORE_ENVIRONMENT=Development,
// takže kontrola vázaná na Production by byla mrtvý kód a produkce by tiše jela s klíčem z gitu.
if (!builder.Environment.IsDevelopment() && apiKey == DefaultDevApiKey)
{
    throw new InvalidOperationException(
        $"API_KEY environment variable must be set outside Development. " +
        $"Default '{DefaultDevApiKey}' is not allowed (environment: {builder.Environment.EnvironmentName}).");
}

// Override connection string and scraper API base URL from environment variables
var dbHost = Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost";
var dbPort = Environment.GetEnvironmentVariable("DB_PORT") ?? "5432";
var dbName = Environment.GetEnvironmentVariable("DB_NAME") ?? "realestate_dev";
var dbUser = Environment.GetEnvironmentVariable("DB_USER") ?? "postgres";
var dbPassword = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "dev";
var scraperApiBaseUrl = Environment.GetEnvironmentVariable("SCRAPER_API_BASE_URL") ?? "http://localhost:8001";

Log.Information("SCRAPER_API_BASE_URL={ScraperApiBaseUrl}", scraperApiBaseUrl);

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

// ─── Problem Details ─────────────────────────────────────────────────────────
// Standardní RFC 7807 error response místo prázdných 500ek.
builder.Services.AddProblemDetails();

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

// ─── Health Checks ───────────────────────────────────────────────────────────
// /health/db (Postgres), /health/ollama, /health/scraper
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgres", tags: new[] { "db" })
    .AddUrlGroup(
        new Uri((builder.Configuration["Ollama:BaseUrl"] ?? "http://host.docker.internal:11434").TrimEnd('/') + "/api/tags"),
        name: "ollama",
        tags: new[] { "ai" },
        timeout: TimeSpan.FromSeconds(3))
    .AddUrlGroup(
        new Uri(scraperApiBaseUrl.TrimEnd('/') + "/"),
        name: "scraper",
        tags: new[] { "scraper" },
        timeout: TimeSpan.FromSeconds(3));

// ─── Rate Limiting ───────────────────────────────────────────────────────────
// Chrání drahé AI endpointy (RAG ask, embed) před zneužitím.
// Pozor: AddFixedWindowLimiter(name, …) vytváří JEDEN sdílený limiter pro všechny volající –
// komentář „per IP" tedy dřív nesouhlasil s kódem a jeden klient mohl vyčerpat limit všem.
// Proto AddPolicy + PartitionedRateLimiter s klíčem podle IP.
static string RateLimitPartitionKey(HttpContext ctx) =>
    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // RAG ask: 20 req/min na IP
    options.AddPolicy("rag-ask", ctx => RateLimitPartition.GetFixedWindowLimiter(
        RateLimitPartitionKey(ctx),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    // RAG embed: 5 req/min na IP (drahá operace)
    options.AddPolicy("rag-embed", ctx => RateLimitPartition.GetFixedWindowLimiter(
        RateLimitPartitionKey(ctx),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    // Login/registrace: 10 pokusů/min na IP – brzda proti hádání hesel
    options.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        RateLimitPartitionKey(ctx),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    // Leady (hypotéka): 5/min na IP
    options.AddPolicy("leads", ctx => RateLimitPartition.GetFixedWindowLimiter(
        RateLimitPartitionKey(ctx),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

// ─── Forwarded Headers ───────────────────────────────────────────────────────
// API běží za Traefikem – bez tohoto vidí aplikace jako klientskou IP kontejner proxy,
// takže by rate limiting výše partišnoval všechny návštěvníky do jednoho kbelíku.
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Traefik je v Docker síti s proměnlivou IP – seznam známých proxy nelze zadat staticky.
    // Bezpečné jen proto, že kontejner není z internetu dostupný přímo (UFW + DOCKER-USER).
    opts.KnownNetworks.Clear();
    opts.KnownProxies.Clear();
});


var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Bootstrap schématu záměrně NENÍ vázaný na Development.
// Dřív byl uvnitř if (IsDevelopment()) – přepnutí nasazení na Production by tak u prázdné
// databáze znamenalo start bez jediné tabulky. Řídí se výhradně SKIP_EF_MIGRATIONS.
{
    var skipMigrations = string.Equals(
        Environment.GetEnvironmentVariable("SKIP_EF_MIGRATIONS"),
        "true",
        StringComparison.OrdinalIgnoreCase);

    if (!skipMigrations)
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RealEstateDbContext>();

        // Retry loop: after macOS restart Docker may start API before postgres is ready
        const int maxDbRetries = 20;
        for (int attempt = 1; attempt <= maxDbRetries; attempt++)
        {
            try
            {
                // 🔥 Use EnsureCreatedAsync instead of MigrateAsync to avoid column naming conflicts
                await dbContext.Database.EnsureCreatedAsync();
                await DbInitializer.SeedAsync(dbContext, logger: scope.ServiceProvider.GetRequiredService<ILogger<Program>>());
                break;
            }
            catch (Exception ex) when (attempt < maxDbRetries)
            {
                Log.Warning("DB not ready (pokus {Attempt}/{Max}): {Msg}. Čekám 5s...",
                    attempt, maxDbRetries, ex.Message.Split('\n')[0]);
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }
}

// Musí být první middleware – ostatní (rate limiting, logování) potřebují skutečnou klientskou IP.
app.UseForwardedHeaders();

// Pozn.: UseHttpsRedirection() zde záměrně NENÍ.
// TLS terminuje Traefik na sudgate a přesměrování 80→443 dělá edge (entrypoint `web`).
// Uvnitř Docker sítě chodí požadavky po HTTP (App → http://realestate-api:8080) –
// redirect by je rozbil, jakmile se prostředí přepne na Production.

// Stažené fotky inzerátů (/uploads/listings/…) jsou dílem zdrojů – slouží jen klasifikaci,
// veřejně je nešíříme. UI zobrazuje původní URL zdroje. Fotky z prohlídek (/uploads/…/my_photos) zůstávají.
var serveStoredListingPhotos = builder.Configuration.GetValue<bool?>("Photos:ServeStoredListingPhotos") ?? false;
if (!serveStoredListingPhotos)
{
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/uploads/listings", StringComparison.OrdinalIgnoreCase)
            && path.Value!.Contains("/photos/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next(context);
    });
}

// Enable static files for local storage serving
app.UseStaticFiles();

app.UseCors();

// Global exception handler – vrací ProblemDetails JSON místo HTML stack trace
app.UseExceptionHandler();
app.UseStatusCodePages();

// HTTP request logging – metoda, cesta, status, čas obsluhy
app.UseSerilogRequestLogging();

// Rate limiter musí být před endpointy
app.UseRateLimiter();

// Identita volajícího (Bearer / X-Api-Key / anonym) – plní scoped ICurrentUser
app.UseMiddleware<CurrentUserMiddleware>(apiKey);

// ─── Endpoints ────────────────────────────────────────────────────────────────
// Health check – veřejně přístupný (používá Docker healthcheck a monitoring)
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
    .WithName("Health")
    .AllowAnonymous();

// Detailní health checks (per komponenta)
app.MapHealthChecks("/health/db", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("db"),
});
app.MapHealthChecks("/health/ollama", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ai"),
});
app.MapHealthChecks("/health/scraper", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("scraper"),
});

app.MapAuthEndpoints();
app.MapBillingEndpoints();
app.MapLeadEndpoints();
app.MapMarketEndpoints();
app.MapSavedSearchEndpoints();
app.MapListingEndpoints();
app.MapSourceEndpoints();
app.MapAnalysisEndpoints();
app.MapExportEndpoints();
app.MapDriveAuthEndpoints();
app.MapRagEndpoints();
app.MapSpatialEndpoints();
app.MapCadastreEndpoints();
app.MapPhotoEndpoints();
app.MapOllamaEndpoints();
app.MapLocalAnalysisEndpoints();

// ─── Scraping endpoints – chráněno API klíčem ─────────────────────────────────
app.MapScrapingEndpoints()
    .AddEndpointFilter(async (ctx, next) =>
    {
        // FixedTimeEquals – porovnání nezávislé na délce shodného prefixu (timing side-channel)
        static bool KeysMatch(string? provided, string expected) =>
            provided is not null
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(provided),
                System.Text.Encoding.UTF8.GetBytes(expected));

        if (!ctx.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var providedKey)
            || !KeysMatch(providedKey.ToString(), apiKey))
        {
            return Results.Problem(
                title: "Unauthorized",
                detail: "Platný API klíč je vyžadován v hlavičce X-Api-Key.",
                statusCode: StatusCodes.Status401Unauthorized);
        }
        return await next(ctx);
    });

    Log.Information("RealEstate API úspěšně spuštěno");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "RealEstate API selhalo při startu");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
