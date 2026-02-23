namespace RealEstate.App.Models;

/// <summary>
/// DTO pro zobrazení zdroje inzerátů ve filtrech a chipech.
/// Odpovídá odpovědi API /api/sources.
/// </summary>
public sealed record SourceDto(Guid Id, string Code, string Name, string BaseUrl, bool IsActive);
