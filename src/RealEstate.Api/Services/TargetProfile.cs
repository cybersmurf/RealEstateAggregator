using System.Linq.Expressions;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Api.Services;

/// <summary>
/// „Náš cíl" – Petrova kritéria hledání domu (1. 10. 2026): samostatně stojící dům od 4+1 se zahradou
/// v okresech Znojmo / Brno-venkov do ~10 mil. Kč, bez nutné rekonstrukce. Jedna definice pro filtr
/// (<see cref="ScoreExpression"/> – počítá se v SQL), čip v seznamu a MCP (<see cref="Evaluate"/>).
/// Do 20. 10. 2026 to počítala jen stránka inzerátů u sebe (5 kritérií, cena 7,5 mil., koridor obcí).
/// </summary>
public static class TargetProfile
{
    public const decimal MaxPrice = 10_000_000m;
    public const double MinAreaBuiltUp = 100;
    public const int MinRooms = 4;
    public const double MinLandForGarden = 300;

    public static readonly string[] Districts = ["Znojmo", "Brno-venkov"];

    /// <summary>Stavy, které znamenají rekonstrukci nebo rozestavěnost – banka nedá hypotéku.</summary>
    public static readonly string[] BadConditions =
    [
        "Před rekonstrukcí", "Nutná rekonstrukce", "K rekonstrukci", "V rekonstrukci",
        "K demolici", "Špatný", "Ve výstavbě", "Projekt",
    ];

    public static readonly string[] Labels =
    [
        "Dům na prodej",
        "Cena do 10 mil. Kč",
        "Užitná plocha od 100 m²",
        "Aspoň 4 pokoje",
        "Okres Znojmo / Brno-venkov",
        "Zahrada",
        "Samostatně stojící",
        "Bez nutné rekonstrukce",
    ];

    public static int Total => Labels.Length;

    /// <summary>Počet splněných kritérií jako SQL výraz (CASE WHEN … součet) – pro filtr a řazení přes LinqKit Invoke.</summary>
    public static readonly Expression<Func<Listing, int>> ScoreExpression = l =>
        (l.PropertyType == PropertyType.House && l.OfferType == OfferType.Sale ? 1 : 0)
        + (l.Price != null && l.Price <= MaxPrice ? 1 : 0)
        + (l.AreaBuiltUp != null && l.AreaBuiltUp >= MinAreaBuiltUp ? 1 : 0)
        + (l.Rooms != null && l.Rooms >= MinRooms ? 1 : 0)
        + (l.District != null && Districts.Contains(l.District) ? 1 : 0)
        + ((l.AreaLand != null && l.AreaLand >= MinLandForGarden) || (l.SmartTags != null && l.SmartTags.Contains("Zahrada")) ? 1 : 0)
        + (l.HousePosition == "detached" || (l.HousePositionListed != null && l.HousePositionListed.StartsWith("Samostatn")) ? 1 : 0)
        + (l.Condition == null || !BadConditions.Contains(l.Condition) ? 1 : 0);

    public sealed record Evaluation(int Met, int Total, List<string> Missing);

    /// <summary>Totéž co <see cref="ScoreExpression"/> v paměti, navíc se seznamem nesplněných kritérií.</summary>
    public static Evaluation Evaluate(Listing l)
    {
        bool[] met =
        [
            l.PropertyType == PropertyType.House && l.OfferType == OfferType.Sale,
            l.Price is not null && l.Price <= MaxPrice,
            l.AreaBuiltUp is not null && l.AreaBuiltUp >= MinAreaBuiltUp,
            l.Rooms is not null && l.Rooms >= MinRooms,
            l.District is not null && Districts.Contains(l.District),
            (l.AreaLand is not null && l.AreaLand >= MinLandForGarden) || (l.SmartTags is not null && l.SmartTags.Contains("Zahrada")),
            l.HousePosition == "detached" || (l.HousePositionListed is not null && l.HousePositionListed.StartsWith("Samostatn")),
            l.Condition is null || !BadConditions.Contains(l.Condition),
        ];
        var missing = Labels.Where((_, i) => !met[i]).ToList();
        return new Evaluation(met.Count(m => m), Total, missing);
    }
}
