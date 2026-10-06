namespace RealEstate.App.Services;

/// <summary>
/// Jediný zdroj pravdy pro mapování source code → logo URL.
/// Eliminuje duplicity z Home.razor a Listings.razor.
/// </summary>
public sealed class SourceLogoProvider
{
    private static readonly Dictionary<string, string> _logoMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "REMAX",         "/images/logos/REMAX.svg" },
        { "MMR",           "/images/logos/MMR.svg" },
        { "PRODEJMETO",    "/images/logos/PRODEJMETO.svg" },
        { "LEXAMO",        "/images/logos/LEXAMO.svg" },
        { "PREMIAREALITY", "/images/logos/PREMIAREALITY.svg" },
        { "CENTURY21",     "/images/logos/CENTURY21.svg" },
        { "SREALITY",      "/images/logos/SREALITY.png" },
        { "IDNES",         "/images/logos/IDNES.svg" },
        { "DELUXREALITY",  "/images/logos/DELUXREALITY.png" },
        { "HVREALITY",     "/images/logos/HVREALITY.png" },
        { "ZNOJMOREALITY", "/images/logos/ZNOJMOREALITY.png" },
        { "NEMZNOJMO",     "/images/logos/NEMZNOJMO.png" },
        { "REALINGO",      "/images/logos/REALINGO.png" },
        { "REALMIX",       "/images/logos/REALMIX.svg" },
        { "REALITYCECHY",  "/images/logos/REALITYCECHY.svg" },
        { "REALITYMIX",    "/images/logos/REALITYMIX.svg" },
        { "REALCITY",      "/images/logos/REALCITY.svg" },
        { "BEZREALITKY",   "/images/logos/BEZREALITKY.svg" },
        { "OKDRAZBY",      "/images/logos/OKDRAZBY.svg" },
        { "ULOVDOMOV",     "/images/logos/ULOVDOMOV.svg" },
        { "REAS",          "/images/logos/REAS.svg" },
        { "BAZOS",         "/images/logos/BAZOS.svg" },
    };

    public string? GetLogoUrl(string? code)
        => code is not null && _logoMap.TryGetValue(code, out var url) ? url : null;

    public IReadOnlyDictionary<string, string> All => _logoMap;
}
