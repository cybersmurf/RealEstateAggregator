---
description: "Use when adding, modifying, or understanding chips/badges on listing cards in Listings.razor. Covers TargetScore ('Náš cíl'), PriceSignal, and SmartTags patterns."
applyTo: "src/RealEstate.App/Components/Pages/Listings.razor"
---

# Listing Card Badge System

## Card Content Order (top → bottom)

```
┌─────────────────────────────┐
│  [Photo / placeholder]       │  MudCardMedia 180px  
├─────────────────────────────┤  MudCardContent pa-2
│  Title (2-line clamp)        │  Typo.subtitle2
│  Price (N0 Kč / neuvedena)   │  Typo.h6
│  📍 LocationText             │  Typo.caption
│  [🎯 Náš cíl] OR [⭐ X/5 k] │  TargetScore chip (conditional)
│  [📈 PriceSignal chip]       │  conditional
│  [tag] [tag] [tag] [tag]     │  SmartTags (max 4, conditional)
├─────────────────────────────┤  MudCardActions pa-2
│  [logo/source]  [Typ ikona]  │
└─────────────────────────────┘
```

## 1. TargetScore Badge ("Náš cíl")

### Kritéria (konstanty v Listings.razor)
```csharp
private const string  TargetPropertyType   = "House";   // Typ nemovitosti
private const string  TargetOfferType      = "Sale";    // Typ nabídky
private const decimal TargetMaxPrice       = 10_000_000m;  // Cena do 10M
private const double  TargetMinAreaBuiltUp = 80.0;      // Plocha ≥ 80m²
// 5. kritérium: has_garden == true z AiNormalizedData
```

### Skórovací funkce
```csharp
private static (int score, int total) ScoreListingTarget(ListingSummaryDto item)
```

### Zobrazení
| Skóre | Badge | Barva | Ikona |
|-------|-------|-------|-------|
| 5/5 | "Náš cíl" | `Color.Success` (zelená) | `TrackChanges` |
| 3–4/5 | "X/5 kritérií" | `Color.Warning` (žlutá), `Variant.Outlined` | `Star` |
| < 3 | *(nezobrazí se)* | – | – |

### AiNormalizedData – struktura
```json
{
  "has_garden": true,
  "has_garage": false,
  "has_basement": true,
  "has_pool": null,
  "has_balcony": null,
  "has_terrace": true,
  "has_elevator": false,
  "has_storage": true,
  "energy_class": "B",
  "heating_type": "gas",
  "year_built": 2010,
  "floor": 5,
  "total_floors": 6,
  "is_single_floor": false,
  "extension_possible": null,
  "ownership": "personal"
}
```
Parsuj pomocí `System.Text.Json.JsonDocument` – NIKDY `Enum.Parse`, NIKDY blokovanie.

## 2. PriceSignal Chip

Zdroj: `item.PriceSignal` ∈ `{ "low", "fair", "high" }` (AI analýza ceny)

| Hodnota | Text | Barva | Ikona |
|---------|------|-------|-------|
| `"low"` | "Podhodnocená" | `Color.Success` | `TrendingDown` |
| `"fair"` | "Přiměřená" | `Color.Warning` | `TrendingFlat` |
| `"high"` | "Nadhodnocená" | `Color.Error` | `TrendingUp` |

Zobrazí se pouze pokud `item.PriceSignal` není null/prázdný.

## 3. SmartTags Chips

Zdroj: `item.SmartTags` – JSON pole stringů, např. `["sklep","zahrada","garáž"]`

- Max **4 tagy** na kartě (`cardTags.Take(4)`)
- `Variant.Outlined`, `Color.Secondary`, `font-size:0.7rem; height:20px`
- Parse: `ParseSmartTags(string? json)` → `List<string>`

## Pravidla pro přidávání nových badge

1. Pořadí: TargetScore → PriceSignal → SmartTags (od nejdůležitějšího k nejméně)
2. Vždy `T="string"` na `<MudChip>`
3. Podmíněné zobrazení – nikdy nezobrazuj prázdný chip
4. `Class="mt-1"` pro vertikální mezeru od předchozího prvku
5. Pro nový badge přidej odpovídající helper metodu jako `private static` do `@code`

## Pomocné metody

```csharp
ParseSmartTags(string? json)       → List<string>
ScoreListingTarget(ListingSummaryDto) → (int score, int total)
PriceColor(decimal? price)         → Color  (zelenomodro-červená škála dle výše ceny)
PropertyTypeInfo(string? type)     → (string Icon, Color Color)
TranslatePropertyType(string? t)   → string (House→Dům, Apartment→Byt, ...)
SourceLogoUrl(string? code)        → string? (cesta k SVG/PNG logu)
SourceColor(string? code)          → Color
```
