# Technický návrh - Real Estate Aggregator

**Verze**: 1.0.0  
**Datum**: 22. února 2026  
**Status**: Living Document

---

## 📐 Architektura systému

### High-level architektura

```
┌─────────────────────────────────────────────────────────────────┐
│                         PRESENTATION LAYER                       │
│                                                                  │
│  ┌────────────────────────────────────────────────────────┐    │
│  │          Blazor Web App (MudBlazor UI)                 │    │
│  │  • Dashboard  • Filters  • Detail  • Analysis          │    │
│  └────────────────────┬───────────────────────────────────┘    │
│                       │ HTTP/REST                               │
└───────────────────────┼─────────────────────────────────────────┘
                        │
┌───────────────────────▼─────────────────────────────────────────┐
│                       APPLICATION LAYER                          │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │             ASP.NET Core Web API                        │   │
│  │  • Controllers  • Services  • DTOs  • Validators        │   │
│  └─────────────────────┬───────────────────────────────────┘   │
│                        │                                         │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │     Background Services (IHostedService, planned)       │   │
│  │  • AnalysisJobProcessor  • CloudStorageUploader         │   │
│  └─────────────────────────────────────────────────────────┘   │
└───────────────────────┬─────────────────────────────────────────┘
                        │
┌───────────────────────▼─────────────────────────────────────────┐
│                       DOMAIN LAYER                               │
│                                                                  │
│  ┌────────────────────────────────────────────────────────┐    │
│  │  Domain Entities: Listing, Source, Photo, UserState,  │    │
│  │                   AnalysisJob                          │    │
│  └────────────────────────────────────────────────────────┘    │
│                                                                  │
│  ┌────────────────────────────────────────────────────────┐    │
│  │  Domain Services: IListingRepository, ISourceRepository│    │
│  └────────────────────────────────────────────────────────┘    │
└───────────────────────┬─────────────────────────────────────────┘
                        │
┌───────────────────────▼─────────────────────────────────────────┐
│                   INFRASTRUCTURE LAYER                           │
│                                                                  │
│  ┌─────────────────┐  ┌──────────────────┐  ┌──────────────┐  │
│  │  EF Core        │  │  Cloud Storage   │  │  External    │  │
│  │  • DbContext    │  │  • Google Drive  │  │  APIs        │  │
│  │  • Repositories │  │  • OneDrive      │  │              │  │
│  └────────┬────────┘  └──────────────────┘  └──────────────┘  │
└───────────┼─────────────────────────────────────────────────────┘
            │
┌───────────▼─────────────────────────────────────────────────────┐
│                      DATA LAYER                                  │
│                                                                  │
│             PostgreSQL Database (Primary Storage)                │
│  • Listings  • Photos  • Sources  • UserStates  • AnalysisJobs  │
└──────────────────────────────────────────────────────────────────┘
            ▲
            │ Write
            │
┌───────────┴─────────────────────────────────────────────────────┐
│          SCRAPING LAYER (Playwright .NET + Python)                │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │  Scraping Orchestrators                                  │  │
│  │  • Playwright (.NET) – REMAX                             │  │
│  │  • Python Scraper API – MMR, Prodejme.to, Znojmo Reality│  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │  Core Components                                         │  │
│  │  • BaseScraper  • DataNormalizer  • DBWriter             │  │
│  └──────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────┘
```

---

## 🗂️ Datový model (detailní)

### ER Diagram

```
┌─────────────────┐
│     Source      │
└────────┬────────┘
         │ 1
         │
         │ N
┌────────▼────────────────────┐        ┌──────────────────┐
│        Listing              │───────▶│  ListingPhoto    │
│                             │ 1    N │                  │
│ • Id (Guid)                 │        │ • Id (Guid)      │
│ • SourceId (FK)             │        │ • ListingId (FK) │
│ • ExternalId (string)       │        │ • OriginalUrl    │
│ • Url                       │        │ • StoredUrl      │
│ • Title                     │        │ • Order          │
│ • Description               │        └──────────────────┘
│ • PropertyType (enum)       │
│ • OfferType (enum)          │
│ • Price                     │        ┌──────────────────────┐
│ • LocationText              │───────▶│ UserListingState     │
│ • Region                    │ 1    N │                      │
│ • District                  │        │ • Id (Guid)          │
│ • Municipality              │        │ • UserId (Guid?)     │
│ • AreaBuiltUp               │        │ • ListingId (FK)     │
│ • AreaLand                  │        │ • Status (enum)      │
│ • Rooms                     │        │ • Notes (text)       │
│ • Condition (enum)          │        │ • LastUpdated        │
│ • FirstSeenAt               │        └──────────────────────┘
│ • LastSeenAt                │
│ • IsActive                  │        ┌──────────────────────┐
└─────────────────────────────┘───────▶│   AnalysisJob        │
                               1    N  │                      │
                                       │ • Id (Guid)          │
                                       │ • ListingId (FK)     │
                                       │ • Status (enum)      │
                                       │ • StorageProvider    │
                                       │ • StoragePath        │
                                       │ • RequestedAt        │
                                       │ • FinishedAt         │
                                       │ • ErrorMessage       │
                                       └──────────────────────┘
```

### Tabulky (DDL koncepty)

#### Source
```sql
CREATE TABLE Sources (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    Name VARCHAR(100) NOT NULL UNIQUE,
    BaseUrl VARCHAR(500) NOT NULL,
    LogoUrl VARCHAR(500),
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_sources_active ON Sources(IsActive);
```

#### Listing
```sql
CREATE TABLE Listings (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    SourceId UUID NOT NULL REFERENCES Sources(Id),
    ExternalId VARCHAR(100),
    Url VARCHAR(1000) NOT NULL,
    Title VARCHAR(500) NOT NULL,
    Description TEXT,
    PropertyType VARCHAR(50) NOT NULL,  -- Enum: House, Apartment, Land, Cottage
    OfferType VARCHAR(20) NOT NULL,     -- Enum: Sale, Rent
    Price DECIMAL(18, 2),
    PriceNote VARCHAR(200),
    LocationText VARCHAR(500),
    Region VARCHAR(100),
    District VARCHAR(100),
    Municipality VARCHAR(100),
    AreaBuiltUp DECIMAL(10, 2),         -- m²
    AreaLand DECIMAL(10, 2),            -- m²
    Rooms INT,
    HasKitchen BOOLEAN,
    ConstructionType VARCHAR(50),       -- Enum: Brick, Panel, Wood, ...
    Condition VARCHAR(50),              -- Enum: New, Renovated, ToRenovate, ...
    CreatedAtSource TIMESTAMP,
    UpdatedAtSource TIMESTAMP,
    FirstSeenAt TIMESTAMP NOT NULL DEFAULT NOW(),
    LastSeenAt TIMESTAMP NOT NULL DEFAULT NOW(),
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    
    CONSTRAINT uq_source_external UNIQUE(SourceId, ExternalId)
);

-- Indexy pro vyhledávání a filtrování
CREATE INDEX idx_listings_source ON Listings(SourceId);
CREATE INDEX idx_listings_property_type ON Listings(PropertyType);
CREATE INDEX idx_listings_offer_type ON Listings(OfferType);
CREATE INDEX idx_listings_price ON Listings(Price) WHERE Price IS NOT NULL;
CREATE INDEX idx_listings_region ON Listings(Region);
CREATE INDEX idx_listings_active ON Listings(IsActive);
CREATE INDEX idx_listings_first_seen ON Listings(FirstSeenAt);
CREATE INDEX idx_listings_location_text ON Listings USING gin(to_tsvector('simple', LocationText));
```

#### ListingPhoto
```sql
CREATE TABLE ListingPhotos (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ListingId UUID NOT NULL REFERENCES Listings(Id) ON DELETE CASCADE,
    OriginalUrl VARCHAR(1000) NOT NULL,
    StoredUrl VARCHAR(1000),
    Order INT NOT NULL DEFAULT 0,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_photos_listing ON ListingPhotos(ListingId);
CREATE INDEX idx_photos_order ON ListingPhotos(ListingId, Order);
```

#### UserListingState
```sql
CREATE TABLE UserListingStates (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    UserId UUID,  -- NULL pro MVP (single user)
    ListingId UUID NOT NULL REFERENCES Listings(Id) ON DELETE CASCADE,
    Status VARCHAR(20) NOT NULL,  -- Enum: New, Liked, Disliked, Ignored, ToVisit, Visited
    Notes TEXT,
    LastUpdated TIMESTAMP NOT NULL DEFAULT NOW(),
    
    CONSTRAINT uq_user_listing UNIQUE(UserId, ListingId)
);

CREATE INDEX idx_user_state_listing ON UserListingStates(ListingId);
CREATE INDEX idx_user_state_user ON UserListingStates(UserId);
CREATE INDEX idx_user_state_status ON UserListingStates(Status);
```

#### AnalysisJob
```sql
CREATE TABLE AnalysisJobs (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ListingId UUID NOT NULL REFERENCES Listings(Id) ON DELETE CASCADE,
    Status VARCHAR(20) NOT NULL DEFAULT 'Pending',  -- Pending, Running, Succeeded, Failed
    StorageProvider VARCHAR(50),  -- GoogleDrive, OneDrive, Local
    StoragePath VARCHAR(1000),
    StorageUrl VARCHAR(1000),
    RequestedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    FinishedAt TIMESTAMP,
    ErrorMessage TEXT,
    
    CONSTRAINT chk_status CHECK (Status IN ('Pending', 'Running', 'Succeeded', 'Failed'))
);

CREATE INDEX idx_analysis_listing ON AnalysisJobs(ListingId);
CREATE INDEX idx_analysis_status ON AnalysisJobs(Status);
CREATE INDEX idx_analysis_requested ON AnalysisJobs(RequestedAt);
```

---

## 🔧 .NET Backend - Detailní design

### Project structure

```
RealEstate.Api/
├── Controllers/
│   ├── ListingsController.cs
│   ├── SourcesController.cs
│   └── AnalysisController.cs
├── DTOs/
│   ├── Listing/
│   │   ├── ListingDto.cs
│   │   ├── ListingDetailDto.cs
│   │   ├── ListingSummaryDto.cs
│   │   └── ListingFilterDto.cs
│   ├── Analysis/
│   │   ├── AnalysisJobDto.cs
│   │   └── CreateAnalysisDto.cs
│   └── Common/
│       └── PagedResultDto.cs
├── Mapping/
│   └── MappingProfile.cs
├── Program.cs
└── appsettings.json

RealEstate.App/
├── Components/
│   ├── FilterPanel.razor
│   ├── ListingCard.razor
│   └── ListingDetailDialog.razor
├── Pages/
│   ├── Dashboard.razor
│   ├── Analyses.razor
│   └── Settings.razor
├── Services/
│   ├── IListingApiService.cs
│   └── ListingApiService.cs
├── Shared/
│   ├── MainLayout.razor
│   └── NavMenu.razor
└── _Imports.razor

RealEstate.Domain/
├── Entities/
│   ├── Source.cs
│   ├── Listing.cs
│   ├── ListingPhoto.cs
│   ├── UserListingState.cs
│   └── AnalysisJob.cs
├── Enums/
│   ├── PropertyType.cs
│   ├── OfferType.cs
│   ├── ConstructionType.cs
│   ├── Condition.cs
│   ├── ListingStatus.cs
│   └── AnalysisStatus.cs
└── Repositories/
    ├── IRepository.cs
    ├── IListingRepository.cs
    └── IAnalysisJobRepository.cs

RealEstate.Infrastructure/
├── Data/
│   ├── RealEstateDbContext.cs
│   ├── DbInitializer.cs
│   └── Configurations/
│       ├── ListingConfiguration.cs
│       ├── SourceConfiguration.cs
│       └── ...
├── Repositories/
│   ├── Repository.cs
│   ├── ListingRepository.cs
│   └── AnalysisJobRepository.cs
└── CloudStorage/
    ├── IGoogleDriveService.cs
    ├── GoogleDriveService.cs
    ├── IOneDriveService.cs
    └── OneDriveService.cs

RealEstate.Background/
└── Services/
    ├── AnalysisBackgroundService.cs
    ├── IDocumentGenerator.cs
    └── MarkdownDocumentGenerator.cs
```

### Klíčové třídy

#### Listing Entity
```csharp
namespace RealEstate.Domain.Entities;

public class Listing
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public string? ExternalId { get; set; }
    public string Url { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    
    public PropertyType PropertyType { get; set; }
    public OfferType OfferType { get; set; }
    
    public decimal? Price { get; set; }
    public string? PriceNote { get; set; }
    
    public string? LocationText { get; set; }
    public string? Region { get; set; }
    public string? District { get; set; }
    public string? Municipality { get; set; }
    
    public decimal? AreaBuiltUp { get; set; }
    public decimal? AreaLand { get; set; }
    public int? Rooms { get; set; }
    public bool? HasKitchen { get; set; }
    
    public ConstructionType? ConstructionType { get; set; }
    public Condition? Condition { get; set; }
    
    public DateTime? CreatedAtSource { get; set; }
    public DateTime? UpdatedAtSource { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public bool IsActive { get; set; } = true;
    
    // Navigation properties
    public Source Source { get; set; } = null!;
    public ICollection<ListingPhoto> Photos { get; set; } = new List<ListingPhoto>();
    public ICollection<UserListingState> UserStates { get; set; } = new List<UserListingState>();
    public ICollection<AnalysisJob> AnalysisJobs { get; set; } = new List<AnalysisJob>();
}
```

#### ListingFilterDto
```csharp
namespace RealEstate.Api.DTOs.Listing;

public class ListingFilterDto
{
    public List<Guid>? SourceIds { get; set; }
    public string? Region { get; set; }
    public string? District { get; set; }
    public string? Municipality { get; set; }
    
    public decimal? PriceMin { get; set; }
    public decimal? PriceMax { get; set; }
    
    public decimal? AreaBuiltUpMin { get; set; }
    public decimal? AreaBuiltUpMax { get; set; }
    
    public decimal? AreaLandMin { get; set; }
    public decimal? AreaLandMax { get; set; }
    
    public PropertyType? PropertyType { get; set; }
    public OfferType? OfferType { get; set; }
    
    public ListingStatus? Status { get; set; }
    
    public string? SearchText { get; set; }
    
    public DateTime? OnlyNewSince { get; set; }
    
    // Paginace
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    
    // Řazení
    public string? SortBy { get; set; } = "FirstSeenAt";
    public bool SortDescending { get; set; } = true;
}
```

#### ListingsController (ukázka)
```csharp
namespace RealEstate.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ListingsController : ControllerBase
{
    private readonly IListingRepository _listingRepository;
    private readonly IMapper _mapper;
    private readonly ILogger<ListingsController> _logger;

    public ListingsController(
        IListingRepository listingRepository,
        IMapper mapper,
        ILogger<ListingsController> logger)
    {
        _listingRepository = listingRepository;
        _mapper = mapper;
        _logger = logger;
    }

    /// <summary>
    /// Získá seznam inzerátů s filtrováním a paginací
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<ListingSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<ListingSummaryDto>>> GetListings(
        [FromQuery] ListingFilterDto filter)
    {
        var query = _listingRepository.GetQueryable();
        
        // Aplikace filtrů
        query = ApplyFilters(query, filter);
        
        // Počet celkem
        var totalCount = await query.CountAsync();
        
        // Řazení
        query = ApplySorting(query, filter.SortBy, filter.SortDescending);
        
        // Paginace
        query = query
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize);
        
        var listings = await query.ToListAsync();
        var dtos = _mapper.Map<List<ListingSummaryDto>>(listings);
        
        return Ok(new PagedResultDto<ListingSummaryDto>
        {
            Items = dtos,
            TotalCount = totalCount,
            PageNumber = filter.PageNumber,
            PageSize = filter.PageSize
        });
    }
    
    private IQueryable<Listing> ApplyFilters(IQueryable<Listing> query, ListingFilterDto filter)
    {
        if (filter.SourceIds?.Any() == true)
            query = query.Where(l => filter.SourceIds.Contains(l.SourceId));
            
        if (!string.IsNullOrEmpty(filter.Region))
            query = query.Where(l => l.Region == filter.Region);
            
        if (filter.PriceMin.HasValue)
            query = query.Where(l => l.Price >= filter.PriceMin.Value);
            
        if (filter.PriceMax.HasValue)
            query = query.Where(l => l.Price <= filter.PriceMax.Value);
            
        // ... další filtry
        
        if (!string.IsNullOrEmpty(filter.SearchText))
        {
            var search = filter.SearchText.ToLower();
            query = query.Where(l => 
                EF.Functions.ILike(l.Title, $"%{search}%") ||
                EF.Functions.ILike(l.Description ?? "", $"%{search}%"));
        }
        
        query = query.Where(l => l.IsActive);
        
        return query;
    }
    
    // ... další metody
}
```

---

## 🐍 Python Scraper - Detailní design

### Struktura

```
scraper/
├── core/
│   ├── __init__.py
│   ├── runner.py           # Orchestrator
│   ├── database.py         # DB connection & operations
│   └── scrapers/
│       ├── __init__.py
│       ├── remax_scraper.py
│       ├── mmreality_scraper.py
│       ├── prodejmeto_scraper.py
│       └── znojmoreality_scraper.py
├── config/
│   ├── settings.yaml
│   └── logging.yaml
├── requirements.txt
├── main.py
└── README.md
```

### BaseScraper Protocol

```python
# scrapers/base_scraper.py
from typing import Protocol, List
from core.models import RawListing, NormalizedListing

class BaseScraper(Protocol):
    """Protocol pro všechny scrapers"""
    
    source_name: str
    source_id: str  # UUID z DB
    base_url: str
    
    def fetch_listings(self, max_pages: int = 10) -> List[RawListing]:
        """
        Projde listing stránky a vrátí základní info o inzerátech.
        
        Args:
            max_pages: Maximální počet stránek k procházení
            
        Returns:
            List RawListingů (URL, ExternalId)
        """
        ...
    
    def fetch_listing_detail(self, raw: RawListing) -> str:
        """
        Stáhne HTML detail inzerátu.
        
        Args:
            raw: RawListing s URL
            
        Returns:
            HTML string
        """
        ...
    
    def normalize(self, raw: RawListing, html: str) -> NormalizedListing:
        """
        Parsuje HTML a vytvoří NormalizedListing.
        
        Args:
            raw: Original RawListing
            html: HTML detail stránky
            
        Returns:
            NormalizedListing připravený k uložení do DB
        """
        ...
```

### Data Models

```python
# core/models.py
from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional, List
from enum import Enum

class PropertyType(str, Enum):
    HOUSE = "House"
    APARTMENT = "Apartment"
    LAND = "Land"
    COTTAGE = "Cottage"
    COMMERCIAL = "Commercial"

class OfferType(str, Enum):
    SALE = "Sale"
    RENT = "Rent"

@dataclass
class RawListing:
    """Základní info z listing stránky"""
    url: str
    external_id: Optional[str] = None

@dataclass
class NormalizedListing:
    """Kompletní naparsovaný inzerát"""
    source_id: str  # UUID
    external_id: Optional[str]
    url: str
    title: str
    description: Optional[str] = None
    
    property_type: PropertyType
    offer_type: OfferType
    
    price: Optional[float] = None
    price_note: Optional[str] = None
    
    location_text: Optional[str] = None
    region: Optional[str] = None
    district: Optional[str] = None
    municipality: Optional[str] = None
    
    area_built_up: Optional[float] = None
    area_land: Optional[float] = None
    rooms: Optional[int] = None
    
    condition: Optional[str] = None
    construction_type: Optional[str] = None
    
    created_at_source: Optional[datetime] = None
    updated_at_source: Optional[datetime] = None
    
    photo_urls: List[str] = field(default_factory=list)

@dataclass
class ScraperRunLog:
    """Log jednoho běhu scraperu"""
    source_id: str
    started_at: datetime
    finished_at: Optional[datetime] = None
    new_count: int = 0
    updated_count: int = 0
    error_count: int = 0
    errors: List[str] = field(default_factory=list)
```

### Remax Scraper (ukázka)

```python
# scrapers/remax_scraper.py
import httpx
from bs4 import BeautifulSoup
from typing import List
import re
from core.models import RawListing, NormalizedListing, PropertyType, OfferType

class RemaxScraper:
    source_name = "Remax"
    source_id = "..."  # UUID z DB
    base_url = "https://www.remax-czech.cz"
    
    def __init__(self):
        self.client = httpx.Client(timeout=30.0)
    
    def fetch_listings(self, max_pages: int = 10) -> List[RawListing]:
        """Projde paginované výpisy"""
        raw_listings = []
        
        for page in range(1, max_pages + 1):
            url = f"{self.base_url}/reality/vyhledavani/?page={page}"
            
            try:
                response = self.client.get(url)
                response.raise_for_status()
                
                soup = BeautifulSoup(response.text, 'html.parser')
                
                # Najít všechny karty inzerátů
                cards = soup.select('.property-card')  # Příklad selektoru
                
                if not cards:
                    break  # Konec paginace
                
                for card in cards:
                    link = card.select_one('a.property-link')
                    if link and link.get('href'):
                        detail_url = self.base_url + link['href']
                        external_id = self._extract_id_from_url(detail_url)
                        
                        raw_listings.append(RawListing(
                            url=detail_url,
                            external_id=external_id
                        ))
                
            except Exception as e:
                print(f"Error fetching page {page}: {e}")
                continue
        
        return raw_listings
    
    def fetch_listing_detail(self, raw: RawListing) -> str:
        """Stáhne detail HTML"""
        response = self.client.get(raw.url)
        response.raise_for_status()
        return response.text
    
    def normalize(self, raw: RawListing, html: str) -> NormalizedListing:
        """Parsuje Remax HTML do NormalizedListing"""
        soup = BeautifulSoup(html, 'html.parser')
        
        # Titulek
        title_elem = soup.select_one('h1.property-title')
        title = title_elem.get_text(strip=True) if title_elem else "Bez názvu"
        
        # Popis
        desc_elem = soup.select_one('.property-description')
        description = desc_elem.get_text(strip=True) if desc_elem else None
        
        # Cena
        price_elem = soup.select_one('.property-price')
        price = self._parse_price(price_elem.get_text() if price_elem else None)
        
        # Parametry
        params = self._parse_parameters(soup)
        
        # Fotky
        photo_urls = [img['src'] for img in soup.select('.property-gallery img') if img.get('src')]
        
        return NormalizedListing(
            source_id=self.source_id,
            external_id=raw.external_id,
            url=raw.url,
            title=title,
            description=description,
            property_type=params.get('property_type', PropertyType.HOUSE),
            offer_type=params.get('offer_type', OfferType.SALE),
            price=price,
            location_text=params.get('location'),
            area_built_up=params.get('area'),
            area_land=params.get('land_area'),
            rooms=params.get('rooms'),
            photo_urls=photo_urls
        )
    
    def _extract_id_from_url(self, url: str) -> Optional[str]:
        """Extrahuje ID z URL"""
        match = re.search(r'/nemovitost/(\d+)', url)
        return match.group(1) if match else None
    
    def _parse_price(self, price_text: Optional[str]) -> Optional[float]:
        """Parsuje cenu z textu (např. '4 500 000 Kč')"""
        if not price_text:
            return None
        
        # Odstranit vše kromě číslic
        digits = re.sub(r'[^\d]', '', price_text)
        try:
            return float(digits)
        except ValueError:
            return None
    
    def _parse_parameters(self, soup: BeautifulSoup) -> dict:
        """Parsuje parametry z tabulky/seznamu"""
        params = {}
        
        # Příklad: najít tabulku parametrů
        param_table = soup.select('.params-table tr')
        for row in param_table:
            label = row.select_one('.param-label')
            value = row.select_one('.param-value')
            
            if label and value:
                key = label.get_text(strip=True).lower()
                val = value.get_text(strip=True)
                
                if 'plocha' in key:
                    params['area'] = self._parse_area(val)
                elif 'pozemek' in key:
                    params['land_area'] = self._parse_area(val)
                elif 'pokoje' in key or 'dispozice' in key:
                    params['rooms'] = self._parse_rooms(val)
                # ... další parsování
        
        return params
    
    def _parse_area(self, text: str) -> Optional[float]:
        """Parsuje plochu (např. '120 m²' -> 120.0)"""
        match = re.search(r'([\d\s,\.]+)', text)
        if match:
            num_str = match.group(1).replace(' ', '').replace(',', '.')
            try:
                return float(num_str)
            except ValueError:
                pass
        return None
    
    def _parse_rooms(self, text: str) -> Optional[int]:
        """Parsuje počet pokojů (např. '4+1' -> 4)"""
        match = re.search(r'(\d+)', text)
        return int(match.group(1)) if match else None
```

### Runner (orchestrator)

```python
# core/runner.py
import asyncio
from typing import List
from datetime import datetime
from core.db import DatabaseManager
from core.models import NormalizedListing, ScraperRunLog
from scrapers.remax_scraper import RemaxScraper
from scrapers.mmreality_scraper import MmRealityScraper
import logging

logger = logging.getLogger(__name__)

class ScraperRunner:
    def __init__(self, db: DatabaseManager):
        self.db = db
        self.scrapers = [
            RemaxScraper(),
            MmRealityScraper(),
            # ... další scrapers
        ]
    
    async def run_all(self):
        """Spustí všechny scrapers"""
        logger.info("Starting scraper run...")
        
        for scraper in self.scrapers:
            await self.run_scraper(scraper)
        
        logger.info("Scraper run completed.")
    
    async def run_scraper(self, scraper):
        """Spustí jeden scraper"""
        run_log = ScraperRunLog(
            source_id=scraper.source_id,
            started_at=datetime.utcnow()
        )
        
        try:
            logger.info(f"Running {scraper.source_name} scraper...")
            
            # Fetch listings
            raw_listings = scraper.fetch_listings(max_pages=5)
            logger.info(f"Found {len(raw_listings)} raw listings")
            
            for raw in raw_listings:
                try:
                    # Fetch detail
                    html = scraper.fetch_listing_detail(raw)
                    
                    # Normalize
                    normalized = scraper.normalize(raw, html)
                    
                    # Upsert do DB
                    is_new = await self.db.upsert_listing(normalized)
                    
                    if is_new:
                        run_log.new_count += 1
                    else:
                        run_log.updated_count += 1
                    
                except Exception as e:
                    logger.error(f"Error processing {raw.url}: {e}")
                    run_log.error_count += 1
                    run_log.errors.append(str(e))
            
        except Exception as e:
            logger.error(f"Fatal error in {scraper.source_name}: {e}")
            run_log.errors.append(str(e))
        
        finally:
            run_log.finished_at = datetime.utcnow()
            await self.db.save_run_log(run_log)
            
            logger.info(f"{scraper.source_name} done: "
                       f"{run_log.new_count} new, "
                       f"{run_log.updated_count} updated, "
                       f"{run_log.error_count} errors")
```

---

## 🎨 Frontend - Blazor komponenty

### Dashboard Component (ukázka)

```razor
@page "/"
@using RealEstate.App.Services
@using RealEstate.Api.DTOs
@inject IListingApiService ListingApi

<MudContainer MaxWidth="MaxWidth.ExtraExtraLarge" Class="mt-4">
    <MudText Typo="Typo.h4" Class="mb-4">Real Estate Aggregator</MudText>
    
    <!-- Filtrační panel -->
    <MudExpansionPanels>
        <MudExpansionPanel Text="Filtry" IsInitiallyExpanded="true">
            <FilterPanel @bind-Filter="filter" OnApplyFilters="LoadListingsAsync" />
        </MudExpansionPanel>
    </MudExpansionPanels>
    
    <!-- Data Grid -->
    <MudDataGrid T="ListingSummaryDto" 
                 Items="@listings" 
                 Loading="@isLoading"
                 Elevation="4"
                 Class="mt-4">
        <Columns>
            <PropertyColumn Property="x => x.Source.Name" Title="Zdroj">
                <CellTemplate>
                    <MudChip Size="Size.Small">@context.Item.Source.Name</MudChip>
                </CellTemplate>
            </PropertyColumn>
            
            <PropertyColumn Property="x => x.Title" Title="Název" />
            
            <PropertyColumn Property="x => x.LocationText" Title="Lokalita" />
            
            <PropertyColumn Property="x => x.Price" Title="Cena" Format="N0" />
            
            <PropertyColumn Property="x => x.AreaBuiltUp" Title="Plocha (m²)" />
            
            <PropertyColumn Property="x => x.FirstSeenAt" Title="Přidáno" Format="dd.MM.yyyy" />
            
            <TemplateColumn Title="Akce">
                <CellTemplate>
                    <MudButtonGroup Size="Size.Small">
                        <MudIconButton Icon="@Icons.Material.Filled.Visibility" 
                                       OnClick="@(() => ShowDetail(context.Item.Id))" />
                        <MudIconButton Icon="@Icons.Material.Filled.Favorite" 
                                       Color="Color.Error" />
                        <MudIconButton Icon="@Icons.Material.Filled.Analytics" 
                                       OnClick="@(() => CreateAnalysis(context.Item.Id))" />
                    </MudButtonGroup>
                </CellTemplate>
            </TemplateColumn>
        </Columns>
        
        <PagerContent>
            <MudDataGridPager T="ListingSummaryDto" />
        </PagerContent>
    </MudDataGrid>
</MudContainer>

@code {
    private List<ListingSummaryDto> listings = new();
    private ListingFilterDto filter = new();
    private bool isLoading = false;
    
    protected override async Task OnInitializedAsync()
    {
        await LoadListingsAsync();
    }
    
    private async Task LoadListingsAsync()
    {
        isLoading = true;
        StateHasChanged();
        
        try
        {
            var result = await ListingApi.GetListingsAsync(filter);
            listings = result.Items.ToList();
        }
        catch (Exception ex)
        {
            // Error handling
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }
    
    private async Task ShowDetail(Guid listingId)
    {
        // Otevřít dialog s detailem
    }
    
    private async Task CreateAnalysis(Guid listingId)
    {
        // Vytvořit analýzu
    }
}
```

---

## 🔌 API integrace - Cloud Storage

### Google Drive Service

```csharp
namespace RealEstate.Infrastructure.CloudStorage;

public class GoogleDriveService : IGoogleDriveService
{
    private readonly DriveService _driveService;
    private readonly IConfiguration _configuration;
    
    public GoogleDriveService(IConfiguration configuration)
    {
        _configuration = configuration;
        
        var credential = GoogleCredential.FromFile("credentials.json")
            .CreateScoped(DriveService.Scope.Drive);
        
        _driveService = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "RealEstateAggregator"
        });
    }
    
    public async Task<string> UploadFileAsync(
        string fileName, 
        Stream fileStream, 
        string mimeType = "text/markdown")
    {
        var folderId = _configuration["GoogleDrive:FolderId"];
        
        var fileMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = fileName,
            Parents = new[] { folderId }
        };
        
        var request = _driveService.Files.Create(
            fileMetadata, 
            fileStream, 
            mimeType);
        
        request.Fields = "id, webViewLink";
        
        var file = await request.UploadAsync();
        
        if (file.Status != UploadStatus.Completed)
            throw new Exception($"Upload failed: {file.Exception?.Message}");
        
        var uploadedFile = request.ResponseBody;
        
        // Nastavit oprávnění na "anyone with link"
        await SetPublicPermission(uploadedFile.Id);
        
        return uploadedFile.WebViewLink;
    }
    
    private async Task SetPublicPermission(string fileId)
    {
        var permission = new Permission
        {
            Type = "anyone",
            Role = "reader"
        };
        
        await _driveService.Permissions.Create(permission, fileId).ExecuteAsync();
    }
}
```

---

## ⚙️ Konfigurace & Nastavení

### appsettings.json (.NET)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=realestate_dev;Username=postgres;Password=dev"
  },
  "GoogleDrive": {
    "FolderId": "your-folder-id-here",
    "CredentialsPath": "credentials.json"
  },
  "OneDrive": {
    "ClientId": "your-client-id",
    "ClientSecret": "your-client-secret",
    "TenantId": "your-tenant-id",
    "FolderPath": "/RealEstateAnalyses"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

### settings.yaml (Python)

```yaml
database:
  host: localhost
  port: 5432
  database: realestate_dev
  user: postgres
  password: dev

scraping:
  max_pages_per_source: 10
  request_timeout: 30
  delay_between_requests: 1.0  # seconds
  user_agent: "Mozilla/5.0 (compatible; RealEstateScraper/1.0)"

sources:
  - id: "uuid-remax"
    name: "Remax"
    base_url: "https://www.remax-czech.cz"
    enabled: true
    
  - id: "uuid-mmreality"
    name: "MM Reality"
    base_url: "https://www.mmreality.cz"
    enabled: true

scheduler:
  cron: "0 8,20 * * *"  # Každý den v 8:00 a 20:00

logging:
  level: INFO
  file: logs/scraper.log
```

### docker-compose.yml

```yaml
version: '3.8'

services:
  postgres:
    image: postgres:15
    environment:
      POSTGRES_DB: realestate_dev
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: dev
    ports:
      - "5432:5432"
    volumes:
      - postgres_data:/var/lib/postgresql/data
  
  api:
    build:
      context: .
      dockerfile: src/RealEstate.Api/Dockerfile
    ports:
      - "5001:80"
    environment:
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=realestate_dev;Username=postgres;Password=dev"
    depends_on:
      - postgres
  
  scraper:
    build:
      context: scraper
      dockerfile: Dockerfile
    environment:
      DB_HOST: postgres
      DB_NAME: realestate_dev
      DB_USER: postgres
      DB_PASSWORD: dev
    depends_on:
      - postgres

volumes:
  postgres_data:
```

---

## 🚀 Deployment

### Fáze nasazení

1. **Development** (lokální)
   - Docker Compose
   - PostgreSQL local
   - .NET dev server
   - Python venv

2. **Staging** (testovací)
   - Azure App Service / AWS ECS
   - PostgreSQL managed (Azure Database)
   - Automated scraping (Azure Functions / AWS Lambda)

3. **Production**
   - Stejné jako Staging, ale s produkčním DB
   - Monitoring (Application Insights / CloudWatch)
   - Backup strategie

---

## 🔒 Bezpečnost

- **API**: Minimálně API key pro MVP, později JWT auth
- **Database**: Encrypted connections (SSL)
- **Secrets**: Azure Key Vault / AWS Secrets Manager
- **Scraping**: Respektování robots.txt, rate limiting

---

## 🤖 RAG + AI Architektura (Session 5–6, únor 2026)

### Přehled

Aplikace integruje lokální AI pro sémantické vyhledávání a chat nad inzeráty. Veškeré zpracování probíhá lokálně (Ollama na M2 Ultra) bez odesílání dat do cloudu.

```
Blazor UI (ListingDetail)
    │ POST /api/listings/{id}/analyses
    │ POST /api/listings/{id}/ask
    ▼
RagService (.NET)
    │ embeddingy           │ chat
    ▼                      ▼
OllamaEmbeddingService   Ollama :11434
    ▼                      nomic-embed-text (768 dim)
PostgreSQL pgvector        qwen2.5:14b (9 GB)
listing_analyses
    ↑
MCP Server (Python FastMCP 3.x :8002)
    ← stdio (Claude Desktop)
    ← SSE/HTTP (Docker)
```

### Databázová entita `listing_analyses`

```sql
CREATE TABLE re_realestate.listing_analyses (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id  uuid        NOT NULL REFERENCES re_realestate.listings(id) ON DELETE CASCADE,
    content     text        NOT NULL,
    embedding   vector(768),           -- NULL dokud není embedováno
    source      text        NOT NULL DEFAULT 'manual',  -- manual|claude|mcp|auto
    title       text,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_listing_analyses_embedding
    ON re_realestate.listing_analyses USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);
```

### Embedding providers

| Provider | Model | Dimenze | Cena | Offline |
|---|---|---|---|---|
| **Ollama** (primární) | `nomic-embed-text` | 768 | Zdarma | ✅ |
| **OpenAI** (fallback) | `text-embedding-3-small` | 1536 | $0.02/1M | ❌ |

Přepínání přes `Embedding__Provider=ollama|openai` env var.

### Ingestor pattern

Každý zdroj dat (ruční poznámka, AI závěr, popis inzerátu, PDF, e-mail) se ukládá jako jeden záznam v `listing_analyses` s různým `source`:

| Source | Spuštění |
|---|---|
| `manual` | UI nebo Claude Desktop |
| `claude` | MCP `save_analysis` tool |
| `auto` | `POST /api/listings/{id}/embed-description` (idempotentní) |
| `import` | Vlastní ingestor (PDF, e-mail...) |

### RAG endpointy

| Metoda | Cesta | Popis |
|---|---|---|
| `POST` | `/api/listings/{id}/analyses` | Uložit analýzu + embedding |
| `GET` | `/api/listings/{id}/analyses` | Seznam analýz inzerátu |
| `DELETE` | `/api/listings/{id}/analyses/{aId}` | Smazat analýzu |
| `POST` | `/api/listings/{id}/ask` | RAG chat pro jeden inzerát |
| `POST` | `/api/rag/ask` | RAG chat napříč všemi inzeráty |
| `GET` | `/api/rag/status` | Stav RAG (počty, provider) |
| `POST` | `/api/listings/{id}/embed-description` | Auto-embed popisu (idempotentní) |
| `POST` | `/api/rag/embed-descriptions` | Batch embed všech bez `auto` analýzy |

### MCP Server

**Soubor:** `mcp/server.py` – FastMCP 3.x, 9 nástrojů

| Tool | Popis |
|---|---|
| `search_listings` | Hledání inzerátů s filtry |
| `get_listing` | Detail inzerátu |
| `get_analyses` | Analýzy inzerátu |
| `save_analysis` | Uložit + embedovat analýzu |
| `ask_listing` | RAG chat pro inzerát |
| `ask_general` | RAG chat napříč všemi |
| `list_sources` | Aktivní zdroje |
| `get_rag_status` | Stav RAG systému |
| `embed_description` | Auto-embed popisu inzerátu |
| `bulk_embed_descriptions` | Dávkový embed (limit N) |

### Cloud Storage Export s retry (Session 6)

`GoogleDriveExportService` + `OneDriveExportService`:
- Retry 3× s exponenciálním backoff (2s, 4s, 6s)
- HTTP timeout 30 s pro stahování fotek
- `DriveExportResultDto` obsahuje `PhotosUploaded`, `PhotosTotal`, `AllPhotosUploaded`
- UI badge zelený/oranžový podle úplnosti exportu

---

## 📖 Dokumentační soubory

| Soubor | Obsah |
|---|---|
| `docs/TECHNICAL_DESIGN.md` | Tento soubor – architektura a technická rozhodnutí |
| `docs/API_CONTRACTS.md` | API endpointy (request/response příklady) |
| `docs/RAG_MCP_DESIGN.md` | Detailní design RAG + MCP serveru |
| `docs/AI_SESSION_SUMMARY.md` | Historie sessions + changelog |
| `docs/BACKLOG.md` | Product backlog |
| `QUICK_START.md` | Jak rychle spustit celý stack |

---

**Konec technické dokumentace** • Verze 1.1 • 25. února 2026
