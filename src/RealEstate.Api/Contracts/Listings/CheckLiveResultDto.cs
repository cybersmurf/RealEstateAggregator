namespace RealEstate.Api.Contracts.Listings;

/// <param name="IsLive">true = inzerát existuje, false = 404/410, null = nelze ověřit</param>
/// <param name="HttpStatus">HTTP status code vrácený zdrojem</param>
/// <param name="Error">Chybová zpráva (timeout, DNS, …)</param>
public sealed record CheckLiveResultDto(bool? IsLive, int? HttpStatus, string? Error);
