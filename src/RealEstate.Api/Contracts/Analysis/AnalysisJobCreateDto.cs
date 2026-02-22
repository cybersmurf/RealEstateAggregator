namespace RealEstate.Api.Contracts.Analysis;

public sealed class AnalysisJobCreateDto
{
    public string StorageProvider { get; set; } = "GoogleDrive"; // or "OneDrive"
}
