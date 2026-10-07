namespace RealEstate.App.Services;

/// <summary>Poloha domu (listings.house_position) → český název; pořadí = pořadí ve výběru filtru.</summary>
public static class HousePositionLabels
{
    public static readonly (string Value, string Label)[] Options =
    [
        ("detached", "samostatný"),
        ("semi_detached", "přisazený z jedné strany"),
        ("terraced", "řadový"),
        ("corner", "rohový"),
    ];

    public static string Label(string value)
        => Options.FirstOrDefault(o => o.Value == value).Label ?? value;
}
