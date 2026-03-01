namespace RealEstate.Api.Contracts.Export;

/// <summary>Soubor v Google Drive složce.</summary>
public record DriveFileDto(
    string Id,
    string Name,
    string WebViewLink,
    DateTime? ModifiedAt
);
