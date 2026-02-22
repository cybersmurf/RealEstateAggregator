# REMAX Scraper - Dokumentace

## Přehled

Funkční scraper pro REMAX Czech Republic s reálnými selektory otestovanými na živém webu (únor 2026).

## URL Struktura

- **List stránka**: `https://www.remax-czech.cz/reality/vyhledavani/?stranka=1`
- **Detail stránka**: `https://www.remax-czech.cz/reality/detail/{id}/{slug}`
  - Příklad: `https://www.remax-czech.cz/reality/detail/423340/prodej-domu-123-m2-rakovnik`

## Selectorové Strategie

### List Page Parsing

```python
# Najde všechny odkazy na detaily
soup.select('a[href*="/reality/detail/"]')

# Extrahuje external_id z URL pomocí regex
re.search(r'/reality/detail/(\d+)/', href)
```

**Co se extrahuje:**
- `external_id` - ID inzerátu (např. 423340)
- `detail_url` - Kompletní URL detailu
- `title` - Title z textu odkazu

**Deduplikace:** Výsledky se deduplikují podle `external_id` (REMAX často opakuje odkazy).

### Detail Page Parsing

#### Title
```python
h1 = soup.find('h1')
# Výsledek: "Prodej domu 123 m², Rakovník (ID 254-NP00843)"
```

#### Location
```python
# Regex match na text obsahující "ulice", "část obce", "okres"
soup.find_all(string=re.compile(r'ulice|část obce|okres', re.I))
# Výsledek: "ulice Hálkova, Rakovník – část obce Rakovník II"
```

#### Price
```python
# Najde čísla následovaná "Kč"
soup.find(string=re.compile(r'(\d[\d\s]+)\s*Kč'))
# Extrahuje: "7 490 000" → float(7490000)
```

#### Description
```python
# Hledá delší odstavce (>100 znaků) v <p> nebo <div> tagech
for p in soup.find_all(['p', 'div'], limit=20):
    text = p.get_text(' ', strip=True)
    if len(text) > 100:
        description_parts.append(text)
```

#### Area (Plocha)
```python
# Najde všechny výskyty "123 m²" nebo "123 m2"
soup.find_all(string=re.compile(r'(\d+)\s*m[²2]'))

# Rozlišení:
# - "Plocha pozemku: 211 m²" → area_land
# - "Užitná plocha: 123 m²" → area_built_up
```

#### Photos
```python
# Najde <img> tagy s URL obsahujícími "mlsf.remax-czech.cz"
for img in soup.find_all('img'):
    if 'mlsf.remax-czech.cz' in src or '/data/' in src:
        photo_urls.append(photo_url)

# Příklad URL: https://mlsf.remax-czech.cz/data//zs/423340/3006762.jpg
```

#### Property Type (dedukce z titulu)
```python
title_lower = title.lower()

if "dům" in title_lower or "domu" in title_lower or "vila" in title_lower:
    → "Dům"
elif "byt" in title_lower or "bytu" in title_lower:
    → "Byt"
elif "pozemek" in title_lower:
    → "Pozemek"
elif "komerč" in title_lower or "sklado" in title_lower or "kancelář" in title_lower:
    → "Komerční"
else:
    → "Ostatní"
```

#### Offer Type (dedukce z titulu)
```python
if "pronájem" in title_lower:
    → "Pronájem"
else:
    → "Prodej"
```

## Použití

### Základní použití

```python
from scraper.core.scrapers import RemaxScraper

scraper = RemaxScraper()
count = await scraper.scrape(max_pages=5)
print(f"Scraped {count} listings")
```

### S Playwright (pro JS-heavy stránky)

```python
scraper = RemaxScraper(use_playwright_for_details=True)
count = await scraper.scrape(max_pages=5)
```

## Výstupní Struktura

```python
{
    "source_code": "REMAX",
    "external_id": "423340",
    "url": "https://www.remax-czech.cz/reality/detail/423340/...",
    "title": "Prodej domu 123 m², Rakovník (ID 254-NP00843)",
    "location_text": "ulice Hálkova, Rakovník – část obce Rakovník II",
    "price": 7490000.0,
    "description": "Rodinný dům nedaleko centra města...",
    "area_built_up": 123.0,
    "area_land": 211.0,
    "photos": [
        "https://mlsf.remax-czech.cz/data//zs/423340/3006762.jpg",
        "https://mlsf.remax-czech.cz/data//zs/423340/3006750.jpg",
        ...
    ],
    "property_type": "Dům",
    "offer_type": "Prodej"
}
```

## Rate Limiting

- Scraper čeká **1 sekundu** mezi jednotlivými list pages (`asyncio.sleep(1)`)
- Používá `httpx.AsyncClient` s `timeout=30` sekund
- `follow_redirects=True` pro automatické přesměrování

## Logging

Scraper loguje:
- INFO: Začátek/konec scrapingu, počet zpracovaných
- DEBUG: Fetch a parse operace
- ERROR: Chyby při zpracování jednotlivých items

## TODO

- [x] Implementovat DB persistence v `_save_listing()` (✅ Hotovo - asyncpg + upsert)
- [ ] Přidat retry logiku pro failed requests
- [x] Mapování na DB schema (PropertyType enum mapping) (✅ Hotovo - české → anglické)
- [ ] Extrakce dalších detailů (počet pokojů, konstrukce, stav)
- [ ] Unit testy s mock HTML

## Implementace

**Database persistence** (od 22. února 2026):
- Používá `asyncpg` connection pool
- Upsert na základě `(source_id, external_id)` - nové insertuji, existující updatuji
- Automatické mapování enumů: "Dům" → "House", "Prodej" → "Sale"
- Synchronizace fotek do `listing_photos` tabulky

## Poznámky

- **Robustní**: Selektory nejsou závislé na CSS třídách (které se mohou měnit)
- **Regex-based**: Používá regex pro hledání textu místo pevných selectorů
- **Deduplikace**: Automaticky odstraňuje duplicitní odkazy na stejný inzerát
- **Error handling**: Každý item se zpracovává samostatně, chyba v jednom neovlivní ostatní

## Testování

```bash
# Syntax check
python3 -m py_compile scraper/core/scrapers/remax_scraper.py

# Import test
python3 -c "from scraper.core.scrapers import RemaxScraper; print('OK')"
```

## Aktualizace selektorů

Pokud REMAX změní strukturu webu:

1. Načti skutečné HTML: `curl https://www.remax-czech.cz/reality/vyhledavani/ > test.html`
2. Prohlédni strukturu: `grep -A 5 'detail/' test.html`
3. Aktualizuj regex patterny v `_parse_list_page()` a `_parse_detail_page()`
4. Otestuj na živém webu

---

**Datum aktualizace**: 22. února 2026  
**Testováno na**: https://www.remax-czech.cz  
**Stav**: ✅ Funkční (syntax validated)
