# API Contracts - Real Estate Aggregator

**Verze API**: v1  
**Base URL**: `https://localhost:5001/api/v1`  
**Datum**: 22. února 2026

---

## 🔐 Autentizace

Pro MVP není autentizace vyžadována. V budoucích verzích:
- **Authorization**: `Bearer {jwt_token}`

---

## 📋 Listings API

### GET /api/v1/listings

Získá seznam inzerátů s filtrováním a paginací.

#### Query Parameters

| Parametr | Typ | Povinný | Popis |
|----------|-----|---------|-------|
| `sourceIds` | `Guid[]` | Ne | Seznam ID zdrojů (realitních kanceláří) |
| `region` | `string` | Ne | Kraj (např. "Jihomoravský") |
| `district` | `string` | Ne | Okres (např. "Znojmo") |
| `municipality` | `string` | Ne | Obec (např. "Znojmo") |
| `priceMin` | `decimal` | Ne | Minimální cena |
| `priceMax` | `decimal` | Ne | Maximální cena |
| `areaBuiltUpMin` | `decimal` | Ne | Minimální plocha budovy (m²) |
| `areaBuiltUpMax` | `decimal` | Ne | Maximální plocha budovy (m²) |
| `areaLandMin` | `decimal` | Ne | Minimální plocha pozemku (m²) |
| `areaLandMax` | `decimal` | Ne | Maximální plocha pozemku (m²) |
| `propertyType` | `string` | Ne | Typ nemovitosti: `House`, `Apartment`, `Land`, `Cottage`, `Commercial` |
| `offerType` | `string` | Ne | Typ nabídky: `Sale`, `Rent` |
| `status` | `string` | Ne | User stav: `New`, `Liked`, `Disliked`, `Ignored`, `ToVisit`, `Visited` |
| `searchText` | `string` | Ne | Fulltext vyhledávání v názvu a popisu |
| `onlyNewSince` | `DateTime` | Ne | Pouze inzeráty novější než toto datum |
| `pageNumber` | `int` | Ne | Číslo stránky (default: 1) |
| `pageSize` | `int` | Ne | Počet položek na stránku (default: 50, max: 200) |
| `sortBy` | `string` | Ne | Sloupec pro řazení: `Price`, `FirstSeenAt`, `LastSeenAt`, `AreaBuiltUp` |
| `sortDescending` | `bool` | Ne | Sestupné řazení (default: true) |

#### Response 200 OK

```json
{
  "items": [
    {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "source": {
        "id": "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d",
        "name": "Remax",
        "logoUrl": "https://example.com/remax-logo.png"
      },
      "title": "Prodej rodinného domu 4+1, Znojmo",
      "locationText": "Znojmo, okres Znojmo",
      "region": "Jihomoravský",
      "district": "Znojmo",
      "municipality": "Znojmo",
      "propertyType": "House",
      "offerType": "Sale",
      "price": 4500000,
      "priceNote": "včetně provize",
      "areaBuiltUp": 120.5,
      "areaLand": 450.0,
      "rooms": 4,
      "condition": "Good",
      "firstSeenAt": "2026-02-20T10:30:00Z",
      "lastSeenAt": "2026-02-22T08:15:00Z",
      "userStatus": "New",
      "photoCount": 12
    }
  ],
  "totalCount": 156,
  "pageNumber": 1,
  "pageSize": 50,
  "totalPages": 4
}
```

---

### GET /api/v1/listings/{id}

Získá detail jednoho inzerátu.

#### Path Parameters

| Parametr | Typ | Popis |
|----------|-----|-------|
| `id` | `Guid` | ID inzerátu |

#### Response 200 OK

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "source": {
    "id": "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d",
    "name": "Remax",
    "baseUrl": "https://www.remax-czech.cz",
    "logoUrl": "https://example.com/remax-logo.png"
  },
  "externalId": "12345",
  "url": "https://www.remax-czech.cz/nemovitosti/12345",
  "title": "Prodej rodinného domu 4+1, Znojmo",
  "description": "Nabízíme k prodeji rodinný dům v klidné lokalitě...",
  "propertyType": "House",
  "offerType": "Sale",
  "price": 4500000,
  "priceNote": "včetně provize",
  "locationText": "Znojmo, okres Znojmo",
  "region": "Jihomoravský",
  "district": "Znojmo",
  "municipality": "Znojmo",
  "areaBuiltUp": 120.5,
  "areaLand": 450.0,
  "rooms": 4,
  "hasKitchen": true,
  "constructionType": "Brick",
  "condition": "Good",
  "createdAtSource": "2026-01-15T00:00:00Z",
  "updatedAtSource": "2026-02-10T00:00:00Z",
  "firstSeenAt": "2026-02-20T10:30:00Z",
  "lastSeenAt": "2026-02-22T08:15:00Z",
  "isActive": true,
  "photos": [
    {
      "id": "7c8d9e0f-1a2b-3c4d-5e6f-7a8b9c0d1e2f",
      "originalUrl": "https://cdn.remax.cz/photos/12345/1.jpg",
      "storedUrl": null,
      "order": 0
    },
    {
      "id": "8d9e0f1a-2b3c-4d5e-6f7a-8b9c0d1e2f3a",
      "originalUrl": "https://cdn.remax.cz/photos/12345/2.jpg",
      "storedUrl": null,
      "order": 1
    }
  ],
  "userState": {
    "status": "New",
    "notes": null,
    "lastUpdated": "2026-02-22T08:15:00Z"
  },
  "analysisJobs": [
    {
      "id": "9e0f1a2b-3c4d-5e6f-7a8b-9c0d1e2f3a4b",
      "status": "Succeeded",
      "storageProvider": "GoogleDrive",
      "storageUrl": "https://drive.google.com/file/d/xxxxx/view",
      "requestedAt": "2026-02-21T14:20:00Z",
      "finishedAt": "2026-02-21T14:22:00Z"
    }
  ]
}
```

#### Response 404 Not Found

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.4",
  "title": "Not Found",
  "status": 404,
  "detail": "Listing with ID '3fa85f64-5717-4562-b3fc-2c963f66afa6' was not found."
}
```

---

### POST /api/v1/listings/{id}/state

Uloží nebo aktualizuje user stav inzerátu.

#### Path Parameters

| Parametr | Typ | Popis |
|----------|-----|-------|
| `id` | `Guid` | ID inzerátu |

#### Request Body

```json
{
  "status": "Liked",
  "notes": "Zajímavá lokalita, domluvit prohlídku"
}
```

**UpdateUserStateDto:**

| Pole | Typ | Povinný | Popis |
|------|-----|---------|-------|
| `status` | `string` | Ano | `New`, `Liked`, `Disliked`, `Ignored`, `ToVisit`, `Visited` |
| `notes` | `string` | Ne | Uživatelské poznámky (max 2000 znaků) |

#### Response 200 OK

```json
{
  "status": "Liked",
  "notes": "Zajímavá lokalita, domluvit prohlídku",
  "lastUpdated": "2026-02-22T10:45:00Z"
}
```

#### Response 400 Bad Request

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "Validation Error",
  "status": 400,
  "errors": {
    "status": ["Invalid status value. Allowed: New, Liked, Disliked, Ignored, ToVisit, Visited"]
  }
}
```

---

## 🏢 Sources API

### GET /api/v1/sources

Získá seznam všech zdrojů (realitních kanceláří).

#### Response 200 OK

```json
[
  {
    "id": "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d",
    "name": "Remax",
    "baseUrl": "https://www.remax-czech.cz",
    "logoUrl": "https://example.com/remax-logo.png",
    "isActive": true,
    "listingCount": 245
  },
  {
    "id": "2b3c4d5e-6f7a-8b9c-0d1e-2f3a4b5c6d7e",
    "name": "MM Reality",
    "baseUrl": "https://www.mmreality.cz",
    "logoUrl": "https://example.com/mmreality-logo.png",
    "isActive": true,
    "listingCount": 312
  },
  {
    "id": "3c4d5e6f-7a8b-9c0d-1e2f-3a4b5c6d7e8f",
    "name": "Prodejme.to",
    "baseUrl": "https://www.prodejme.to",
    "logoUrl": "https://example.com/prodejme-logo.png",
    "isActive": true,
    "listingCount": 128
  }
]
```

---

## 🕷️ Scraping (Playwright) API

### POST /api/scraping-playwright/run

Spustí scraping job v .NET (Playwright). Primárně pro REMAX. Endpoint je zatím neversionovaný.

#### Request Body

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
    "offerType": "Sale",
    "maxPages": 5
  }
}
```

**ScrapeTriggerDto:**

| Pole | Typ | Povinný | Popis |
|------|-----|---------|-------|
| `sourceCodes` | `string[]` | Ne | Pokud je prázdné, scrapuje všechny aktivní zdroje |
| `fullRescan` | `bool` | Ne | Ignoruje cache, scrapuje vše |
| `remaxProfile` | `object` | Ne | Profil pro REMAX (volitelný, má default) |

**RemaxScrapingProfileDto:**

| Pole | Typ | Povinný | Popis |
|------|-----|---------|-------|
| `name` | `string` | Ne | Jméno profilu |
| `directUrl` | `string` | Ne | Přímá URL (má prioritu nad ostatními poli) |
| `regionId` | `int` | Ne | Region ID (např. 116 = Jihomoravský kraj) |
| `districtId` | `int` | Ne | Okres ID (např. 3713 = Znojmo) |
| `cityName` | `string` | Ne | Město (text) |
| `propertyTypeMask` | `int` | Ne | Typ nemovitosti (bitmask) |
| `priceMax` | `long` | Ne | Maximální cena |
| `priceMin` | `long` | Ne | Minimální cena |
| `searchText` | `string` | Ne | Fulltext |
| `searchType` | `int` | Ne | 1=fulltext, 2=region-based |
| `offerType` | `string` | Ne | `Sale` nebo `Rent` |
| `maxPages` | `int` | Ne | Maximální počet stran |

#### Response 200 OK

```json
{
  "jobId": "c62493ab-e619-46ca-952d-c25db6043f4c",
  "status": "Succeeded",
  "message": "Playwright scraping job completed for sources: REMAX"
}
```

---

## 🧠 Analysis API

### POST /api/v1/listings/{id}/analysis

Vytvoří novou analýzu pro inzerát (stáhne data + fotky a nahraje do cloudu).

#### Path Parameters

| Parametr | Typ | Popis |
|----------|-----|-------|
| `id` | `Guid` | ID inzerátu |

#### Request Body (volitelné)

```json
{
  "storageProvider": "GoogleDrive",
  "includePhotos": true,
  "format": "Markdown"
}
```

**CreateAnalysisDto:**

| Pole | Typ | Povinný | Default | Popis |
|------|-----|---------|---------|-------|
| `storageProvider` | `string` | Ne | `GoogleDrive` | `GoogleDrive`, `OneDrive`, `Local` |
| `includePhotos` | `bool` | Ne | `true` | Zahrnout fotky do balíčku |
| `format` | `string` | Ne | `Markdown` | `Markdown`, `HTML`, `Word` |

#### Response 202 Accepted

```json
{
  "id": "9e0f1a2b-3c4d-5e6f-7a8b-9c0d1e2f3a4b",
  "listingId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "Pending",
  "storageProvider": "GoogleDrive",
  "requestedAt": "2026-02-22T11:00:00Z"
}
```

#### Response 409 Conflict

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.8",
  "title": "Conflict",
  "status": 409,
  "detail": "An analysis job for this listing is already in progress."
}
```

---

### GET /api/v1/analysis/{jobId}

Získá stav analýzy.

#### Path Parameters

| Parametr | Typ | Popis |
|----------|-----|-------|
| `jobId` | `Guid` | ID analýzy |

#### Response 200 OK (Pending/Running)

```json
{
  "id": "9e0f1a2b-3c4d-5e6f-7a8b-9c0d1e2f3a4b",
  "listingId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "Running",
  "storageProvider": "GoogleDrive",
  "requestedAt": "2026-02-22T11:00:00Z",
  "finishedAt": null,
  "storageUrl": null,
  "errorMessage": null
}
```

#### Response 200 OK (Succeeded)

```json
{
  "id": "9e0f1a2b-3c4d-5e6f-7a8b-9c0d1e2f3a4b",
  "listingId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "Succeeded",
  "storageProvider": "GoogleDrive",
  "requestedAt": "2026-02-22T11:00:00Z",
  "finishedAt": "2026-02-22T11:02:15Z",
  "storageUrl": "https://drive.google.com/file/d/1a2b3c4d5e6f7g8h9i0j/view",
  "storagePath": "/RealEstateAnalyses/Listing_3fa85f64.md",
  "errorMessage": null
}
```

#### Response 200 OK (Failed)

```json
{
  "id": "9e0f1a2b-3c4d-5e6f-7a8b-9c0d1e2f3a4b",
  "listingId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "Failed",
  "storageProvider": "GoogleDrive",
  "requestedAt": "2026-02-22T11:00:00Z",
  "finishedAt": "2026-02-22T11:01:30Z",
  "storageUrl": null,
  "errorMessage": "Failed to authenticate with Google Drive API: Invalid credentials"
}
```

---

### GET /api/v1/analysis

Získá seznam všech analýz (volitelně filtrovaný).

#### Query Parameters

| Parametr | Typ | Povinný | Popis |
|----------|-----|---------|-------|
| `listingId` | `Guid` | Ne | Filtr podle inzerátu |
| `status` | `string` | Ne | Filtr podle statusu |
| `pageNumber` | `int` | Ne | Stránka (default: 1) |
| `pageSize` | `int` | Ne | Počet položek (default: 50) |

#### Response 200 OK

```json
{
  "items": [
    {
      "id": "9e0f1a2b-3c4d-5e6f-7a8b-9c0d1e2f3a4b",
      "listingId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "listingTitle": "Prodej rodinného domu 4+1, Znojmo",
      "status": "Succeeded",
      "storageProvider": "GoogleDrive",
      "storageUrl": "https://drive.google.com/file/d/xxxxx/view",
      "requestedAt": "2026-02-22T11:00:00Z",
      "finishedAt": "2026-02-22T11:02:15Z"
    }
  ],
  "totalCount": 12,
  "pageNumber": 1,
  "pageSize": 50,
  "totalPages": 1
}
```

---

## 📦 DTOs (Data Transfer Objects)

### Common DTOs

#### PagedResultDto<T>

```typescript
{
  items: T[],
  totalCount: number,
  pageNumber: number,
  pageSize: number,
  totalPages: number
}
```

### Listing DTOs

#### ListingSummaryDto

```typescript
{
  id: string (Guid),
  source: SourceDto,
  title: string,
  locationText?: string,
  region?: string,
  district?: string,
  municipality?: string,
  propertyType: PropertyType,
  offerType: OfferType,
  price?: number,
  priceNote?: string,
  areaBuiltUp?: number,
  areaLand?: number,
  rooms?: number,
  condition?: Condition,
  firstSeenAt: DateTime,
  lastSeenAt: DateTime,
  userStatus?: ListingStatus,
  photoCount: number
}
```

#### ListingDetailDto

```typescript
{
  id: string (Guid),
  source: SourceDto,
  externalId?: string,
  url: string,
  title: string,
  description?: string,
  propertyType: PropertyType,
  offerType: OfferType,
  price?: number,
  priceNote?: string,
  locationText?: string,
  region?: string,
  district?: string,
  municipality?: string,
  areaBuiltUp?: number,
  areaLand?: number,
  rooms?: number,
  hasKitchen?: boolean,
  constructionType?: ConstructionType,
  condition?: Condition,
  createdAtSource?: DateTime,
  updatedAtSource?: DateTime,
  firstSeenAt: DateTime,
  lastSeenAt: DateTime,
  isActive: boolean,
  photos: ListingPhotoDto[],
  userState?: UserListingStateDto,
  analysisJobs: AnalysisJobSummaryDto[]
}
```

### Source DTOs

#### SourceDto

```typescript
{
  id: string (Guid),
  name: string,
  baseUrl: string,
  logoUrl?: string,
  isActive: boolean,
  listingCount?: number
}
```

### Analysis DTOs

#### AnalysisJobDto

```typescript
{
  id: string (Guid),
  listingId: string (Guid),
  listingTitle?: string,
  status: AnalysisStatus,
  storageProvider?: string,
  storagePath?: string,
  storageUrl?: string,
  requestedAt: DateTime,
  finishedAt?: DateTime,
  errorMessage?: string
}
```

---

## 🔢 Enums

### PropertyType

```
House
Apartment
Land
Cottage
Commercial
Industrial
Garage
Other
```

### OfferType

```
Sale
Rent
```

### ConstructionType

```
Brick
Panel
Wood
Stone
Mixed
Other
```

### Condition

```
New
AfterReconstruction
Good
ToReconstruct
InConstruction
Project
```

### ListingStatus (User State)

```
New
Liked
Disliked
Ignored
ToVisit
Visited
```

### AnalysisStatus

```
Pending
Running
Succeeded
Failed
```

---

## ❌ Error Responses

Všechny chyby používají RFC 7807 Problem Details formát.

### 400 Bad Request

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "price": ["Price must be greater than 0"],
    "pageSize": ["Page size must be between 1 and 200"]
  }
}
```

### 404 Not Found

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.4",
  "title": "Not Found",
  "status": 404,
  "detail": "Resource with ID 'xxx' was not found."
}
```

### 500 Internal Server Error

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.6.1",
  "title": "An error occurred while processing your request.",
  "status": 500,
  "detail": "Internal server error. Please contact support if the problem persists."
}
```

---

## 📝 Versioning

API používá URL versioning: `/api/v1/...`

Při breaking changes bude vytvořena nová verze (v2).

**Backward compatible změny** (nová pole v response) nebudou vyžadovat novou verzi.

---

## 🔄 Rate Limiting (Future)

Pro budoucí verze s autentizací:

```
X-RateLimit-Limit: 1000
X-RateLimit-Remaining: 987
X-RateLimit-Reset: 2026-02-22T12:00:00Z
```

Limity:
- Authenticated: 1000 requests/hour
- Anonymous (MVP): neomezeno

---

**Konec API dokumentace** • Verze 1.0 • 22. února 2026
