# REMAX Scraping Architecture & Configuration

**Verze**: 1.0  
**Datum**: 22. února 2026  
**Status**: Production Ready

---

## 📋 Obsah

1. [Architektura](#architektura)
2. [Komponenty](#komponenty)
3. [RemaxScrapingProfileDto](#remaxscrapingprofiledto)
4. [Příklady](#příklady)
5. [API Reference](#api-reference)
6. [Limitace & Edge Cases](#limitace--edge-cases)
7. [Troubleshooting](#troubleshooting)

---

## 🏗️ Architektura

### High-level flow

```
┌─────────────────────────────────────────────────────────┐
│                      HTTP CLIENT                         │
│  POST /api/scraping-playwright/run                       │
│  Body: { sourceCodes: ["REMAX"], remaxProfile: {...} }  │
└────────────────────┬────────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────────┐
│        PlaywrightScrapingOrchestrator                    │
│  • ParseNutAndParseProfile                              │
│  • Route ke RemaxScrapingService                         │
└────────────────────┬────────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────────┐
│          RemaxScrapingService                            │
│  • BuildSearchUrl(profile) → REMAX search URL            │
│  • Create Playwright browser instance                    │
│  • Pass profil & URL do RemaxImporter                    │
└────────────────────┬────────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────────┐
│  RemaxImporter (Main Orchestrator)                       │
│  ├─ RemaxListScraper.ScrapeListAsync(url)               │
│  │  └─ Returns: IReadOnlyList<RemaxListItem>            │
│  │     • Title, DetailUrl, LocationText, Price          │
│  │                                                       │
│  └─ For each item:                                       │
│     ├─ RemaxDetailScraper.ScrapeDetailAsync(item)       │
│     │  └─ Returns: RemaxDetailResult                     │
│     │     • Full title, description, area, photos       │
│     │                                                   │
│     └─ MapToListingEntity() → Listing                   │
│        └─ ListingRepository.UpsertAsync()               │
│           → PostgreSQL INSERT/UPDATE                    │
└────────────────────┬────────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────────┐
│  PostgreSQL Database                                    │
│  • re_realestate.listings (upserted)                    │
│  • re_realestate.listing_photos (cascade insert)        │
└─────────────────────────────────────────────────────────┘
```

### Class diagram

```
┌─────────────────────────────────┐
│  RemaxScrapingProfileDto        │
│  (Configuration)                │
├─────────────────────────────────┤
│ • Name: string                  │
│ • DirectUrl?: string            │
│ • RegionId?: int                │
│ • DistrictId?: int              │
│ • CityName?: string             │
│ • PropertyTypeMask: int         │
│ • PriceMin/Max?: long           │
│ • SearchText?: string           │
│ • SearchType: int (1|2)         │
│ • OfferType: string             │
│ • MaxPages: int                 │
└──────────────┬──────────────────┘
               │ used by
               ▼
┌─────────────────────────────────┐
│  RemaxScrapingService           │
│  • BuildSearchUrl()             │
│  • RunAsync(profile)            │
└──────────────┬──────────────────┘
               │ creates instance
               ▼
┌─────────────────────────────────┐
│  RemaxImporter                  │
│  • ImportAsync()                │
├─────────────────────────────────┤
│ Dependencies:                   │
│ • IBrowser (Playwright)         │
│ • IListingRepository            │
│ • ILogger<RemaxImporter>        │
└──────────────┬──────────────────┘
               │
        ┌──────┴──────┐
        │             │
        ▼             ▼
┌──────────────────┐ ┌──────────────────┐
│ RemaxListScraper │ │RemaxDetailScraper│
│ • FindCards()    │ │ • ParseDetail()  │
│ • ExtractUrl()   │ │ • ParseArea()    │
│ • ParsePrice()   │ │ • ParsePhotos()  │
└──────────────────┘ └──────────────────┘
        │                    │
        └────────┬───────────┘
                 ▼
        ┌───────────────────┐
        │ ListingRepository │
        │ • UpsertAsync()   │
        └───────────────────┘
```

---

## 🔧 Komponenty

### 1. RemaxScrapingService

**Odpovědnost**: Orchestrace scrapingu podle profilu

**Klíčové metody**:

```csharp
public async Task RunAsync(RemaxScrapingProfileDto profile, CancellationToken ct)
{
    // 1. Normalizuje profil
    string searchUrl = profile.DirectUrl ?? BuildSearchUrl(profile);
    
    // 2. Vytvoří Playwright browser
    var playwright = await Playwright.CreateAsync();
    var browser = await playwright.Chromium.LaunchAsync(...);
    
    // 3. Spustí scraping
    var importer = new RemaxImporter(browser, _listingRepository, logger);
    await importer.ImportAsync(sourceId, searchUrl, ct);
}
```

**BuildSearchUrl()**:
- Akceptuje `RemaxScrapingProfileDto`
- Vrací kompletní REMAX search URL se všemi parametry
- Automaticky encodeuje speciální znaky

---

### 2. RemaxImporter

**Odpovědnost**: Řídí scraping listů, detailů a persistence

**Flow**:
```csharp
public async Task ImportAsync(Guid sourceId, string searchUrl, CancellationToken ct)
{
    // 1. Scrape list page
    var items = await listScraper.ScrapeListAsync(searchUrl, ct);
    
    // 2. For each item: get detail
    foreach (var item in items)
    {
        var detail = await detailScraper.ScrapeDetailAsync(item, ct);
        
        // 3. Map to entity & upsert
        var entity = MapToListingEntity(sourceId, detail);
        await repository.UpsertAsync(entity, ct);
    }
}
```

---

### 3. RemaxListScraper

**Odpovědnost**: Scrapuje seznam inzerátů z list page

**Selektory** (fallback chain):
- `.remax-search-result-item`
- `.property-item`
- `.realty-item`
- `.search-result`

**Extrahuje z každé karty**:
- **Title**: `.remax-search-result-title a` ← `.property-title a` ← `h2 a` ← `h3 a`
- **DetailUrl**: `href` atribut z titulu - absolutní URL
- **Location**: `.remax-search-result-location` ← `.property-location` ← `.location`
- **Price**: `.remax-search-result-price` ← `.property-price` ← `.price` → parsováno ParsePrice()

**Output**: `List<RemaxListItem>`
```csharp
{
    Title = "4+kk Znojmo, 120m²",
    DetailUrl = "https://www.remax-czech.cz/nemovitost/123456-...",
    LocationText = "Znojmo",
    Price = 3_500_000m
}
```

---

### 4. RemaxDetailScraper

**Odpovědnost**: Scrapuje kompletní detail inzerátu

**Extrahuje**:

| Pole | Selektor | Fallback | Format |
|------|----------|----------|--------|
| **Title** | `h1` | `.property-title` | string |
| **Description** | `.property-detail__description` | `.remax-property-description` | string |
| **Price** | `.property-detail__price-main` | `.price-main` | ParsePrice() |
| **PriceNote** | `.property-detail__price-note` | `.price-note` | string (opt) |
| **AreaBuiltUp** | Table row s "užitná plocha" | UL/LI items | ParseArea() |
| **AreaLand** | Table row s "plocha pozemku" | UL/LI items | ParseArea() |
| **Photos** | `img[src*="mlsf.remax"]` | `/data/` pattern | Max 20 URLs |

**Output**: `RemaxDetailResult`
```csharp
{
    Title = "Prodej domu 4+kz se zahradou",
    Description = "Pěkný dům v centru Znojma...",
    LocationText = "Znojmo",
    Price = 3_500_000m,
    AreaBuiltUp = 120.0,
    AreaLand = 500.0,
    PriceNote = "Cena bez maklérského poplatku",
    PhotoUrls = [ "https://mlsf.remax-czech.cz/..." ]
}
```

---

## 🎯 RemaxScrapingProfileDto

**Konfigurační objekt pro REMAX scraping**

```csharp
public sealed class RemaxScrapingProfileDto
{
    // Identifikace profilu
    public string Name { get; set; } = "Default";
    
    // ─── STRATEGII vyhledávání ───
    
    /// Direktní URL (nejvyšší priorita - ostatní parametry ignorovány)
    public string? DirectUrl { get; set; }
    
    /// Region ID (např. 116 = Jihomoravský kraj)
    public int? RegionId { get; set; }
    
    /// District/Okres ID (např. 3713 = Znojmo)
    public int? DistrictId { get; set; }
    
    /// Město/municipalita (textově)
    public string? CityName { get; set; }
    
    // ─── FILTRY ───
    
    /// Bitmask typ nemovitostí (6=domy, 1=byty, atd.)
    public int PropertyTypeMask { get; set; } = 6;
    
    /// Maximální cena v Kč
    public long? PriceMax { get; set; } = 7_500_000;
    
    /// Minimální cena v Kč
    public long? PriceMin { get; set; }
    
    /// Hledaný text (pro fulltext search)
    public string? SearchText { get; set; }
    
    // ─── CHOVÁNÍ ───
    
    /// Typ vyhledávání: 1=fulltext (desc_text), 2=region-based
    public int SearchType { get; set; } = 2;
    
    /// Nabídnutá: "Sale" nebo "Rent"
    public string OfferType { get; set; } = "Sale";
    
    /// Max počet stránek (0 = všechny, 5 = default)
    public int MaxPages { get; set; } = 5;
}
```

### Priority řešení:
1. **DirectUrl** - pokud je specifikovaná, všechno ostatní se ignoruje
2. **RegionId + DistrictId** - pro region-based vyhledávání
3. **CityName + SearchText** - pro textové vyhledávání

---

## 📚 Příklady

### Příklad 1: Okres Znojmo (Default)
```csharp
var profile = new RemaxScrapingProfileDto
{
    Name = "Znojmo district",
    RegionId = 116,  // Jihomoravský kraj
    DistrictId = 3713,  // Znojmo
    PropertyTypeMask = 6,  // Domy a vily
    PriceMax = 7_500_000,
    SearchType = 2  // Region-based
};
await remaxService.RunAsync(profile, ct);
```

**Generovaná URL**:
```
https://www.remax-czech.cz/reality/vyhledavani/?hledani=2&regions[116][3713]=on&price_to=7500000&types[6]=on
```

### Příklad 2: Fulltext hledání - Město Znojmo
```csharp
var profile = new RemaxScrapingProfileDto
{
    Name = "Znojmo city fulltext",
    SearchText = "Znojmo",
    SearchType = 1,  // Fulltext
    PropertyTypeMask = 6,
    PriceMax = 5_000_000
};
await remaxService.RunAsync(profile, ct);
```

**Generovaná URL**:
```
https://www.remax-czech.cz/reality/vyhledavani/?hledani=1&desc_text=Znojmo&price_to=5000000&types[6]=on
```

### Příklad 3: Direktní URL (Praha, byty do 2M)
```csharp
var profile = new RemaxScrapingProfileDto
{
    Name = "Prague apartments",
    DirectUrl = "https://www.remax-czech.cz/reality/vyhledavani/?hledani=2&regions[109][3559]=on&price_to=2000000&types[1]=on"
};
await remaxService.RunAsync(profile, ct);
```

### Příklad 4: API Request (Blazor frontend)
```typescript
const response = await fetch('/api/scraping-playwright/run', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
        sourceCodes: ['REMAX'],
        remaxProfile: {
            name: 'Custom search',
            regionId: 116,
            districtId: 3713,
            propertyTypeMask: 6,
            priceMax: 6_000_000,
            searchType: 2,
            maxPages: 10
        }
    })
});

const result = await response.json();
console.log(`Job ${result.jobId} status: ${result.status}`);
```

---

## 🔌 API Reference

### POST /api/scraping-playwright/run

**Request**:
```json
{
    "sourceCodes": ["REMAX"],
    "fullRescan": false,
    "remaxProfile": {
        "name": "Znojmo district",
        "regionId": 116,
        "districtId": 3713,
        "propertyTypeMask": 6,
        "priceMax": 7500000,
        "searchType": 2,
        "maxPages": 5
    }
}
```

**Response** (200 OK):
```json
{
    "jobId": "a885569f-edeb-407e-b50a-6c34ae0ff431",
    "status": "Succeeded",
    "message": "Playwright scraping job completed for sources: REMAX"
}
```

### Status codes:
- **Succeeded**: Scraping skončil, všechny listingy uloženy
- **Failed**: Chyba během scrapingu (viz message)

---

## ⚠️ Limitace & Edge Cases

### Selektory
- **Risk**: REMAX mění HTML strukturu bez varování
- **Mitigation**: Fallback chain selektorů (3-5 variant za polem)
- **Solution**: Monitorovat logy, updatovat selektory ročně

### Performance
| Operace | Čas |
|---------|-----|
| List page scrape | 3-5 sec |
| Detail page scrape | 2-3 sec per item |
| Total (10 items) | ~30-40 sec |
| Typical timeout | 30 sec |

### Datové anomálie

**Cena**:
- Parametrické: "Na dotaz"
- Speciální: "2 500 000 - 3 500 000 Kč" (range)
- **Řešení**: ParsePrice() vezme první číslo

**Plocha**:
- Může chybět (null)
- Může být negativní (parsing error)
- Textové: "120m²" ← "120 m2" ← "120m2"
- **Řešení**: Nullable double, fallback na list item hodnoty

**Fotky**:
- Max 20 per listing
- Někdy s watermarkem
- Možné 404 po měsících
- **Řešení**:Store original_url, lazy load v UI

### Property Type Detekce
Dedukuje se z titulu (regex):
- "Dům" | "Vila" → House
- "Byt" → Apartment
- "Pozemek" → Land
- "Chata" → Cottage
- "Komerč" | "Skladová" → Commercial
- Default: Other

---

## 🔍 Troubleshooting

### ❌ "Načteno 0 inzerátů ze seznamu"

**Příčiny**:
1. Špatné RegionId/DistrictId
2. REMAX jste selektor
3. URL vrací prázdný seznam (legitimní)

**Debug**:
```bash
# Check DirectUrl visibility
curl "https://www.remax-czech.cz/reality/vyhledavani/?hledani=2&regions[116][3713]=on&types[6]=on"
```

### ❌ Playwright timeout (30 sec)

**Příčiny**:
- Síť pomalá
- REMAX server přetížený
- JS nenačten

**Řešení**:
- Zvýšit timeout v `BrowserTypeLaunchOptions`
- Redukovat MaxPages
- Zkusit později

### ❌ "REMAX source not found in database"

**Příčina**: Source není v DB

**Řešení**:
```sql
INSERT INTO re_realestate.sources (id, code, name, base_url, is_active)
VALUES (gen_random_uuid(), 'REMAX', 'RE/MAX Czech Republic', 'https://www.remax-czech.cz', true);
```

### ✅ Debugging

**Aktivovat verbose logging** (appsettings.Development.json):
```json
{
    "Logging": {
        "LogLevel": {
            "RealEstate.Infrastructure.Scraping.Remax": "Debug"
        }
    }
}
```

**Logy zahrnují**:
- URL generování
- Počet nalezených karet
- Chyby parsování
- Úspěšné/neúspěšné upserty

---

## 📋 Known Issues & Roadmap

### Current Limitations
- [ ] Nur Playwright (Python scraper deprecated)
- [ ] Maximálně 100 stránek
- [ ] Bez proxy rotace
- [ ] Bez retry logiky

### Future Enhancements
- [ ] Proxy support pro rate limiting
- [ ] Exponential backoff + retry
- [ ] Cached selectors (learning AI)
- [ ] Advanced filtering (rooms, usable area, etc.)
- [ ] Thumbnail generation + CDN upload

---

**Last Updated**: 22. února 2026  
**Maintainer**: Development Team
