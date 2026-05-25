namespace RealEstate.Api.Contracts.Listings;

public sealed record PriceHistoryDto(
    decimal? Price,
    DateTimeOffset RecordedAt,
    string Source
);
