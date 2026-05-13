---
description: "Use when adding, modifying, or understanding chips/badges on listing cards in Listings.razor. Covers TargetScore ('Náš cíl'), Přízemní, PriceSignal, and SmartTags patterns."
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
│  [🏡 Přízemní]               │  IsSingleFloor chip (conditional)
│  [📈 PriceSignal chip]       │  conditional
│  [tag] [tag] [tag] [tag]     │  SmartTags (max 4, conditional)
├─────────────────────────────┤  MudCardActions pa-2
│  [logo/source]  [Typ ikona]  │
└─────────────────────────────┘
```

## 1. TargetScore Badge ("Náš cíl")

### Kritéria (konstanty v Listings.razor)
```csharp
private const decimal TargetMaxPrice       = 7_500_000m;   // 1. Cena ≤ 7,5M
private const double  TargetMinAreaBuiltUp = 100.0;        // 2. Plocha ≥ 100 m²
private const int     TargetMinRooms       = 4;            // 3. Dispozice 4+kk a výše
// 4. Lokalita: IsOnTargetCorridor() – HashSet<string> _targetMunicipalities
//    osa Rajhrad → Pohořelice → Miroslav → Lechovice → Znojmo ±6 km
// 5. Zahrada: has_garden == true z AiNormalizedData
```

**⚠️ PropertyType a OfferType NEJSOU součástí skóre** – jsou to filtry v UI, ne kritéria.

### Skórovací funkce
```csharp
private static (int score, int total) ScoreListingTarget(ListingSummaryDto item)
// Pořadí: cena → plocha → pokoje → lokalita → zahrada
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
Parsuj pomocí `System.Text.Json.JsonDocument` – NIKDY `Enum.Parse`, NIKDY blokování.

## 2. Přízemní Badge

Zobrazuje se **nezávisle** na TargetScore – za TargetScore chipem, před PriceSignal.

```csharp
private static bool IsSingleFloor(ListingSummaryDto item)
// Čte is_single_floor z AiNormalizedData
// true  → chip; false nebo null → chip se NEZOBRAZÍ
```

| Podmínka | Badge | Barva | Varianta | Ikona |
|----------|-------|-------|----------|-------|
| `is_single_floor == true` | "Přízemní" | `Color.Info` (modrá) | `Outlined` | `Cottage` |

**Jak AI detekuje `is_single_floor`:** klíčová slova v popisu: `bungalov`, `přízemní`,
`přízemí`, `bez schodů`, `parter`, `1NP`, `1. NP`, `přízemní dům`.

**Manuální oprava** (když AI nezjistí z textu):
```sql
UPDATE re_realestate.listings
SET ai_normalized_data = ai_normalized_data || '{"is_single_floor": true}'::jsonb
WHERE id = '<uuid>';
```

## 3. PriceSignal Chip

Zdroj: `item.PriceSignal` ∈ `{ "low", "fair", "high" }` (AI analýza ceny)

| Hodnota | Text | Barva | Ikona |
|---------|------|-------|-------|
| `"low"` | "Podhodnocená" | `Color.Success` | `TrendingDown` |
| `"fair"` | "Přiměřená" | `Color.Warning` | `TrendingFlat` |
| `"high"` | "Nadhodnocená" | `Color.Error` | `TrendingUp` |

Zobrazí se pouze pokud `item.PriceSignal` není null/prázdný.

## 4. SmartTags Chips

Zdroj: `item.SmartTags` – JSON pole stringů, např. `["sklep","zahrada","garáž"]`

- Max **4 tagy** na kartě (`cardTags.Take(4)`)
- `Variant.Outlined`, `Color.Secondary`, `font-size:0.7rem; height:20px`
- Parse: `ParseSmartTags(string? json)` → `List<string>`

## Pravidla pro přidávání nových badge

1. Pořadí: **TargetScore → Přízemní → PriceSignal → SmartTags** (od nejdůležitějšího k nejméně)
2. Vždy `T="string"` na `<MudChip>`
3. Podmíněné zobrazení – nikdy nezobrazuj prázdný chip
4. `Class="mt-1"` pro vertikální mezeru od předchozího prvku
5. Pro nový badge přidej odpovídající helper metodu jako `private static` do `@code`

## Pomocné metody

```csharp
ParseSmartTags(string? json)         → List<string>
ScoreListingTarget(ListingSummaryDto) → (int score, int total)
IsSingleFloor(ListingSummaryDto)      → bool   (is_single_floor z AiNormalizedData)
IsOnTargetCorridor(ListingSummaryDto) → bool   (Municipality nebo LocationText v HashSet)
PriceColor(decimal? price)            → Color  (zelenomodro-červená škála dle výše ceny)
PropertyTypeInfo(string? type)        → (string Icon, Color Color)
TranslatePropertyType(string? t)      → string (House→Dům, Apartment→Byt, ...)
SourceLogoUrl(string? code)           → string? (cesta k SVG/PNG logu)
SourceColor(string? code)             → Color
```
