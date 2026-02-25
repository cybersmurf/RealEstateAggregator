using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Rag;
using RealEstate.Api.Services;
using RealEstate.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace RealEstate.Api.Endpoints;

public static class RagEndpoints
{
    public static IEndpointRouteBuilder MapRagEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/listings/{id:guid}")
            .WithTags("RAG / Analyses");

        // ── GET: seznam analýz inzerátu ────────────────────────────────────────
        group.MapGet("/analyses", GetAnalyses)
            .WithName("GetListingAnalyses")
            .WithSummary("Vrátí všechny uložené analýzy pro inzerát");

        // ── POST: uložení nové analýzy (+ embedding) ──────────────────────────
        group.MapPost("/analyses", SaveAnalysis)
            .WithName("SaveListingAnalysis")
            .WithSummary("Uloží analýzu textu + vygeneruje embedding");

        // ── DELETE: smazání analýzy ────────────────────────────────────────────
        group.MapDelete("/analyses/{analysisId:guid}", DeleteAnalysis)
            .WithName("DeleteListingAnalysis")
            .WithSummary("Smaže analýzu");

        // ── POST: RAG dotaz nad konkrétním inzerátem ──────────────────────────
        group.MapPost("/ask", AskListing)
            .WithName("AskListing")
            .WithSummary("RAG dotaz nad analýzami konkrétního inzerátu");

        // ── POST: RAG dotaz přes všechny inzeráty ──────────────────────────────
        app.MapPost("/api/rag/ask", AskGeneral)
            .WithTags("RAG / Analyses")
            .WithName("AskGeneral")
            .WithSummary("RAG dotaz přes všechny uložené analýzy");

        // ── POST: embed popis jednoho inzerátu ────────────────────────────────
        group.MapPost("/embed-description", EmbedDescription)
            .WithName("EmbedListingDescription")
            .WithSummary("Embeduje popis inzerátu jako 'auto' analýzu (idempotentní)");

        // ── POST: bulk embed popisů inzerátů ──────────────────────────────────
        app.MapPost("/api/rag/embed-descriptions", BulkEmbedDescriptions)
            .WithTags("RAG / Analyses")
            .WithName("BulkEmbedDescriptions")
            .WithSummary("Batch embed popisů inzerátů bez 'auto' analýzy");

        // ── GET: embedding status ──────────────────────────────────────────────
        app.MapGet("/api/rag/status", GetRagStatus)
            .WithTags("RAG / Analyses")
            .WithName("GetRagStatus")
            .WithSummary("Stav RAG (počet embeddingů, jestli je OpenAI nakonfigurováno)");

        return app;
    }

    // ─── HANDLERS ──────────────────────────────────────────────────────────────

    private static async Task<IResult> GetAnalyses(
        Guid id,
        IRagService rag,
        CancellationToken ct)
    {
        var analyses = await rag.GetAnalysesAsync(id, ct);
        return TypedResults.Ok(analyses);
    }

    private static async Task<IResult> SaveAnalysis(
        Guid id,
        [FromBody] SaveAnalysisRequestDto dto,
        IRagService rag,
        RealEstateDbContext db,
        CancellationToken ct)
    {
        var exists = await db.Listings.AnyAsync(l => l.Id == id, ct);
        if (!exists)
            return TypedResults.NotFound(new { error = $"Inzerát {id} nenalezen" });

        if (string.IsNullOrWhiteSpace(dto.Content))
            return TypedResults.BadRequest(new { error = "Content nesmí být prázdný" });

        var result = await rag.SaveAnalysisAsync(id, dto.Content, dto.Source, dto.Title, ct);
        return TypedResults.Created($"/api/listings/{id}/analyses/{result.Id}", result);
    }

    private static async Task<IResult> DeleteAnalysis(
        Guid id,
        Guid analysisId,
        IRagService rag,
        CancellationToken ct)
    {
        var deleted = await rag.DeleteAnalysisAsync(analysisId, ct);
        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<IResult> AskListing(
        Guid id,
        [FromBody] AskRequestDto dto,
        IRagService rag,
        RealEstateDbContext db,
        CancellationToken ct)
    {
        var exists = await db.Listings.AnyAsync(l => l.Id == id, ct);
        if (!exists)
            return TypedResults.NotFound(new { error = $"Inzerát {id} nenalezen" });

        if (string.IsNullOrWhiteSpace(dto.Question))
            return TypedResults.BadRequest(new { error = "Question nesmí být prázdný" });

        var response = await rag.AskListingAsync(id, dto.Question, dto.TopK, ct);
        return TypedResults.Ok(response);
    }

    private static async Task<IResult> AskGeneral(
        [FromBody] AskRequestDto dto,
        IRagService rag,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Question))
            return TypedResults.BadRequest(new { error = "Question nesmí být prázdný" });

        var response = await rag.AskGeneralAsync(dto.Question, dto.TopK, ct);
        return TypedResults.Ok(response);
    }

    private static async Task<IResult> EmbedDescription(
        Guid id,
        IRagService rag,
        RealEstateDbContext db,
        CancellationToken ct)
    {
        var exists = await db.Listings.AnyAsync(l => l.Id == id, ct);
        if (!exists)
            return TypedResults.NotFound(new { error = $"Inzerát {id} nenalezen" });

        var (analysis, alreadyExists) = await rag.EmbedListingDescriptionAsync(id, ct);
        if (alreadyExists)
            return TypedResults.Ok(new { message = "Auto analýza již existuje", alreadyExists = true });

        return TypedResults.Created($"/api/listings/{id}/analyses/{analysis!.Id}", analysis);
    }

    private static async Task<IResult> BulkEmbedDescriptions(
        [FromBody] BulkEmbedRequestDto? dto,
        IRagService rag,
        CancellationToken ct)
    {
        var limit = dto?.Limit is > 0 ? dto.Limit : 100;
        var count = await rag.BulkEmbedDescriptionsAsync(limit, ct);
        return TypedResults.Ok(new { processed = count, message = $"Zpracováno {count} inzerátů" });
    }

    private static async Task<IResult> GetRagStatus(
        IEmbeddingService embeddingService,
        RealEstateDbContext db,
        CancellationToken ct)
    {
        var totalAnalyses = await db.ListingAnalyses.CountAsync(ct);
        var withEmbedding = await db.ListingAnalyses.CountAsync(a => a.Embedding != null, ct);
        var listingsWithAnalyses = await db.ListingAnalyses
            .Select(a => a.ListingId)
            .Distinct()
            .CountAsync(ct);

        return TypedResults.Ok(new
        {
            openAiConfigured = embeddingService.IsConfigured,
            totalAnalyses,
            withEmbedding,
            withoutEmbedding = totalAnalyses - withEmbedding,
            listingsWithAnalyses
        });
    }
}
public record BulkEmbedRequestDto(int Limit = 100);