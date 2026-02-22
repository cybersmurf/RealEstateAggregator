namespace RealEstate.Api.Contracts.Sources;

public sealed class SourceDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty; // "REMAX", "MMR", ...
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
