using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using RealEstate.Api.Contracts.Rag;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>
/// RAG (Retrieval-Augmented Generation) service.
/// Ukládá analýzy inzerátů s embeddingy a odpovídá na otázky přes pgvector + OpenAI.
/// </summary>
public sealed class RagService(
    RealEstateDbContext db,
    IEmbeddingService embedding,
    ILogger<RagService> logger) : IRagService
{
    // ─── SAVE ──────────────────────────────────────────────────────────────────

    public async Task<ListingAnalysisDto> SaveAnalysisAsync(
        Guid listingId, string content, string source, string? title, CancellationToken ct)
    {
        // Vygeneruj embedding (může vrátit null pokud OpenAI není nakonfigurováno)
        float[]? floats = null;
        if (embedding.IsConfigured)
        {
            floats = await embedding.GetEmbeddingAsync(content, ct);
            if (floats is null)
                logger.LogWarning("Embedding generation failed for listing {Id} – analysis saved without vector", listingId);
        }

        var analysis = new ListingAnalysis
        {
            Id = Guid.NewGuid(),
            ListingId = listingId,
            Content = content,
            Source = source,
            Title = title,
            Embedding = floats is not null ? new Vector(floats) : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.ListingAnalyses.Add(analysis);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Saved analysis {Id} for listing {ListingId} (source={Source}, hasEmbedding={HasEmb})",
            analysis.Id, listingId, source, floats is not null);

        return ToDto(analysis);
    }

    // ─── EMBED DESCRIPTION ─────────────────────────────────────────────────────

    public async Task<(ListingAnalysisDto? Analysis, bool AlreadyExists)> EmbedListingDescriptionAsync(
        Guid listingId, CancellationToken ct)
    {
        // Idempotence: přeskoč pokud "auto" analýza již existuje
        var alreadyExists = await db.ListingAnalyses
            .AnyAsync(a => a.ListingId == listingId && a.Source == "auto", ct);
        if (alreadyExists)
        {
            logger.LogInformation("Listing {Id} already has 'auto' analysis – skipping embed", listingId);
            return (null, true);
        }

        var listing = await db.Listings
            .FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing is null)
            return (null, false);

        // Sestavíme strukturovaný text pro embedding – všechna dostupná data
        var text = BuildListingText(listing);
        var title = $"Popis inzerátu – {listing.Title}";

        var dto = await SaveAnalysisAsync(listingId, text, "auto", title, ct);
        return (dto, false);
    }

    public async Task<int> BulkEmbedDescriptionsAsync(int limit, CancellationToken ct)
    {
        // Najdi listing_id bez "auto" analýzy (aktivní inzeráty)
        var processedIds = db.ListingAnalyses
            .Where(a => a.Source == "auto")
            .Select(a => a.ListingId);

        var listings = await db.Listings
            .Where(l => l.IsActive && !processedIds.Contains(l.Id))
            .OrderBy(l => l.FirstSeenAt)
            .Take(limit)
            .ToListAsync(ct);

        int count = 0;
        foreach (var listing in listings)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var text = BuildListingText(listing);
                await SaveAnalysisAsync(listing.Id, text, "auto", $"Popis inzerátu – {listing.Title}", ct);
                count++;
                logger.LogInformation("BulkEmbed: embedded listing {Id} ({Count}/{Total})", listing.Id, count, listings.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "BulkEmbed: failed for listing {Id}", listing.Id);
            }
        }
        return count;
    }

    /// <summary>Sestaví strukturovaný text z inzerátu pro embedding.</summary>
    private static string BuildListingText(RealEstate.Domain.Entities.Listing l)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {l.Title}");
        sb.AppendLine();
        sb.AppendLine($"Typ: {l.PropertyType} | Nabídka: {l.OfferType}");
        if (l.Price.HasValue)
            sb.AppendLine($"Cena: {l.Price.Value:N0} Kč{(l.PriceNote is not null ? $" ({l.PriceNote})" : "")}");
        sb.AppendLine($"Lokalita: {l.LocationText}");
        if (l.Municipality is not null) sb.AppendLine($"Obec: {l.Municipality}");
        if (l.District is not null) sb.AppendLine($"Okres: {l.District}");
        if (l.Disposition is not null) sb.AppendLine($"Dispozice: {l.Disposition}");
        if (l.AreaBuiltUp.HasValue) sb.AppendLine($"Plocha: {l.AreaBuiltUp} m²");
        if (l.AreaLand.HasValue) sb.AppendLine($"Pozemek: {l.AreaLand} m²");
        if (l.Condition is not null) sb.AppendLine($"Stav: {l.Condition}");
        if (l.ConstructionType is not null) sb.AppendLine($"Konstrukce: {l.ConstructionType}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(l.Description))
        {
            sb.AppendLine("## Popis");
            sb.AppendLine(l.Description);
        }
        return sb.ToString();
    }

    // ─── ASK LISTING ───────────────────────────────────────────────────────────

    public async Task<AskResponseDto> AskListingAsync(Guid listingId, string question, int topK, CancellationToken ct)
    {
        var chunks = await FindSimilarAsync(question, topK, ct, listingId);

        if (chunks.Count == 0)
        {
            // Fallback: vrátíme všechny analýzy bez embeddingového řazení
            var allAnalyses = await db.ListingAnalyses
                .Where(a => a.ListingId == listingId)
                .OrderByDescending(a => a.CreatedAt)
                .Take(topK)
                .ToListAsync(ct);

            if (allAnalyses.Count == 0)
                return new AskResponseDto(
                    "Žádné analýzy pro tento inzerát nejsou uloženy.",
                    [], HasEmbeddings: false);

            // Použijeme text bez embeddingového řazení
            chunks = allAnalyses.Select(a => (Analysis: a, Similarity: 0.0)).ToList();
        }

        // Načti základní info o inzerátu
        var listing = await db.Listings
            .Where(l => l.Id == listingId)
            .Select(l => new { l.Title, l.LocationText, l.Price, l.PropertyType, l.OfferType })
            .FirstOrDefaultAsync(ct);

        var systemPrompt = BuildSystemPrompt();
        var userMessage = BuildUserMessage(
            question,
            listingContext: listing is null ? null
                : $"{listing.Title} | {listing.LocationText} | {listing.Price:N0} Kč | {listing.PropertyType} | {listing.OfferType}",
            chunks.Select(c => c.Analysis.Content).ToList());

        var answer = await embedding.ChatAsync(systemPrompt, userMessage, ct);

        return new AskResponseDto(
            answer,
            chunks.Select(c => ToChunkDto(c.Analysis, c.Similarity)).ToList(),
            HasEmbeddings: chunks.Any(c => c.Analysis.Embedding is not null));
    }

    // ─── ASK GENERAL ───────────────────────────────────────────────────────────

    public async Task<AskResponseDto> AskGeneralAsync(string question, int topK, CancellationToken ct)
    {
        var chunks = await FindSimilarAsync(question, topK, ct, listingId: null);

        if (chunks.Count == 0)
            return new AskResponseDto(
                "Není nalezena žádná relevantní analýza. Nejdříve je potřeba uložit analýzy inzerátů.",
                [], HasEmbeddings: false);

        var systemPrompt = BuildSystemPrompt();
        var userMessage = BuildUserMessage(question, listingContext: null,
            chunks.Select(c => c.Analysis.Content).ToList());

        var answer = await embedding.ChatAsync(systemPrompt, userMessage, ct);

        return new AskResponseDto(
            answer,
            chunks.Select(c => ToChunkDto(c.Analysis, c.Similarity)).ToList(),
            HasEmbeddings: true);
    }

    // ─── GET / DELETE ──────────────────────────────────────────────────────────

    public async Task<List<ListingAnalysisDto>> GetAnalysesAsync(Guid listingId, CancellationToken ct)
    {
        var analyses = await db.ListingAnalyses
            .Where(a => a.ListingId == listingId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
        return analyses.Select(ToDto).ToList();
    }

    public async Task<bool> DeleteAnalysisAsync(Guid analysisId, CancellationToken ct)
    {
        var analysis = await db.ListingAnalyses.FindAsync([analysisId], ct);
        if (analysis is null) return false;
        db.ListingAnalyses.Remove(analysis);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ─── HELPERS ───────────────────────────────────────────────────────────────

    /// <summary>
    /// pgvector L2 similarity search přes raw SQL (FromSqlInterpolated) – kompatibilní s EF Core 10.
    /// listingId=null → cross-listing search přes všechny analýzy.
    /// </summary>
    private async Task<List<(ListingAnalysis Analysis, double Similarity)>> FindSimilarAsync(
        string query, int topK, CancellationToken ct, Guid? listingId)
    {
        if (!embedding.IsConfigured)
            return [];

        var queryFloats = await embedding.GetEmbeddingAsync(query, ct);
        if (queryFloats is null) return [];

        var queryVector = new Vector(queryFloats);

        // FromSqlInterpolated zajistí správnou parametrizaci vektoru přes Npgsql
        var results = listingId.HasValue
            ? await db.ListingAnalyses
                .FromSqlInterpolated($"""
                    SELECT * FROM re_realestate.listing_analyses
                    WHERE listing_id = {listingId.Value}
                      AND embedding IS NOT NULL
                    ORDER BY embedding <-> {queryVector}
                    LIMIT {topK}
                    """)
                .AsNoTracking()
                .ToListAsync(ct)
            : await db.ListingAnalyses
                .FromSqlInterpolated($"""
                    SELECT * FROM re_realestate.listing_analyses
                    WHERE embedding IS NOT NULL
                    ORDER BY embedding <-> {queryVector}
                    LIMIT {topK}
                    """)
                .AsNoTracking()
                .ToListAsync(ct);

        return results
            .Select(a => (a, Similarity: CosineSimilarity(a.Embedding!, queryFloats)))
            .ToList();
    }

    private static string BuildSystemPrompt() =>
        """
        Jsi AI asistent pomáhající s analýzou nemovitostí v České republice.
        Odpovídáš v češtině. Vycházíš výhradně z poskytnutého kontextu (analýz inzerátů).
        Pokud kontext neobsahuje dostatečné informace, řekni to otevřeně.
        Buď konkrétní, věcný a stručný. Při odkazování na zdroje uveď jejich pořadí [1], [2], atd.
        """;

    private static string BuildUserMessage(string question, string? listingContext, List<string> contextChunks)
    {
        var sb = new System.Text.StringBuilder();

        if (listingContext is not null)
            sb.AppendLine($"## Inzerát\n{listingContext}\n");

        sb.AppendLine("## Uložené analýzy (kontext)");
        for (int i = 0; i < contextChunks.Count; i++)
            sb.AppendLine($"\n### Analýza [{i + 1}]\n{contextChunks[i]}");

        sb.AppendLine($"\n## Dotaz\n{question}");
        return sb.ToString();
    }

    private static ListingAnalysisDto ToDto(ListingAnalysis a) => new(
        a.Id, a.ListingId, a.Content, a.Title, a.Source,
        HasEmbedding: a.Embedding is not null,
        a.CreatedAt, a.UpdatedAt);

    private static AnalysisChunkDto ToChunkDto(ListingAnalysis a, double similarity) => new(
        a.Id, a.Title,
        ContentExcerpt: a.Content.Length > 300 ? a.Content[..300] + "…" : a.Content,
        a.Source, similarity, a.CreatedAt);

    /// <summary>Cosine similarity v paměti (Vector → float[]).</summary>
    private static double CosineSimilarity(Vector vectorA, float[] b)
    {
        var a = vectorA.Memory.ToArray();
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        return magA == 0 || magB == 0 ? 0.0 : dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
