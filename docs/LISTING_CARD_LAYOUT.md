# Listing Card Layout System

Dokumentace vizuální struktury karet inzerátů v `Listings.razor`.

---

## 1. Grid Layout

Karty jsou renderovány v `MudGrid` s responzivními breakpointy:

```razor
<MudGrid Spacing="3">
    @foreach (var item in _listings)
    {
        <MudItem xs="12" sm="6" md="4" lg="3">
            <!-- MudCard -->
        </MudItem>
    }
</MudGrid>
```

| Breakpoint | Sloupce | Šířka karty |
|-----------|---------|-------------|
| xs (<600px) | 1 | 100% |
| sm (≥600px) | 2 | ~50% |
| md (≥960px) | 3 | ~33% |
| lg (≥1280px)| 4 | ~25% |

---

## 2. Anatomie karty

```
┌──────────────────────────────────────────────────────┐
│  MudCard (Elevation=2, hover: Elevation=6, pointer)  │
│                                                      │
│  ┌──────────────────────────────────────────────┐    │
│  │  MudCardMedia                                │    │
│  │  height: 180px                               │    │
│  │  Image: první dostupná fotka                 │    │
│  │  (originální nebo stažená)                   │    │
│  │  Placeholder: gradient 120px                 │    │
│  │  (LinearGradient, ikona uprostřed)           │    │
│  └──────────────────────────────────────────────┘    │
│                                                      │
│  MudCardContent (pa-2)                               │
│  ┌──────────────────────────────────────────────┐    │
│  │  Title           Typo.subtitle2, 2-line clamp│    │
│  │  Price            Typo.h6, bold, barva dle   │    │
│  │                  výše ceny (PriceColor())     │    │
│  │                  "N0 Kč" nebo "Cena neuvedena"│    │
│  │  📍 LocationText  Typo.caption, šedá          │    │
│  │  ─────────────────────────────────────────── │    │
│  │  [🎯 Náš cíl]     Score chip (pokud score=5) │    │
│  │  [⭐ X/5 kritérií] Score chip (pokud score≥3)│    │  │  [🏠 Přízemní]    Přízemní dům (pokud true)  │    ││  │  [📈 PriceSignal]  AI cenový signál (pokud ≠null)│  │
│  │  [tag] [tag] [tag] [tag]  SmartTags (max 4)  │    │
│  └──────────────────────────────────────────────┘    │
│                                                      │
│  MudCardActions (pa-2)                               │
│  ┌──────────────────────────────────────────────┐    │
│  │  [Logo/ikona zdroje]   [Ikona typu nemovitosti]   │
│  └──────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────┘
```

---

## 3. Badge systém

### 3.1 TargetScore ("Náš cíl")

Implementace: `ScoreListingTarget(ListingSummaryDto item) → (int score, int total)`

Hodnotí 5 kritérií:

| # | Kritérium | Zdroj pole |
|---|-----------|-----------|
| 1 | `Price ≤ 7 500 000 Kč` | `item.Price` |
| 2 | `AreaBuiltUp ≥ 100 m²` | `item.AreaBuiltUp` |
| 3 | `Rooms ≥ 4` (4+kk a výše) | `item.Rooms` |
| 4 | Lokalita na ose Rajhrad–Pohořelice–Lechovice–Znojmo | `item.Municipality` / `item.LocationText` |
| 5 | `has_garden == true` | `item.AiNormalizedData` (JSON) |

Zobrazení:

| Skóre | Badge text | Barva | Varianta | Ikona |
|-------|-----------|-------|---------|-------|
| 5/5 | "Náš cíl" | `Color.Success` (zelená) | Filled | `TrackChanges` |
| 3–4/5 | "X/5 kritérií" | `Color.Warning` (žlutá) | Outlined | `Star` |
| 0–2/5 | *(nezobrazí se)* | – | – | – |

> Konstanty lze upravit přímo v `Listings.razor`:  
> `TargetMaxPrice` (7 500 000), `TargetMinAreaBuiltUp` (100), `TargetMinRooms` (4), `_targetMunicipalities` (HashSet)

**⚠️ PropertyType a OfferType NEJSOU součástí skóre** – jsou to filtry v UI, ne kritéria `ScoreListingTarget`.

### 3.2 PriceSignal

Zdroj: `item.PriceSignal` ∈ `{ "low", "fair", "high", null }`  
Generuje AI job `OllamaTextService.GeneratePriceSignalAsync()`.

| Hodnota | Text | Barva | Ikona |
|---------|------|-------|-------|
| `"low"` | "Podhodnocená" | `Color.Success` | `TrendingDown` |
| `"fair"` | "Přiměřená" | `Color.Warning` | `TrendingFlat` |
| `"high"` | "Nadhodnocená" | `Color.Error` | `TrendingUp` |
| `null` / `""` | *(nezobrazí se)* | – | – |

### 3.3 SmartTags

Zdroj: `item.SmartTags` – JSON pole stringů, např. `["sklep","zahrada","garáž","terasa"]`  
Generuje AI job `OllamaTextService.GenerateSmartTagsAsync()`.

```razor
@* Max 4 tagy, Outlined/Secondary, malé písmo *@
<MudChip T="string" Size="Size.Small" Variant="Variant.Outlined"
         Color="Color.Secondary" Style="font-size:0.7rem;height:20px">
    @tag
</MudChip>
```

Pomocná metoda: `ParseSmartTags(string? json) → List<string>`

### 3.4 Přízemní badge

Zdroj: `IsSingleFloor(item)` – čte `is_single_floor` z `AiNormalizedData` JSON.

| Podmínka | Badge text | Barva | Varianta | Ikona |
|----------|-----------|-------|---------|-------|
| `is_single_floor == true` | "Přízemní" | `Color.Info` (modrá) | Outlined | `Cottage` |
| `is_single_floor == false` nebo `null` | *(nezobrazí se)* | – | – | – |

**Pozice:** za TargetScore chipem, před PriceSignal.

**Jak AI detekuje `is_single_floor`:** klíčová slova v popisu inzerátu:
`bungalov`, `přízemní`, `přízemí`, `bez schodů`, `parter`, `1NP`, `1. NP`, `přízemní dům`

**Manuální oprava** (když AI nezjistí z textu):
```sql
UPDATE re_realestate.listings
SET ai_normalized_data = ai_normalized_data || '{"is_single_floor": true}'::jsonb
WHERE id = '<uuid>';
```

Implementace v `OllamaTextService.cs` (prompt):
```
is_single_floor: true if the house is entirely on one level (bungalov, přízemní, přízemí,
bez schodů, parter, 1NP, 1. NP, přízemní dům, no stairs). Also true if the listing explicitly
mentions that all rooms are on one floor. Use null if uncertain.
```

---

## 4. Fotografie

Priorita načítání:
1. `item.StoredUrl` (stažená lokální kopie ve volume `uploads_data`)
2. `item.PhotoUrl` (originální URL ze scraperu)
3. Gradient placeholder s ikonou

```csharp
// Výpočet URL pro zobrazení
var photoUrl = !string.IsNullOrEmpty(item.StoredUrl) ? item.StoredUrl : item.PhotoUrl;
```

Placeholder: CSS `linear-gradient` 120px výška, centrovaná `Home` ikona z MudBlazor.

---

## 5. Cena – barevné kódování (PriceColor)

```csharp
private static Color PriceColor(decimal? price) => price switch
{
    null         => Color.Default,
    < 2_000_000  => Color.Success,   // zelená – < 2M
    < 5_000_000  => Color.Info,      // modrá – 2–5M
    < 8_000_000  => Color.Warning,   // žlutá – 5–8M
    _            => Color.Error      // červená – > 8M
};
```

---

## 6. Typ nemovitosti (PropertyTypeInfo)

```csharp
private static (string Icon, Color Color) PropertyTypeInfo(string? type) => type switch
{
    "House"      => (Icons.Material.Filled.House,      Color.Primary),
    "Apartment"  => (Icons.Material.Filled.Apartment,  Color.Secondary),
    "Land"       => (Icons.Material.Filled.Landscape,  Color.Success),
    "Cottage"    => (Icons.Material.Filled.Cottage,    Color.Warning),
    "Commercial" => (Icons.Material.Filled.Business,   Color.Error),
    _            => (Icons.Material.Filled.Home,       Color.Default)
};
```

---

## 7. Filtrační panel

Panel (vlevo od gridu nebo jako drawer na mobilu) obsahuje:

| Kontrolka | Typ | Vazba |
|-----------|-----|-------|
| Hledat (fulltext) | `MudTextField` | `_filterSearchText` |
| Obec | `MudTextField` | `_filterMunicipality` |
| Typ nemovitosti | `MudSelect<PropertyType?>` | `_filterPropertyType` |
| Typ nabídky | `MudSelect<OfferType?>` | `_filterOfferType` |
| Cena od | `MudNumericField<decimal?>` | `_filterPriceMin` |
| Cena do | `MudNumericField<decimal?>` | `_filterPriceMax` |
| Plocha od (m²) | `MudNumericField<double?>` | `_filterAreaMin` |
| Řadit dle | `MudSelect<string>` | `_sortBy` |
| Můj stav | `MudSelect<string?>` | `_filterUserStatus` |
| Zdroje | `MudChipSet` | `_selectedSourceCodes` |

**Quick filtry** (MudChips):  
`❤️ Oblíbené` → filtr UserStatus=Liked  
`🚗 K návštěvě` → filtr UserStatus=ToVisit

**Stav filtru** je persistován přes `ProtectedSessionStorage` (přežívá reload stránky).

---

## 8. Přepínač Karta / Tabulka

```razor
<MudToggleIconButton @bind-Toggled="_isTableView"
    Icon="@Icons.Material.Filled.ViewModule"   @* karty *@
    ToggledIcon="@Icons.Material.Filled.ViewList" @* tabulka *@/>
```

Tabulka (`_isTableView=true`) zobrazuje `MudDataGrid` se sloupci:
Titul | Cena | Typ | Nabídka | Plocha | Lokalita | Zdroj | Stav | Akce

---

## 9. Stavový chip (UserStatus)

Zobrazuje se v pravém horním rohu karty (absolutní pozice) nebo jako chip v tabulce.

| Status | Barva | Ikona | Text |
|--------|-------|-------|------|
| `Liked` | Success | Favorite | Oblíbený |
| `ToVisit` | Warning | DirectionsCar | K návštěvě |
| `Visited` | Info | DoneAll | Navštíveno |
| `Disliked` | Error | ThumbDown | Nezajímavý |
| `New` / null | Default | – | *(chip nezobrazí)* |

---

## 10. Výkonnostní poznámky

- `AsNoTracking()` na všech dotazech pro listing cards
- Stránkování: `pageSize` default 24, max 96 (volitelné v UI)
- `CancellationToken` při každém `LoadListingsAsync()` – implementuje `IDisposable`
- Lazy load fotek: `loading="lazy"` na `<img>` tagu uvnitř `MudCardMedia`
