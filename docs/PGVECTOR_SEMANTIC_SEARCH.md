# PostgreSQL pgvector + Semantic Search - Real Estate Aggregator

**Datum**: 22. února 2026  
**Verze**: 1.0  
**Stack**: PostgreSQL 15+ • pgvector • OpenAI Embeddings • .NET 10 • Npgsql

---

## 📐 Architektura rozšířená o AI Semantic Search

```
┌─────────────────────────────────────────────────────────────────┐
│                    BLAZOR CLIENT                                 │
│  • Klasické filtry (lokalita, cena, typ)                        │
│  • Semantic search (volný text query)                           │
└───────────────────────┬─────────────────────────────────────────┘
                        │ HTTP POST
┌───────────────────────▼─────────────────────────────────────────┐
│                    .NET 10 API                                   │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │  EmbeddingService (OpenAI Client)                        │  │
│  │  • CreateAsync(text) → float[1536]                       │  │
│  └──────────────────────┬───────────────────────────────────┘  │
│                         │                                        │
│  ┌──────────────────────▼───────────────────────────────────┐  │
│  │  SemanticSearchService (Npgsql + pgvector-dotnet)        │  │
│  │  • SearchAsync(embedding, limit)                         │  │
│  │  • ORDER BY description_embedding <-> @query_embedding   │  │
│  └──────────────────────┬───────────────────────────────────┘  │
└────────────────────────┼────────────────────────────────────────┘
                         │
┌────────────────────────▼────────────────────────────────────────┐
│              POSTGRESQL 15+ with pgvector                        │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │  listings table                                          │  │
│  │  • description_embedding vector(1536)                    │  │
│  │  • HNSW index for fast similarity search                 │  │
│  └──────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────┘
```

---

## 🗄️ PostgreSQL Schema s pgvector

### 1. Instalace pgvector extension

```sql
-- V PostgreSQL 15+ s pgvector nainstalovaným
CREATE EXTENSION IF NOT EXISTS vector;
```

Verze a dokumentace: [pgvector GitHub](https://github.com/pgvector/pgvector)

---

### 2. Kompletní schema pro listings

```sql
-- Sources tabulka (realitní kanceláře)
CREATE TABLE sources (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code varchar(50) NOT NULL UNIQUE,
    name varchar(200) NOT NULL,
    base_url varchar(500),
    is_active boolean NOT NULL DEFAULT true,
    supports_url_scrape boolean NOT NULL DEFAULT false,
    supports_list_scrape boolean NOT NULL DEFAULT true,
    scraper_type varchar(50) DEFAULT 'Python', -- 'Python' nebo 'PlaywrightDotNet'
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_sources_code ON sources(code);
CREATE INDEX idx_sources_is_active ON sources(is_active);

-- Listings tabulka s pgvector
CREATE TABLE listings (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    source_id uuid NOT NULL REFERENCES sources(id) ON DELETE RESTRICT,
    external_id varchar(200),
    url text NOT NULL,
    title text NOT NULL,
    description text,
    
    -- Lokalita
    location_text varchar(500),
    region varchar(200),
    district varchar(200),
    municipality varchar(200),
    
    -- Typ nemovitosti
    property_type varchar(50) NOT NULL, -- 'House', 'Apartment', 'Land', 'Commercial'
    offer_type varchar(50) NOT NULL,    -- 'Sale', 'Rent'
    
    -- Parametry
    price numeric(18,2),
    price_note varchar(200),
    area_built_up numeric(18,2),
    area_land numeric(18,2),
    rooms integer,
    has_kitchen boolean,
    construction_type varchar(50), -- 'Brick', 'Panel', 'Wood', 'Stone'
    condition varchar(50),         -- 'New', 'VeryGood', 'Good', 'ToReconstruct', 'Demolished'
    
    -- Časové značky
    created_at_source timestamptz,
    updated_at_source timestamptz,
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    is_active boolean NOT NULL DEFAULT true,
    
    -- 🔥 PGVECTOR: Embedding description (OpenAI text-embedding-3-small = 1536 dimenzí)
    description_embedding vector(1536),
    
    -- Metadata
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    
    CONSTRAINT unique_listing_per_source UNIQUE (source_id, external_id)
);

-- Klasické indexy pro filtrování
CREATE INDEX idx_listings_source_id ON listings(source_id);
CREATE INDEX idx_listings_is_active ON listings(is_active);
CREATE INDEX idx_listings_region ON listings(region);
CREATE INDEX idx_listings_district ON listings(district);
CREATE INDEX idx_listings_municipality ON listings(municipality);
CREATE INDEX idx_listings_property_type ON listings(property_type);
CREATE INDEX idx_listings_offer_type ON listings(offer_type);
CREATE INDEX idx_listings_price ON listings(price) WHERE is_active = true;
CREATE INDEX idx_listings_first_seen_at ON listings(first_seen_at DESC);

-- Kompozitní index pro typické dotazy
CREATE INDEX idx_listings_active_region_price 
ON listings(is_active, region, price) 
WHERE is_active = true;

-- 🔥 PGVECTOR: HNSW index pro L2 distance (rychlé similarity search)
CREATE INDEX idx_listings_description_embedding_hnsw
ON listings
USING hnsw (description_embedding vector_l2_ops)
WITH (m = 16, ef_construction = 64);

-- Alternativa: IVFFlat (starší, pomalejší build, rychlejší search na velkých datech)
-- CREATE INDEX idx_listings_description_embedding_ivfflat
-- ON listings
-- USING ivfflat (description_embedding vector_l2_ops)
-- WITH (lists = 100);

-- Full-text search index (Czech language support)
CREATE INDEX idx_listings_fts 
ON listings 
USING gin(to_tsvector('czech', coalesce(title, '') || ' ' || coalesce(description, '')))
WHERE is_active = true;

-- Photos
CREATE TABLE listing_photos (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id uuid NOT NULL REFERENCES listings(id) ON DELETE CASCADE,
    url text NOT NULL,
    thumbnail_url text,
    display_order int NOT NULL DEFAULT 0,
    is_main boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_listing_photos_listing_id ON listing_photos(listing_id);
CREATE INDEX idx_listing_photos_display_order ON listing_photos(listing_id, display_order);

-- User states (like/dislike, notes)
CREATE TABLE user_listing_states (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id uuid NOT NULL REFERENCES listings(id) ON DELETE CASCADE,
    user_id varchar(100) NOT NULL DEFAULT 'default', -- Pro MVP bez auth
    status varchar(50) NOT NULL, -- 'New', 'Liked', 'Disliked', 'ToVisit', 'Visited'
    notes text,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    
    CONSTRAINT unique_user_listing UNIQUE (listing_id, user_id)
);

CREATE INDEX idx_user_listing_states_listing_id ON user_listing_states(listing_id);
CREATE INDEX idx_user_listing_states_status ON user_listing_states(status);

-- Analysis jobs (export to cloud for AI)
CREATE TABLE analysis_jobs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id uuid NOT NULL REFERENCES listings(id) ON DELETE CASCADE,
    status varchar(50) NOT NULL DEFAULT 'Pending', -- 'Pending', 'Running', 'Succeeded', 'Failed'
    cloud_url text,
    error_message text,
    started_at timestamptz,
    completed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_analysis_jobs_listing_id ON analysis_jobs(listing_id);
CREATE INDEX idx_analysis_jobs_status ON analysis_jobs(status);

-- Scrape runs (monitoring)
CREATE TABLE scrape_runs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    source_id uuid REFERENCES sources(id) ON DELETE SET NULL,
    scraper_type varchar(50) NOT NULL, -- 'Python', 'PlaywrightDotNet', 'UrlScrape'
    started_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    status varchar(50) NOT NULL DEFAULT 'Running', -- 'Running', 'Succeeded', 'Failed'
    new_count int NOT NULL DEFAULT 0,
    updated_count int NOT NULL DEFAULT 0,
    error_count int NOT NULL DEFAULT 0,
    error_message text
);

CREATE INDEX idx_scrape_runs_source_id ON scrape_runs(source_id);
CREATE INDEX idx_scrape_runs_started_at ON scrape_runs(started_at DESC);
```

---

### 3. Seed data

```sql
-- Vložení zdrojů
INSERT INTO sources (id, code, name, base_url, supports_url_scrape, supports_list_scrape, scraper_type)
VALUES 
    (gen_random_uuid(), 'REMAX', 'RE/MAX', 'https://www.remax-czech.cz', true, true, 'PlaywrightDotNet'),
    (gen_random_uuid(), 'MMR', 'M&M Reality', 'https://www.mmreality.cz', true, true, 'Python'),
    (gen_random_uuid(), 'PRODEJMETO', 'Prodejme.to', 'https://www.prodejme.to', true, true, 'Python');
```

---

## 🔧 .NET 10 Implementace

### 1. NuGet packages

```bash
dotnet add package Npgsql
dotnet add package Pgvector
dotnet add package OpenAI
```

---

### 2. Connection setup s pgvector

**Program.cs nebo ServiceCollectionExtensions.cs**:

```csharp
using Npgsql;
using Pgvector;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRealEstateDb(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connString = configuration.GetConnectionString("DefaultConnection")!;

        // NpgsqlDataSource s pgvector pluginem
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connString);
        dataSourceBuilder.UseVector(); // 🔥 pgvector-dotnet
        
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);

        return services;
    }
}
```

---

### 3. EmbeddingService - OpenAI Integration

**Services/EmbeddingService.cs**:

```csharp
using OpenAI;
using OpenAI.Embeddings;

namespace RealEstate.Api.Services;

public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

public sealed class EmbeddingService : IEmbeddingService
{
    private readonly OpenAIClient _client;
    private readonly string _model;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(IConfiguration config, ILogger<EmbeddingService> logger)
    {
        var apiKey = config["OpenAI:ApiKey"] 
            ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        
        _client = new OpenAIClient(apiKey);
        _model = config["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small";
        _logger = logger;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Empty text provided for embedding");
            return Array.Empty<float>();
        }

        try
        {
            var request = new EmbeddingsCreateRequest
            {
                Model = _model,
                Input = new[] { text }
            };

            var response = await _client.Embeddings.CreateAsync(request, cancellationToken: ct);
            var embedding = response.Data[0].Embedding.ToArray();

            _logger.LogDebug("Generated embedding with {Dimensions} dimensions", embedding.Length);
            return embedding;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate embedding for text: {Text}", 
                text[..Math.Min(100, text.Length)]);
            throw;
        }
    }
}
```

**appsettings.json**:

```json
{
  "OpenAI": {
    "ApiKey": "sk-...",
    "EmbeddingModel": "text-embedding-3-small"
  }
}
```

---

### 4. ListingEmbeddingRepository

**Infrastructure/Repositories/ListingEmbeddingRepository.cs**:

```csharp
using Npgsql;
using Pgvector;

namespace RealEstate.Infrastructure.Repositories;

public interface IListingEmbeddingRepository
{
    Task UpdateEmbeddingAsync(Guid listingId, float[] embedding, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SearchSimilarAsync(float[] queryEmbedding, int limit = 20, CancellationToken ct = default);
}

public sealed class ListingEmbeddingRepository : IListingEmbeddingRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ListingEmbeddingRepository> _logger;

    public ListingEmbeddingRepository(
        NpgsqlDataSource dataSource, 
        ILogger<ListingEmbeddingRepository> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task UpdateEmbeddingAsync(
        Guid listingId, 
        float[] embedding, 
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE listings
            SET description_embedding = @embedding,
                updated_at = now()
            WHERE id = @id;
        ", conn);

        cmd.Parameters.AddWithValue("id", listingId);
        cmd.Parameters.AddWithValue("embedding", new Vector(embedding));

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        
        _logger.LogInformation(
            "Updated embedding for listing {ListingId} (affected rows: {Affected})", 
            listingId, affected);
    }

    public async Task<IReadOnlyList<Guid>> SearchSimilarAsync(
        float[] queryEmbedding, 
        int limit = 20, 
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // L2 distance (<->) - čím menší, tím podobnější
        const string sql = @"
            SELECT id
            FROM listings
            WHERE is_active = true
              AND description_embedding IS NOT NULL
            ORDER BY description_embedding <-> @query_embedding
            LIMIT @limit;
        ";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("query_embedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("limit", limit);

        var results = new List<Guid>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(reader.GetGuid(0));
        }

        _logger.LogInformation(
            "Semantic search returned {Count} results for query embedding", 
            results.Count);

        return results;
    }
}
```

**Alternativa: Cosine similarity (`<=>`) místo L2 distance:**

```sql
-- Změň index:
CREATE INDEX idx_listings_description_embedding_hnsw
ON listings
USING hnsw (description_embedding vector_cosine_ops); -- cosine místo l2

-- Změň query:
ORDER BY description_embedding <=> @query_embedding
```

---

### 5. SemanticSearchService

**Services/SemanticSearchService.cs**:

```csharp
namespace RealEstate.Api.Services;

public interface ISemanticSearchService
{
    Task<IReadOnlyList<ListingSummaryDto>> SearchAsync(
        string query, 
        int limit = 20, 
        CancellationToken ct = default);
}

public sealed class SemanticSearchService : ISemanticSearchService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IListingEmbeddingRepository _embeddingRepository;
    private readonly IListingRepository _listingRepository;
    private readonly ILogger<SemanticSearchService> _logger;

    public SemanticSearchService(
        IEmbeddingService embeddingService,
        IListingEmbeddingRepository embeddingRepository,
        IListingRepository listingRepository,
        ILogger<SemanticSearchService> logger)
    {
        _embeddingService = embeddingService;
        _embeddingRepository = embeddingRepository;
        _listingRepository = listingRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ListingSummaryDto>> SearchAsync(
        string query, 
        int limit = 20, 
        CancellationToken ct = default)
    {
        _logger.LogInformation("Semantic search for query: {Query}", query);

        // 1. Generate embedding from query
        var queryEmbedding = await _embeddingService.EmbedAsync(query, ct);

        // 2. Find similar listings by embedding
        var listingIds = await _embeddingRepository.SearchSimilarAsync(
            queryEmbedding, 
            limit, 
            ct);

        if (!listingIds.Any())
        {
            _logger.LogWarning("No similar listings found for query: {Query}", query);
            return Array.Empty<ListingSummaryDto>();
        }

        // 3. Load full listing entities
        var listings = await _listingRepository.GetByIdsAsync(listingIds, ct);

        // 4. Map to DTOs (preserve order from similarity search)
        var result = listingIds
            .Select(id => listings.FirstOrDefault(l => l.Id == id))
            .Where(l => l != null)
            .Select(MapToSummaryDto)
            .ToList();

        return result;
    }

    private static ListingSummaryDto MapToSummaryDto(Listing listing)
    {
        // Same mapping as in ListingService
        return new ListingSummaryDto
        {
            Id = listing.Id,
            SourceName = listing.Source.Name,
            SourceCode = listing.Source.Code,
            Title = listing.Title,
            LocationText = listing.LocationText ?? string.Empty,
            Price = listing.Price,
            AreaBuiltUp = (double?)listing.AreaBuiltUp,
            AreaLand = (double?)listing.AreaLand,
            FirstSeenAt = listing.FirstSeenAt,
            UserStatus = listing.UserStates.FirstOrDefault()?.Status ?? "New"
        };
    }
}
```

---

### 6. API Endpoints

**Endpoints/SemanticSearchEndpoints.cs**:

```csharp
using Microsoft.AspNetCore.Mvc;

namespace RealEstate.Api.Endpoints;

public sealed class SemanticSearchRequest
{
    public string Query { get; set; } = string.Empty;
    public int Limit { get; set; } = 20;
}

public static class SemanticSearchEndpoints
{
    public static IEndpointRouteBuilder MapSemanticSearchEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/semantic")
            .WithTags("Semantic Search")
            .WithOpenApi();

        group.MapPost("/search", SearchListings)
            .WithName("SemanticSearchListings")
            .WithSummary("Semantic search for listings using natural language query");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<ListingSummaryDto>>> SearchListings(
        [FromBody] SemanticSearchRequest request,
        [FromServices] ISemanticSearchService service,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return TypedResults.Ok<IReadOnlyList<ListingSummaryDto>>(
                Array.Empty<ListingSummaryDto>());
        }

        var results = await service.SearchAsync(request.Query, request.Limit, ct);
        return TypedResults.Ok(results);
    }
}
```

**Program.cs registrace**:

```csharp
// Services
builder.Services.AddSingleton<IEmbeddingService, EmbeddingService>();
builder.Services.AddScoped<IListingEmbeddingRepository, ListingEmbeddingRepository>();
builder.Services.AddScoped<ISemanticSearchService, SemanticSearchService>();

// ...

// Endpoints
app.MapSemanticSearchEndpoints();
```

---

## 🎨 Blazor UI pro Semantic Search

**Pages/Listings.razor**:

```razor
@page "/listings"
@inject HttpClient Http

<MudPaper Class="pa-4">
    <MudStack Spacing="2">
        
        <!-- Semantic Search -->
        <MudCard Elevation="2" Class="pa-3">
            <MudCardContent>
                <MudText Typo="Typo.h6">🔍 Chytré vyhledávání (AI)</MudText>
                <MudTextField @bind-Value="_semanticQuery" 
                              Label="Popište, co hledáte..." 
                              Variant="Variant.Outlined"
                              Placeholder="např. 'chci chalupu k rekonstrukci se studnou a velkým pozemkem'"
                              Lines="2"
                              Class="mt-2" />
                <MudButton Color="Color.Secondary" 
                           Variant="Variant.Filled" 
                           OnClick="SemanticSearchAsync"
                           StartIcon="@Icons.Material.Filled.AutoAwesome"
                           Class="mt-2">
                    AI Hledání
                </MudButton>
            </MudCardContent>
        </MudCard>

        <MudDivider />

        <!-- Klasické filtry (existující) -->
        <MudExpansionPanels>
            <MudExpansionPanel Text="Pokročilé filtry">
                <!-- ... existující filtry ... -->
            </MudExpansionPanel>
        </MudExpansionPanels>

        <!-- Výsledky -->
        <!-- ... existující tabulka ... -->
    </MudStack>
</MudPaper>

@code {
    private string? _semanticQuery;
    private List<ListingSummaryDto> _items = new();
    private bool _isLoading = false;

    private async Task SemanticSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(_semanticQuery))
            return;

        _isLoading = true;
        StateHasChanged();

        try
        {
            var request = new { Query = _semanticQuery, Limit = 50 };
            var response = await Http.PostAsJsonAsync("api/semantic/search", request);
            response.EnsureSuccessStatusCode();

            var results = await response.Content
                .ReadFromJsonAsync<List<ListingSummaryDto>>();
            
            _items = results ?? new();
        }
        catch (Exception ex)
        {
            // TODO: Snackbar notification
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }
}
```

---

## ⚡ Background Job: Generate Embeddings

**Background/Services/EmbeddingGeneratorService.cs**:

```csharp
namespace RealEstate.Background.Services;

public sealed class EmbeddingGeneratorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EmbeddingGeneratorService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public EmbeddingGeneratorService(
        IServiceProvider serviceProvider,
        ILogger<EmbeddingGeneratorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Embedding Generator Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await GenerateEmbeddingsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in embedding generation cycle");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task GenerateEmbeddingsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        
        var listingRepo = scope.ServiceProvider
            .GetRequiredService<IListingRepository>();
        var embeddingService = scope.ServiceProvider
            .GetRequiredService<IEmbeddingService>();
        var embeddingRepo = scope.ServiceProvider
            .GetRequiredService<IListingEmbeddingRepository>();

        // Načti listings bez embeddingu
        var listingsWithoutEmbedding = await listingRepo
            .GetListingsWithoutEmbeddingAsync(limit: 100, ct);

        _logger.LogInformation(
            "Generating embeddings for {Count} listings", 
            listingsWithoutEmbedding.Count);

        foreach (var listing in listingsWithoutEmbedding)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                // Combine title + location + description
                var text = $"{listing.Title}\n{listing.LocationText}\n{listing.Description}";
                
                var embedding = await embeddingService.EmbedAsync(text, ct);
                
                await embeddingRepo.UpdateEmbeddingAsync(listing.Id, embedding, ct);
                
                _logger.LogDebug("Generated embedding for listing {Id}", listing.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    "Failed to generate embedding for listing {Id}", 
                    listing.Id);
            }

            // Rate limiting (OpenAI má limity)
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        _logger.LogInformation("Embedding generation cycle completed");
    }
}
```

**Program.cs registrace**:

```csharp
builder.Services.AddHostedService<EmbeddingGeneratorService>();
```

---

## 🧪 Testování

### Unit test: EmbeddingService

```csharp
[Fact]
public async Task EmbedAsync_ValidText_ReturnsEmbedding()
{
    // Arrange
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string>
        {
            ["OpenAI:ApiKey"] = "sk-test-key",
            ["OpenAI:EmbeddingModel"] = "text-embedding-3-small"
        })
        .Build();

    var service = new EmbeddingService(config, NullLogger<EmbeddingService>.Instance);

    // Act
    var embedding = await service.EmbedAsync("Test listing description");

    // Assert
    embedding.Should().NotBeEmpty();
    embedding.Length.Should().Be(1536); // text-embedding-3-small dimension
}
```

### Integration test: Semantic search

```csharp
[Fact]
public async Task SearchAsync_WithQuery_ReturnsSimilarListings()
{
    // Arrange
    await SeedTestListingsWithEmbeddings();

    // Act
    var results = await _semanticSearchService.SearchAsync(
        "dům se studnou v jižní Moravě", 
        limit: 10);

    // Assert
    results.Should().NotBeEmpty();
    results.Should().AllSatisfy(r => 
        r.LocationText.Should().Contain("Morava"));
}
```

---

## 📊 Performance Optimizations

### 1. Index tuning

HNSW parametry pro balance mezi rychlostí a přesností:

```sql
-- Rychlejší search, menší přesnost
CREATE INDEX idx_fast 
ON listings 
USING hnsw (description_embedding vector_l2_ops)
WITH (m = 8, ef_construction = 32);

-- Pomalejší search, větší přesnost (default)
CREATE INDEX idx_accurate 
ON listings 
USING hnsw (description_embedding vector_l2_ops)
WITH (m = 16, ef_construction = 64);

-- Pro mega přesnost (produkce)
CREATE INDEX idx_production 
ON listings 
USING hnsw (description_embedding vector_l2_ops)
WITH (m = 32, ef_construction = 128);
```

### 2. Filtrování před similarity search

Kombinuj klasické filtry s pgvector:

```sql
-- Nejdřív omez region/cenu, pak semantic search na podmnožině
SELECT id, title, description
FROM listings
WHERE is_active = true
  AND region = 'Jihomoravský kraj'
  AND price BETWEEN 2000000 AND 5000000
  AND description_embedding IS NOT NULL
ORDER BY description_embedding <-> :query_embedding
LIMIT 20;
```

V .NET:

```csharp
public async Task<IReadOnlyList<Guid>> SearchSimilarWithFiltersAsync(
    float[] queryEmbedding,
    string? region = null,
    decimal? priceMin = null,
    decimal? priceMax = null,
    int limit = 20,
    CancellationToken ct = default)
{
    var sql = new StringBuilder(@"
        SELECT id
        FROM listings
        WHERE is_active = true
          AND description_embedding IS NOT NULL
    ");

    if (!string.IsNullOrWhiteSpace(region))
        sql.Append(" AND region = @region");
    
    if (priceMin.HasValue)
        sql.Append(" AND price >= @priceMin");
    
    if (priceMax.HasValue)
        sql.Append(" AND price <= @priceMax");

    sql.Append(@"
        ORDER BY description_embedding <-> @query_embedding
        LIMIT @limit;
    ");

    await using var conn = await _dataSource.OpenConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand(sql.ToString(), conn);

    cmd.Parameters.AddWithValue("query_embedding", new Vector(queryEmbedding));
    cmd.Parameters.AddWithValue("limit", limit);
    
    if (!string.IsNullOrWhiteSpace(region))
        cmd.Parameters.AddWithValue("region", region);
    
    if (priceMin.HasValue)
        cmd.Parameters.AddWithValue("priceMin", priceMin.Value);
    
    if (priceMax.HasValue)
        cmd.Parameters.AddWithValue("priceMax", priceMax.Value);

    // ... execute and return
}
```

### 3. Caching embeddings

Pro často používané query (např. user saved searches):

```csharp
public sealed class EmbeddingCache
{
    private readonly IMemoryCache _cache;
    private readonly IEmbeddingService _embeddingService;

    public async Task<float[]> GetOrCreateAsync(string text, CancellationToken ct)
    {
        var cacheKey = $"embedding:{text.GetHashCode()}";

        if (_cache.TryGetValue<float[]>(cacheKey, out var cached))
            return cached!;

        var embedding = await _embeddingService.EmbedAsync(text, ct);
        
        _cache.Set(cacheKey, embedding, TimeSpan.FromHours(24));
        
        return embedding;
    }
}
```

---

## 🎯 Real Estate Specific Tips

### 1. Composite embeddings

Pro lepší výsledky generuj embedding z více polí:

```csharp
private static string BuildEmbeddingText(Listing listing)
{
    var parts = new List<string>
    {
        listing.Title,
        listing.LocationText ?? string.Empty,
        listing.Description ?? string.Empty,
        $"Cena: {listing.Price:N0} Kč",
        $"Plocha: {listing.AreaBuiltUp} m²",
        $"Pozemek: {listing.AreaLand} m²",
        $"Typ: {listing.PropertyType}",
        $"Stav: {listing.Condition}"
    };

    return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
```

### 2. User preference embedding

Ulož embedding uživatelských preferencí:

```sql
CREATE TABLE user_preferences (
    user_id varchar(100) PRIMARY KEY,
    preference_text text NOT NULL,
    preference_embedding vector(1536),
    updated_at timestamptz NOT NULL DEFAULT now()
);
```

Pak semantic search s user embedding:

```csharp
var userPreference = await _userPrefRepo.GetByUserIdAsync(userId);
var results = await _embeddingRepo.SearchSimilarAsync(
    userPreference.Embedding, 
    limit: 50);
```

### 3. Hybrid search (keyword + semantic)

Kombinuj full-text search s pgvector:

```sql
SELECT 
    id, 
    title,
    ts_rank(to_tsvector('czech', title || ' ' || description), to_tsquery('czech', :keywords)) as text_score,
    1 - (description_embedding <-> :query_embedding) as semantic_score,
    (ts_rank(...) * 0.3 + (1 - (...<->...)) * 0.7) as hybrid_score
FROM listings
WHERE is_active = true
  AND (
      to_tsvector('czech', title || ' ' || description) @@ to_tsquery('czech', :keywords)
      OR description_embedding <-> :query_embedding < 0.5
  )
ORDER BY hybrid_score DESC
LIMIT 20;
```

---

## 📚 Reference

- [pgvector GitHub](https://github.com/pgvector/pgvector)
- [pgvector-dotnet NuGet](https://www.nuget.org/packages/Pgvector/)
- [OpenAI Embeddings API](https://platform.openai.com/docs/guides/embeddings)
- [Supabase pgvector Guide](https://supabase.com/blog/openai-embeddings-postgres-vector)
- [TigerData pgvector Tutorial](https://www.tigerdata.com/blog/postgresql-as-a-vector-database-using-pgvector)
- [PostgreSQL Azure AI Integration](https://learn.microsoft.com/en-us/azure/postgresql/azure-ai/generative-ai-azure-openai)

---

**Konec dokumentu**  
Pro SQL schema viz [DATABASE_SCHEMA.md](DATABASE_SCHEMA.md)  
Pro implementation tasks viz [BACKLOG.md](BACKLOG.md) → Sprint "Semantic Search Implementation"
