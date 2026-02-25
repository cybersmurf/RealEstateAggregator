namespace RealEstate.Domain.Entities;

/// <summary>
/// Uložená prostorová oblast pro filtrování inzerátů.
/// Podporuje: koridor (buffer kolem trasy), bounding box, vlastní polygon.
/// </summary>
public class SpatialArea
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string AreaType { get; set; } = "corridor";  // corridor | bbox | polygon | circle

    /// <summary>WKT geometrie v EPSG:4326 (WGS84)</summary>
    public string GeomWkt { get; set; } = null!;

    // Metadata koridoru
    public string? StartCity { get; set; }
    public string? EndCity { get; set; }
    public int? BufferMeters { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
