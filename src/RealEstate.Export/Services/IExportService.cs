using RealEstate.Domain.Entities;

namespace RealEstate.Export.Services;

/// <summary>
/// Interface pro exportování inzerátů do různých formátů
/// </summary>
public interface IExportService
{
    Task<string> ExportListingAsync(Listing listing, ExportFormat format, CancellationToken ct = default);
    Task<string> ExportListingsAsync(IEnumerable<Listing> listings, ExportFormat format, CancellationToken ct = default);
}

public enum ExportFormat
{
    Markdown,
    Json,
    Html
}
