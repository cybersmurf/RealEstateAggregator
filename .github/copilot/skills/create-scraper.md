# Create Scraper Skill

## Description
Create a new real estate scraper for RealEstateAggregator Python backend.

## Usage
```bash
gh copilot suggest "create scraper for sreality.cz"
```

## Steps

1. **Create scraper file**
   - Location: `scraper/core/scrapers/{sitename}_scraper.py`
   - Copy template from `remax_scraper.py`
   - Update class name: `{SiteName}Scraper`

2. **Configure base settings**
   ```python
   class SrealityScraper:
       BASE_URL = "https://sreality.cz"
       SOURCE_CODE = "SREALITY"  # Must match DB sources.code
       
       def __init__(self):
           self.logger = logging.getLogger(__name__)
   ```

3. **Implement core methods**
   - `async def run(self, full_rescan: bool = False) -> int`
     - Entry point, sets max_pages based on full_rescan
   - `async def scrape(self, max_pages: int) -> int`
     - Main scraping loop (fetch list → parse → fetch detail → save)
   - `async def _fetch_page(self, client: httpx.AsyncClient, url: str) -> str`
     - HTTP GET with retry logic
   - `def _parse_list_page(self, html: str) -> List[Dict[str, Any]]`
     - Extract listing URLs + basic info (use regex for robustness)
   - `def _parse_detail_page(self, html: str, item: Dict) -> Dict[str, Any]`
     - Extract: title, price, location, property_type, offer_type, area, photos

4. **Use defensive parsing**
   ```python
   def _parse_detail_page(self, html: str, item: Dict) -> Dict[str, Any]:
       soup = BeautifulSoup(html, "html.parser")
       
       # ✅ Good - defensive with fallback
       title_elem = soup.find("h1", class_="title")
       title = title_elem.get_text(strip=True) if title_elem else "Unknown Title"
       
       # Extract price with regex (more robust than CSS selectors)
       price_text = soup.find(text=re.compile(r'\d+\s*(Kč|CZK)'))
       price = self._parse_price(price_text) if price_text else None
       
       # Photos with fallback
       photos = [img.get("src") for img in soup.select(".gallery img") if img.get("src")]
       
       return {
           "external_id": item["external_id"],
           "url": item["url"],
           "title": title,
           "price": price,
           "property_type": "Byt",  # Czech name
           "offer_type": "Prodej",
           "photos": photos[:20],  # Max 20
       }
   ```

5. **Add to runner.py**
   ```python
   # scraper/core/runner.py
   from ..core.scrapers.sreality_scraper import SrealityScraper
   
   # In run_scraping function:
   if "SREALITY" in source_codes:
       scraper = SrealityScraper()
       count = await scraper.run(full_rescan=request.full_rescan)
       results[scraper.SOURCE_CODE] = count
   ```

6. **Seed database**
   ```bash
   docker exec -it realestate-db psql -U postgres -d realestate_dev
   ```
   ```sql
   INSERT INTO re_realestate.sources (id, code, name, base_url, is_active)
   VALUES (gen_random_uuid(), 'SREALITY', 'Sreality.cz', 'https://sreality.cz', true);
   ```

7. **Test scraper**
   ```bash
   cd scraper
   
   # Test parsing (dry run)
   python -c "
   from core.scrapers.sreality_scraper import SrealityScraper
   import asyncio
   scraper = SrealityScraper()
   asyncio.run(scraper.scrape(max_pages=1))
   "
   
   # Test via API
   curl -X POST http://localhost:8001/v1/scrape/run \
     -H "Content-Type: application/json" \
     -d '{"source_codes":["SREALITY"],"full_rescan":false}'
   ```

## Checklist
- [ ] Scraper file created in `scraper/core/scrapers/`
- [ ] BASE_URL and SOURCE_CODE configured
- [ ] All 5 core methods implemented
- [ ] Defensive parsing with fallbacks (if elem: ...)
- [ ] Czech→English enum mapping for property_type/offer_type
- [ ] Photos limited to max 20
- [ ] Added to runner.py
- [ ] Source seeded in database
- [ ] Tested with max_pages=1 (no DB writes)
- [ ] Tested with API call (DB writes verified)

## Common Pitfalls
- **Regex over CSS selectors** - Websites change class names frequently
- **Rate limiting** - Add `await asyncio.sleep(1)` between detail requests
- **Transaction safety** - Database manager handles this automatically
- **Error handling** - Log exceptions but continue scraping other items
- **Czech enums** - Use "Byt"/"Dům", not "Apartment"/"House" (DB converts)

## Related Files
- `scraper/core/scrapers/remax_scraper.py` (template)
- `scraper/core/runner.py` (register scraper)
- `scraper/core/database.py` (upsert_listing)
- `scripts/init-db.sql` (sources table)
