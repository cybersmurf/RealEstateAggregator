namespace RealEstate.Api.Contracts.Cadastre;

/// <summary>Katastrální data vrácená API endpointem.</summary>
public record ListingCadastreDto(
    Guid Id,
    Guid ListingId,
    long? RuianKod,
    string? ParcelNumber,
    string? LvNumber,
    int? LandAreaM2,
    string? LandType,
    string? OwnerType,
    string? EncumbrancesJson,
    string AddressSearched,
    string? CadastreUrl,
    string FetchStatus,         // pending / found / not_found / error
    string? FetchError,
    DateTime FetchedAt
);

/// <summary>Request pro manuální uložení katastrálních dat.</summary>
public record SaveCadastreDataRequest(
    string? ParcelNumber,
    string? LvNumber,
    int? LandAreaM2,
    string? LandType,
    string? OwnerType,
    string? EncumbrancesJson    // JSON string: [{"type":"...","description":"...","who":"..."}]
);

/// <summary>Výsledek bulk RUIAN vyhledávání.</summary>
public record BulkRuianResultDto(
    int Total,
    int Found,
    int NotFound,
    int Error,
    int Skipped,
    string Message
);
