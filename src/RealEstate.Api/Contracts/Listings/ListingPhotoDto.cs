namespace RealEstate.Api.Contracts.Listings;

public sealed class ListingPhotoDto
{
    public Guid Id { get; set; }
    public string OriginalUrl { get; set; } = string.Empty;
    public string? StoredUrl { get; set; }
    public int Order { get; set; }
    public string? AiDescription { get; set; }

    // Ollama Vision klasifikace
    public string? PhotoCategory { get; set; }
    public string? PhotoDescription { get; set; }  // česky, 1-2 věty
    public string? PhotoLabels { get; set; }   // JSON: ["mold","water_damage",...]
    public bool DamageDetected { get; set; }
    public decimal? ClassificationConfidence { get; set; }
    public bool IsClassified => PhotoCategory != null;
}
