namespace RealEstate.Api.Contracts.Export;

/// <summary>Jedna fotka od makléře v Drive podsložce Fotky_od_maklere (zobrazuje se přímo z Drivu).</summary>
public record BrokerPhotoDto(
    string Id,
    string Name,
    string ViewUrl,
    string ThumbnailUrl,
    string DownloadUrl,
    string MimeType,
    long? SizeBytes
);

/// <summary>Kategorie = podsložka (např. 05_Kotel) s popisem z FOTKY_OD_MAKLERE.md.</summary>
public record BrokerPhotoCategoryDto(
    string Folder,
    string Label,
    string? Description,
    IReadOnlyList<BrokerPhotoDto> Photos
);

/// <summary>Fotky od makléře k inzerátu: kategorie, poznámky a odkaz na složku.</summary>
public record BrokerPhotosDto(
    string FolderId,
    string FolderUrl,
    string? Source,
    string? Notes,
    int Total,
    IReadOnlyList<BrokerPhotoCategoryDto> Categories
);
