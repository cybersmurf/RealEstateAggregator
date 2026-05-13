"""
RealEstate MCP Server
======================
Model Context Protocol server pro RealEstateAggregator.
Umožňuje AI asistentům (Claude Desktop, VS Code Copilot, Cursor…) volat nástroje
pro vyhledávání, analýzu a RAG dotazy nad realitními inzeráty.

Spuštění (stdio – Claude Desktop):
    python server.py

Spuštění (HTTP/SSE – Docker, vzdálené):
    TRANSPORT=sse python server.py

Konfigurace Claude Desktop (~/.config/claude/claude_desktop_config.json):
    {
      "mcpServers": {
        "realestate": {
          "command": "python",
          "args": ["/path/to/mcp/server.py"],
          "env": { "API_BASE_URL": "http://localhost:5001" }
        }
      }
    }
"""

import os
import json
import logging
import base64
import io
from PIL import Image
from typing import Optional
import httpx
from fastmcp import FastMCP
from mcp.types import ImageContent, TextContent


def _cap_output(text: str, max_chars: int = 0) -> str:
    """Zkrátí textový výstup nástroje na max_chars znaků, aby se nepřekročil kontext."""
    if max_chars <= 0:
        max_chars = MAX_OUTPUT_CHARS
    if len(text) <= max_chars:
        return text
    truncated = text[:max_chars]
    # Odsekni na poslední celý řádek
    last_newline = truncated.rfind("\n")
    if last_newline > max_chars // 2:
        truncated = truncated[:last_newline]
    used_pct = len(truncated) * 100 // len(text)
    return (
        truncated
        + f"\n\n---\n⚠️ **Výstup zkrácen na {MAX_OUTPUT_CHARS:,} znaků** "
        + f"({used_pct}% z {len(text):,}). "
        + "Použij stránkování (page=N) pro zobrazení dalšího obsahu."
    )


def _resize_image(raw_bytes: bytes, max_width: int = 800, quality: int = 60) -> bytes:
    """Resize image to max_width keeping aspect ratio, compress to JPEG."""
    try:
        img = Image.open(io.BytesIO(raw_bytes))
        if img.mode in ("RGBA", "P"):
            img = img.convert("RGB")
        w, h = img.size
        if w > max_width:
            new_h = int(h * max_width / w)
            img = img.resize((max_width, new_h), Image.LANCZOS)
        buf = io.BytesIO()
        img.save(buf, format="JPEG", quality=quality, optimize=True)
        return buf.getvalue()
    except Exception:
        return raw_bytes  # fallback: original

# ─── Mistral Vision helper ───────────────────────────────────────────────────

async def _analyze_with_mistral_vision(b64: str, prompt: str, client: httpx.AsyncClient) -> str:
    """Pošle obrázek na Mistral Vision API a vrátí textový popis."""
    resp = await client.post(
        "https://api.mistral.ai/v1/chat/completions",
        headers={"Authorization": f"Bearer {MISTRAL_API_KEY}"},
        json={
            "model": MISTRAL_VISION_MODEL,
            "messages": [{
                "role": "user",
                "content": [
                    {"type": "image_url", "image_url": {"url": f"data:image/jpeg;base64,{b64}"}},
                    {"type": "text", "text": prompt},
                ],
            }],
            "max_tokens": 512,
            "temperature": 0.1,
        },
        timeout=30,
    )
    resp.raise_for_status()
    return resp.json()["choices"][0]["message"]["content"].strip()


# ─── Konfigurace ──────────────────────────────────────────────────────────────

API_BASE_URL = os.getenv("API_BASE_URL", "http://localhost:5001")
# Veřejná URL pro stahování fotek (storedUrl je relativní /uploads/...). Může být jiná než API_BASE_URL
# když API není veřejně dostupné (např. port 15001 blokovaný firewallem na serveru).
# Výchozí = stejná jako API_BASE_URL, ale na produkci nastav na veřejně dostupnou URL (port App).
PHOTOS_BASE_URL = os.getenv("PHOTOS_BASE_URL", API_BASE_URL)
API_TIMEOUT = float(os.getenv("API_TIMEOUT_SECONDS", "30"))
TRANSPORT = os.getenv("TRANSPORT", "stdio")   # "stdio" nebo "sse"
PORT = int(os.getenv("PORT", "8002"))
MISTRAL_API_KEY      = os.getenv("MISTRAL_API_KEY",      "Auf12P50gxnU6Py6l5qokYCBmYfWKtkU")
MISTRAL_VISION_MODEL = os.getenv("MISTRAL_VISION_MODEL", "mistral-small-2506")

# Max znaků které jeden MCP tool vrátí – omezuje výši kontextu a kreditů Claude
MAX_OUTPUT_CHARS = int(os.getenv("MCP_MAX_OUTPUT_CHARS", "200000"))

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("realestate-mcp")

# ─── MCP server ───────────────────────────────────────────────────────────────

mcp = FastMCP(
    name="RealEstate Knowledge Base",
    instructions="""
Jsi asistent specializovaný na analýzu nemovitostí z České republiky.
Máš přístup k databázi realitních inzerátů (1 200+ aktivních) a uloženým analýzám.

Dostupné nástroje:
- search_listings: Vyhledávání inzerátů (text + filtry ceny, typu, nabídky)
- get_listing: Detailní informace o konkrétním inzerátu (text + metadata + fotky jako URL)
- get_listing_photos: 📸 Fotky Z INZERÁTU jako obrázky viditelné v chatu
- get_inspection_photos: 📷 Fotky Z PROHLÍDKY jako obrázky viditelné v chatu
- analyze_inspection_photos: 🔍 AI analýza fotek z prohlídky (Mistral Vision / llava) – místnost, stav, popis, nedostatky
- analyze_listing_photos: 🔍 AI analýza fotek z inzerátu (Mistral Vision / llava) – přehled před prohlídkou
- get_analyses: Zobrazení uložených analýz pro inzerát
- save_analysis: Uložení nové analýzy textu (automaticky se vygeneruje embedding)
- ask_listing: RAG dotaz nad analýzami konkrétního inzerátu
- ask_general: RAG dotaz přes všechny inzeráty
- list_sources: Přehled aktivních realitních zdrojů
- get_rag_status: Stav RAG systému (počty embeddingů)
""",
)


# ─── HTTP helper ──────────────────────────────────────────────────────────────

async def _call_api(method: str, path: str, **kwargs) -> dict | list:
    """Zavolá .NET API a vrátí JSON odpověď."""
    url = f"{API_BASE_URL}{path}"
    async with httpx.AsyncClient(timeout=API_TIMEOUT) as client:
        resp = await getattr(client, method)(url, **kwargs)
        resp.raise_for_status()
        return resp.json()


def _fmt_listing(l: dict) -> str:
    """Formátuje inzerát do čitelného textu."""
    price = f"{l.get('price', 0):,.0f} Kč" if l.get("price") else "Cena neuvedena"
    area = f"{l.get('areaBuiltUp', 0):.0f} m²" if l.get("areaBuiltUp") else ""
    disposition = l.get("disposition", "") or ""
    return (
        f"🏠 **{l['title']}**\n"
        f"   ID: `{l['id']}`\n"
        f"   📍 {l.get('locationText', 'N/A')}  |  💰 {price}"
        f"  |  {disposition} {area}\n"
        f"   Typ: {l.get('propertyType')} | Nabídka: {l.get('offerType')}"
        f"  |  Zdroj: {l.get('sourceName', l.get('sourceCode', ''))}\n"
        f"   🔗 {l.get('url', '')}"
    )


# ─── NÁSTROJE ─────────────────────────────────────────────────────────────────


@mcp.tool()
async def search_listings(
    query: str = "",
    property_type: Optional[str] = None,
    offer_type: Optional[str] = None,
    min_price: Optional[float] = None,
    max_price: Optional[float] = None,
    municipality: Optional[str] = None,
    page: int = 1,
    page_size: int = 10,
) -> str:
    """
    Vyhledá realitní inzeráty v databázi.

    Args:
        query: Volný textový dotaz (např. "rodinný dům Znojmo s bazénem")
        property_type: Typ nemovitosti: House | Apartment | Land | Cottage | Commercial | Garage | Other
        offer_type: Typ nabídky: Sale | Rent | Auction
        min_price: Minimální cena v Kč
        max_price: Maximální cena v Kč
        municipality: Obec (např. "Znojmo", "Štítary")
        page: Číslo stránky (default 1)
        page_size: Počet výsledků (max 50, default 10)
    """
    payload = {
        "searchQuery": query or None,
        "propertyType": property_type,
        "offerType": offer_type,
        "minPrice": min_price,
        "maxPrice": max_price,
        "municipality": municipality,
        "page": page,
        "pageSize": min(page_size, 50),
    }
    # Odstraň None hodnoty
    payload = {k: v for k, v in payload.items() if v is not None}

    result = await _call_api("post", "/api/listings/search", json=payload)

    items = result.get("items", [])
    total = result.get("totalCount", 0)

    if not items:
        return "Nenalezeny žádné inzeráty odpovídající kritériím."

    lines = [f"**Nalezeno {total} inzerátů** (strana {page}):\n"]
    for listing in items:
        lines.append(_fmt_listing(listing))
        lines.append("")

    return _cap_output("\n".join(lines))


@mcp.tool()
async def get_listing(listing_id: str) -> str:
    """
    🔍 Vrátí KOMPLETNÍ detail inzerátu včetně ZÁPISU Z PROHLÍDKY.
    
    Co vrací:
    ─────────
    - 📋 ZÁPIS Z PROHLÍDKY: plný text poznámek z osobní návštěvy
    - 💰 Cena, plocha, dispozice, lokalita
    - 🏠 Typ nemovitosti + typ nabídky (prodej/pronájem/dražba)
    - 🌍 GPS + okres + okres katastr
    - 📸 FOTKY Z INZERÁTU: seznam všech staženého fotek
    - 📷 FOTKY Z PROHLÍDKY: vlastní fotky nahrané během prohlídky
    - ☁️ GOOGLE DRIVE ODKAZ: přímý link na složku s analýzami
    - Status: Visited/Liked/ToVisit/Disliked
    
    Typické workflow:
    ──────────────────
    1. get_listing(id) → přečti si ZÁPIS Z PROHLÍDKY
    2. get_analyses(id) → vidíš co se už napsalo
    3. Vytvoř novou analýzu
    4. save_analysis(id, content) → uloží se do DB + vytvoří embedding
    
    ⚡ KRITICKÉ: Zápis z prohlídky je ZCELA ODLIŠNÝ od popisu na webu!
    Obsahuje osobní pozorování, měření, kvalitativní posouzení.
    Vždy si to přečti PŘED tvorbou analýzy!

    Args:
        listing_id: UUID inzerátu (získáš ho ze search_listings)
    """
    try:
        listing = await _call_api("get", f"/api/listings/{listing_id}")
    except httpx.HTTPStatusError as e:
        if e.response.status_code == 404:
            return f"Inzerát {listing_id} nenalezen."
        raise

    photos = listing.get("photos", [])
    user_state = listing.get("userState") or {}

    result_lines = [
        f"# {listing['title']}",
        f"**ID:** `{listing['id']}`",
        f"**Zdroj:** {listing.get('sourceName', listing.get('sourceCode', ''))}",
        f"**Typ:** {listing.get('propertyType')} | **Nabídka:** {listing.get('offerType')}",
        f"**Cena:** {listing.get('price', 0):,.0f} Kč" if listing.get("price") else "**Cena:** neuvedena",
        f"**Lokalita:** {listing.get('locationText', 'N/A')}",
    ]

    if listing.get("areaBuiltUp"):
        result_lines.append(f"**Plocha zastavěná:** {listing['areaBuiltUp']:.0f} m²")
    if listing.get("areaLand"):
        result_lines.append(f"**Plocha pozemku:** {listing['areaLand']:.0f} m²")
    if listing.get("disposition"):
        result_lines.append(f"**Dispozice:** {listing['disposition']}")
    if listing.get("constructionType"):
        result_lines.append(f"**Konstrukce:** {listing['constructionType']}")
    if listing.get("condition"):
        result_lines.append(f"**Stav:** {listing['condition']}")

    result_lines.append(f"**URL:** {listing.get('sourceUrl') or listing.get('url', '')}")

    # ── Google Drive / OneDrive ───────────────────────────────────────────────
    drive_url = listing.get("driveFolderUrl")
    drive_inspection_url = listing.get("driveInspectionFolderUrl")
    has_onedrive = listing.get("hasOneDriveExport", False)
    if drive_url or has_onedrive:
        result_lines += ["", "## ☁️ Cloud export"]
        if drive_url:
            result_lines.append(f"**Google Drive složka:** {drive_url}")
        if drive_inspection_url:
            result_lines.append(f"**Google Drive – fotky z prohlídky:** {drive_inspection_url}")
        if has_onedrive:
            result_lines.append("**OneDrive:** exportováno ✅")

    # ── Stav a zápis z prohlídky ─────────────────────────────────────────────
    if user_state:
        status = user_state.get("status", "New")
        notes = user_state.get("notes", "")
        updated = (user_state.get("lastUpdated") or "")[:10]
        result_lines += [
            "",
            "## 📋 Stav & zápis z prohlídky",
            f"**Stav:** {status} ({updated})",
        ]
        if notes:
            result_lines += [
                "**Poznámky / zápis z prohlídky:**",
                notes,
            ]
        else:
            result_lines.append("_Žádné poznámky._")

    # ── Fotky z inzerátu – jako URL seznam (Claude si je vyžádá přes get_listing_photos) ──
    result_lines += ["", f"## 📸 Fotky z inzerátu ({len(photos)})"]
    if photos:
        for i, p in enumerate(photos):
            url = p.get("storedUrl") or p.get("originalUrl") or ""
            result_lines.append(f"- {url}")
        result_lines.append("")
        result_lines.append(f"💡 Pro zobrazení fotek zavolej: `get_listing_photos(listing_id='{listing_id}')`")
    else:
        result_lines.append("_Žádné fotky._")

    # ── Fotky z prohlídky – počet a odkaz na dedicated tool ─────────────────
    try:
        insp_photos = await _call_api("get", f"/api/listings/{listing['id']}/inspection-photos")
        if insp_photos:
            result_lines += ["", f"## 📷 Fotky z prohlídky ({len(insp_photos)} – vlastní)"]
            result_lines.append(f"💡 Pro zobrazení fotek zavolej: `get_inspection_photos(listing_id='{listing['id']}')`")
        else:
            result_lines += ["", "## 📷 Fotky z prohlídky", "_Žádné vlastní fotky z prohlídky._"]
    except Exception as e:
        logger.warning(f"Failed to fetch inspection photos: {e}")
        pass  # endpoint neexistuje nebo vrátil chybu – ignoruj

    # ── Popis ────────────────────────────────────────────────────────────────
    result_lines += [
        "",
        "## Popis",
        listing.get("description", "Bez popisu")[:3000],
    ]
    if listing.get("description", "") and len(listing["description"]) > 3000:
        result_lines.append("_[popis zkrácen na 3000 znaků]_")

    return _cap_output("\n".join(result_lines))


@mcp.tool()
async def get_inspection_photos(listing_id: str, page: int = 1, page_size: int = 5) -> list:
    """
    📷 Vrátí fotky z prohlídky jako OBRÁZKY (ne URL).
    Claude je vidí přímo v chatu! Fotky jsou automaticky zmenšeny na max 800px.

    Args:
        listing_id: UUID inzerátu
        page: Stránka (začíná 1, default 1)
        page_size: Počet fotek na stránku (default 10, max 20)
    """
    try:
        photos = await _call_api("get", f"/api/listings/{listing_id}/inspection-photos")
    except httpx.HTTPStatusError as e:
        if e.response.status_code == 404:
            return [TextContent(type="text", text=f"Inzerát {listing_id} nenalezen.")]
        raise

    if not photos:
        return [TextContent(type="text", text=f"Pro inzerát {listing_id} nejsou uloženy žádné vlastní fotky z prohlídky.")]

    page_size = min(max(1, page_size), 20)
    total = len(photos)
    total_pages = (total + page_size - 1) // page_size
    page = max(1, min(page, total_pages))
    start = (page - 1) * page_size
    end = min(start + page_size, total)
    page_photos = photos[start:end]

    result = [TextContent(type="text", text=
        f"**Fotky z prohlídky** – stránka {page}/{total_pages} "
        f"({start+1}–{end} z {total} fotek):\n"
        + (f"➡️ Další: `get_inspection_photos(listing_id='{listing_id}', page={page+1})`" if page < total_pages else "")
    )]

    async with httpx.AsyncClient(timeout=60) as client:
        for i, p in enumerate(page_photos, start + 1):
            filename = p.get('originalFileName', f'photo-{i}.jpg')
            filesize_kb = p.get('fileSizeBytes', 0) // 1024
            raw_url = p.get('url', '')
            # Relativní URL → stahuj přes API_BASE_URL (lokální)
            if raw_url.startswith("/"):
                url = API_BASE_URL.rstrip("/") + raw_url
            else:
                url = raw_url
            result.append(TextContent(type="text", text=f"**{i}. {filename}** (orig. {filesize_kb} KB)"))
            try:
                if url:
                    r = await client.get(url)
                    r.raise_for_status()
                    resized = _resize_image(r.content)
                    b64_data = base64.b64encode(resized).decode()
                    result.append(ImageContent(type="image", data=b64_data, mimeType="image/jpeg"))
            except Exception as e:
                logger.warning(f"Failed to fetch photo {filename}: {e}")
                result.append(TextContent(type="text", text=f"❌ {url} (selhalo: {e})"))

    return result


@mcp.tool()
async def get_listing_photos(listing_id: str, page: int = 1, page_size: int = 5) -> list:
    """
    📸 Vrátí fotky z inzerátu jako OBRÁZKY (ne URL).
    Claude je vidí přímo v chatu! Fotky jsou automaticky zmenšeny na max 800px.

    Args:
        listing_id: UUID inzerátu
        page: Stránka (začíná 1, default 1)
        page_size: Počet fotek na stránku (default 10, max 20)
    """
    try:
        listing = await _call_api("get", f"/api/listings/{listing_id}")
    except httpx.HTTPStatusError as e:
        if e.response.status_code == 404:
            return [TextContent(type="text", text=f"Inzerát {listing_id} nenalezen.")]
        raise

    photos = listing.get("photos", [])
    if not photos:
        return [TextContent(type="text", text="Žádné fotky z inzerátu.")]

    page_size = min(max(1, page_size), 20)
    total = len(photos)
    total_pages = (total + page_size - 1) // page_size
    page = max(1, min(page, total_pages))
    start = (page - 1) * page_size
    end = min(start + page_size, total)
    page_photos = photos[start:end]

    result = [TextContent(type="text", text=
        f"**Fotky z inzerátu** – stránka {page}/{total_pages} "
        f"({start+1}–{end} z {total} fotek):\n"
        + (f"➡️ Další: `get_listing_photos(listing_id='{listing_id}', page={page+1})`" if page < total_pages else "")
    )]

    async with httpx.AsyncClient(timeout=60) as client:
        for i, p in enumerate(page_photos, start + 1):
            stored = p.get("storedUrl") or ""
            original = p.get("originalUrl") or ""
            # storedUrl je relativní /uploads/... → stahuj přes API_BASE_URL (lokální)
            # originalUrl je přímá CDN URL (fallback pokud stored není k dispozici)
            if stored.startswith("/"):
                url = API_BASE_URL.rstrip("/") + stored
            elif stored:
                url = stored
            else:
                url = original
            if not url:
                continue
            result.append(TextContent(type="text", text=f"**{i}.**"))
            try:
                r = await client.get(url)
                r.raise_for_status()
                resized = _resize_image(r.content)
                b64_data = base64.b64encode(resized).decode()
                result.append(ImageContent(type="image", data=b64_data, mimeType="image/jpeg"))
            except Exception as e:
                logger.warning(f"Failed to fetch listing photo {url}: {e}")
                result.append(TextContent(type="text", text=f"❌ {url} (selhalo)"))

    return result


@mcp.tool()
async def analyze_inspection_photos(listing_id: str, page: int = 1, page_size: int = 10, force: bool = False) -> str:
    """
    🔍 Analyzuje fotky z prohlídky pomocí AI vision modelu (llava:7b).
    Výsledky jsou ULOŽENY DO DB – příště se načtou z cache (rychlé).
    Fotky zpracovává po stránkách (default 10 fotek/stránka).

    Args:
        listing_id: UUID inzerátu
        page: Stránka fotek (začíná 1)
        page_size: Počet fotek na stránku (default 10, max 15)
        force: True = přeanalyzuj i fotky co už mají popis (default False)
    """
    PROMPT = (
        "Jsi expert na nemovitosti. Popiš tuto fotografii z prohlídky nemovitosti. "
        "Odpověz VÝHRADNĚ v tomto formátu (každý bod na nový řádek):\n"
        "MÍSTNOST: [typ místnosti – kuchyně/obývací pokoj/ložnice/koupelna/WC/chodba/sklep/garáž/zahrada/exteriér/jiné]\n"
        "STAV: [výborný/dobrý/průměrný/špatný/hrubá stavba]\n"
        "POPIS: [1-2 věty o tom co vidíš – materiály, vybavení, světlo, rozměry]\n"
        "POZOR: [případné nedostatky nebo věci k řešení, nebo 'Žádné']\n"
        "Odpovídej česky."
    )

    try:
        photos = await _call_api("get", f"/api/listings/{listing_id}/inspection-photos")
    except httpx.HTTPStatusError as e:
        if e.response.status_code == 404:
            return f"Inzerát {listing_id} nenalezen."
        raise

    if not photos:
        return f"Pro inzerát {listing_id} nejsou uloženy žádné fotky z prohlídky."

    page_size = min(max(1, page_size), 15)
    total = len(photos)
    total_pages = (total + page_size - 1) // page_size
    page = max(1, min(page, total_pages))
    start = (page - 1) * page_size
    end = min(start + page_size, total)
    page_photos = photos[start:end]

    cached_count = sum(1 for p in page_photos if p.get("aiDescription") and not force)
    lines = [
        f"## 🔍 AI analýza fotek z prohlídky – stránka {page}/{total_pages} ({start+1}–{end} z {total})",
        f"Model: `{MISTRAL_VISION_MODEL}` | Inzerát: `{listing_id}` | 💾 Cache: {cached_count}/{len(page_photos)}\n",
    ]

    async with httpx.AsyncClient(timeout=120) as client:
        for i, p in enumerate(page_photos, start + 1):
            photo_id = p.get("id", "")
            filename = p.get("originalFileName", f"photo-{i}.jpg")
            url = p.get("url", "")
            if url and url.startswith("/"):
                url = API_BASE_URL.rstrip("/") + url
            cached = p.get("aiDescription")
            lines.append(f"\n---\n### Fotka {i}: `{filename}`")

            if not url:
                lines.append("_URL chybí – přeskočeno._")
                continue

            if cached and not force:
                lines.append(f"_(z cache)_\n{cached}")
                continue

            try:
                r = await client.get(url)
                r.raise_for_status()
                resized = _resize_image(r.content, max_width=800, quality=80)
                b64 = base64.b64encode(resized).decode()

                answer = await _analyze_with_mistral_vision(b64, PROMPT, client)
                lines.append(answer)

                # Ulož do DB
                if photo_id:
                    try:
                        await client.patch(
                            f"{API_BASE_URL}/api/listings/{listing_id}/inspection-photos/{photo_id}/ai-description",
                            json={"description": answer},
                            timeout=10,
                        )
                    except Exception as save_err:
                        logger.warning(f"Failed to save ai_description for photo {photo_id}: {save_err}")

            except Exception as e:
                logger.warning(f"Mistral analysis failed for {filename}: {e}")
                lines.append(f"❌ Analýza selhala: {e}")

    if page < total_pages:
        lines.append(
            f"\n---\n➡️ Další stránka: "
            f"`analyze_inspection_photos(listing_id='{listing_id}', page={page + 1})`"
        )

    return _cap_output("\n".join(lines))


@mcp.tool()
async def analyze_listing_photos(listing_id: str, page: int = 1, page_size: int = 10, force: bool = False) -> str:
    """
    🔍 Analyzuje fotky Z INZERÁTU pomocí AI vision modelu (llava:7b).
    Výsledky jsou ULOŽENY DO DB – příště se načtou z cache (rychlé).
    Hodí se pro rychlý přehled nemovitosti ještě před prohlídkou.

    Args:
        listing_id: UUID inzerátu
        page: Stránka fotek (začíná 1)
        page_size: Počet fotek na stránku (default 10, max 15)
        force: True = přeanalyzuj i fotky co už mají popis (default False)
    """
    PROMPT = (
        "Jsi expert na nemovitosti. Popiš tuto fotografii z realitního inzerátu. "
        "Odpověz VÝHRADNĚ v tomto formátu (každý bod na nový řádek):\n"
        "MÍSTNOST: [typ místnosti – kuchyně/obývací pokoj/ložnice/koupelna/WC/chodba/sklep/garáž/zahrada/exteriér/jiné]\n"
        "STAV: [výborný/dobrý/průměrný/špatný/hrubá stavba]\n"
        "POPIS: [1-2 věty o tom co vidíš – materiály, vybavení, světlo, rozměry]\n"
        "POZOR: [případné nedostatky nebo věci k prověření na prohlídce, nebo 'Žádné']\n"
        "Odpovídej česky."
    )

    try:
        listing = await _call_api("get", f"/api/listings/{listing_id}")
    except httpx.HTTPStatusError as e:
        if e.response.status_code == 404:
            return f"Inzerát {listing_id} nenalezen."
        raise

    photos = listing.get("photos", [])
    if not photos:
        return "Inzerát nemá žádné fotky."

    page_size = min(max(1, page_size), 15)
    total = len(photos)
    total_pages = (total + page_size - 1) // page_size
    page = max(1, min(page, total_pages))
    start = (page - 1) * page_size
    end = min(start + page_size, total)
    page_photos = photos[start:end]

    title = listing.get("title", listing_id)
    cached_count = sum(1 for p in page_photos if p.get("aiDescription") and not force)
    lines = [
        f"## 🔍 AI analýza fotek z inzerátu – stránka {page}/{total_pages} ({start+1}–{end} z {total})",
        f"**{title}** | Model: `{MISTRAL_VISION_MODEL}` | 💾 Cache: {cached_count}/{len(page_photos)}\n",
    ]

    async with httpx.AsyncClient(timeout=120) as client:
        for i, p in enumerate(page_photos, start + 1):
            photo_id = p.get("id", "")
            url = p.get("storedUrl") or p.get("originalUrl") or ""
            if url and url.startswith("/"):
                url = API_BASE_URL.rstrip("/") + url
            cached = p.get("aiDescription")
            lines.append(f"\n---\n### Fotka {i}")

            if not url:
                lines.append("_URL chybí – přeskočeno._")
                continue

            if cached and not force:
                lines.append(f"_(z cache)_\n{cached}")
                continue

            try:
                r = await client.get(url)
                r.raise_for_status()
                resized = _resize_image(r.content, max_width=800, quality=80)
                b64 = base64.b64encode(resized).decode()

                answer = await _analyze_with_mistral_vision(b64, PROMPT, client)
                lines.append(answer)

                # Ulož do DB
                if photo_id:
                    try:
                        await client.patch(
                            f"{API_BASE_URL}/api/listings/{listing_id}/photos/{photo_id}/ai-description",
                            json={"description": answer},
                            timeout=10,
                        )
                    except Exception as save_err:
                        logger.warning(f"Failed to save ai_description for listing photo {photo_id}: {save_err}")

            except Exception as e:
                logger.warning(f"Mistral analysis failed for listing photo {i}: {e}")
                lines.append(f"❌ Analýza selhala: {e}")

    if page < total_pages:
        lines.append(
            f"\n---\n➡️ Další stránka: "
            f"`analyze_listing_photos(listing_id='{listing_id}', page={page + 1})`"
        )

    return _cap_output("\n".join(lines))


@mcp.tool()
async def analyze_tovisit_listings(
    force: bool = False,
    max_photos_per_listing: int = 10,
) -> str:
    """
    🏠 Analyzuje fotky VŠECH inzerátů označených 'K návštěvě' pomocí AI vision.

    Spouštěj ručně před plánovanými prohlídkami – připraví si popis
    každé fotky z inzerátu, aby byl detail dostupný bez čekání.
    Výsledky se ukládají do DB – příště se načtou z cache (rychlé).

    Fotky z prohlídky (vlastní upload) analyzuj zvlášť přes
    `analyze_inspection_photos`.

    Args:
        force: True = přeanalyzuj i fotky co už mají popis (default False)
        max_photos_per_listing: Max fotek na inzerát (default 10, max 20)
    """
    PROMPT = (
        "Jsi expert na nemovitosti. Popiš tuto fotografii z realitního inzerátu. "
        "Odpověz VÝHRADNĚ v tomto formátu (každý bod na nový řádek):\n"
        "MÍSTNOST: [typ místnosti – kuchyně/obývací pokoj/ložnice/koupelna/WC/chodba/sklep/garáž/zahrada/exteriér/jiné]\n"
        "STAV: [výborný/dobrý/průměrný/špatný/hrubá stavba]\n"
        "POPIS: [1-2 věty o tom co vidíš – materiály, vybavení, světlo, rozměry]\n"
        "POZOR: [případné nedostatky nebo věci k prověření na prohlídce, nebo 'Žádné']\n"
        "Odpovídej česky."
    )

    max_photos_per_listing = min(max(1, max_photos_per_listing), 20)

    # 1. Načti všechny ToVisit inzeráty
    result = await _call_api(
        "post",
        "/api/listings/search",
        json={"userStatus": "ToVisit", "pageSize": 100, "page": 1},
    )
    listings = result.get("items", [])
    total_listings = result.get("totalCount", 0)

    if not listings:
        return "✅ Žádné inzeráty označené 'K návštěvě' nebyly nalezeny."

    # Pokud je stránek více, načti všechny
    if total_listings > 100:
        all_listings = list(listings)
        page = 2
        while len(all_listings) < total_listings:
            batch = await _call_api(
                "post",
                "/api/listings/search",
                json={"userStatus": "ToVisit", "pageSize": 100, "page": page},
            )
            batch_items = batch.get("items", [])
            if not batch_items:
                break
            all_listings.extend(batch_items)
            page += 1
        listings = all_listings

    lines = [
        f"## 🏠 AI analýza fotek inzerátů 'K návštěvě'",
        f"**Celkem inzerátů:** {len(listings)} | **Model:** `{MISTRAL_VISION_MODEL}`",
        f"**Fotek max / inzerát:** {max_photos_per_listing} | **force:** {force}\n",
    ]

    total_photos = 0
    total_cached = 0
    total_analyzed = 0
    total_failed = 0

    async with httpx.AsyncClient(timeout=120) as client:
        for listing_summary in listings:
            listing_id = listing_summary.get("id", "")
            title = listing_summary.get("title", listing_id)

            # Načti detail inzerátu (obsahuje fotky s aiDescription)
            try:
                listing = await _call_api("get", f"/api/listings/{listing_id}")
            except Exception as e:
                lines.append(f"\n### ❌ {title}\nChyba načtení: {e}")
                continue

            photos = listing.get("photos", [])
            if not photos:
                lines.append(f"\n### ⬜ {title}\n_Bez fotek._")
                continue

            # Omez počet fotek
            photos_to_process = photos[:max_photos_per_listing]
            cached = sum(1 for p in photos_to_process if p.get("aiDescription") and not force)
            to_analyze = len(photos_to_process) - cached

            lines.append(
                f"\n### 🏡 {title}\n"
                f"Fotek: {len(photos)} | Zpracovávám: {len(photos_to_process)} "
                f"| 💾 Cache: {cached} | 🔍 Nové: {to_analyze}"
            )

            listing_analyzed = 0
            listing_failed = 0

            for i, p in enumerate(photos_to_process, 1):
                photo_id = p.get("id", "")
                url = p.get("storedUrl") or p.get("originalUrl") or ""
                cached_desc = p.get("aiDescription")

                total_photos += 1

                if not url:
                    continue

                if cached_desc and not force:
                    total_cached += 1
                    continue

                try:
                    r = await client.get(url)
                    r.raise_for_status()
                    resized = _resize_image(r.content, max_width=800, quality=80)
                    b64 = base64.b64encode(resized).decode()

                    answer = await _analyze_with_mistral_vision(b64, PROMPT, client)
                    listing_analyzed += 1
                    total_analyzed += 1

                    # Ulož do DB
                    if photo_id:
                        try:
                            await client.patch(
                                f"{API_BASE_URL}/api/listings/{listing_id}/photos/{photo_id}/ai-description",
                                json={"description": answer},
                                timeout=10,
                            )
                        except Exception as save_err:
                            logger.warning(
                                f"Failed to save ai_description for photo {photo_id}: {save_err}"
                            )

                except Exception as e:
                    logger.warning(f"Vision analysis failed for listing {listing_id} photo {i}: {e}")
                    listing_failed += 1
                    total_failed += 1

            status_icon = "✅" if listing_failed == 0 else "⚠️"
            lines.append(
                f"{status_icon} Hotovo: {listing_analyzed} nových, "
                f"{cached} z cache, {listing_failed} chyb"
            )

    lines.append(
        f"\n---\n## 📊 Celkový přehled\n"
        f"- 📷 Fotek zpracováno: **{total_photos}**\n"
        f"- 🔍 Nově analyzováno: **{total_analyzed}**\n"
        f"- 💾 Z cache: **{total_cached}**\n"
        f"- ❌ Chyb: **{total_failed}**"
    )

    return _cap_output("\n".join(lines))


@mcp.tool()
async def get_analyses(listing_id: str) -> str:
    """
    📊 Vrátí VŠECHNY uložené analýzy pro konkrétní inzerát.
    
    Obsahuje:
    - Plný obsah každé analýzy (bez zkrácení!)
    - Nadpis a zdroj (claude | mcp | manual | ai | ...)
    - Dátu vytvoření analýzy
    - Status embeddingu (zda je prohledávatelná přes RAG)
    - ID analýzy (pro případné smazání)
    
    DŮLEŽITÉ: Jsou tu VŠECHNY analýzy které kdy byly uloženy, 
    ne jen ty nejnovější! Skrz historii vidíš evoluci posouzení.

    Args:
        listing_id: UUID inzerátu (získáš ho ze search_listings nebo get_listing)
    """
    try:
        analyses = await _call_api("get", f"/api/listings/{listing_id}/analyses")
    except httpx.HTTPStatusError as e:
        if e.response.status_code == 404:
            return f"Inzerát {listing_id} nenalezen."
        raise

    if not analyses:
        return f"Pro inzerát {listing_id} nejsou uloženy žádné analýzy."

    lines = [f"**{len(analyses)} analýz** pro inzerát `{listing_id}`:\n"]
    for a in analyses:
        emb = "✅ embedding" if a.get("hasEmbedding") else "❌ bez embeddingu"
        lines.append(
            f"### [{a.get('title') or 'bez názvu'}] – {a.get('source', 'manual')} – {emb}"
        )
        lines.append(f"*{a.get('createdAt', '')[:10]}*")
        lines.append(f"`ID: {a['id']}`")
        content = a.get("content", "")
        lines.append(content)  # plný obsah bez zkrácení
        lines.append("")

    return _cap_output("\n".join(lines))


@mcp.tool()
async def save_analysis(
    listing_id: str,
    content: str,
    title: Optional[str] = None,
    source: str = "claude",
) -> str:
    """
    💾 Uloží NOVOU analýzu inzerátu do databáze.
    
    Automaticky se vygeneruje pgvector embedding (pokud je OpenAI klíč nakonfigurován),
    takže text bude prohledatelný přes RAG a bude dostupný pro budoucí dotazy.
    
    Workflow:
    1. Zavolej get_listing() → přečti si všechna data (ZÁPIS Z PROHLÍDKY!)
    2. Zavolej get_analyses() → vidíš všechny dosavadní analýzy
    3. Vytvoř novou analýzu v Markdown formátu
    4. Zavolej save_analysis() → uloží se a bude prohledávatelná
    
    POZOR: Uložené analýzy jsou vidět všem nástrojům (RAG dotazování, 
    další analýzy, UI). Neukládej sem draft či nejisté věci!

    Args:
        listing_id: UUID inzerátu
        content: Plný text analýzy (markdown, plain text – libovolná délka)
        title: Volitelný nadpis (např. "Analýza z prohlídky 26.2.2026")
        source: Původ: "claude" (default) | "mcp" | "manual" | "ai" | "perplexity"
    """
    payload = {
        "content": content,
        "title": title,
        "source": source,
    }
    try:
        result = await _call_api(
            "post", f"/api/listings/{listing_id}/analyses", json=payload
        )
    except httpx.HTTPStatusError as e:
        if e.response.status_code == 404:
            return f"Inzerát {listing_id} nenalezen."
        raise

    emb_status = "✅ embedding vygenerován" if result.get("hasEmbedding") else "⚠️ bez embeddingu (OpenAI nenastaveno)"
    return (
        f"✅ Analýza uložena\n"
        f"ID: `{result['id']}`\n"
        f"Inzerát: `{listing_id}`\n"
        f"Zdroj: {result.get('source')}\n"
        f"Embedding: {emb_status}\n"
        f"Délka: {len(content)} znaků"
    )


@mcp.tool()
async def ask_listing(
    listing_id: str,
    question: str,
    top_k: int = 5,
) -> str:
    """
    Položí RAG dotaz nad uloženými analýzami konkrétního inzerátu.
    Použije pgvector pro nalezení nejrelevantnějších částí analýz a pošle je jako kontext do OpenAI.

    Args:
        listing_id: UUID inzerátu
        question: Otázka v přirozeném jazyce (česky nebo anglicky)
        top_k: Počet nejpodobnějších analýz použitých jako kontext (default 5)
    """
    payload = {"question": question, "topK": top_k}
    try:
        result = await _call_api(
            "post", f"/api/listings/{listing_id}/ask", json=payload
        )
    except httpx.HTTPStatusError as e:
        if e.response.status_code == 404:
            return f"Inzerát {listing_id} nenalezen."
        raise

    answer = result.get("answer", "")
    sources = result.get("sources", [])
    has_emb = result.get("hasEmbeddings", False)

    lines = [answer, ""]
    if sources:
        lines.append(f"---\n*Použité zdroje ({len(sources)}):*")
        for s in sources:
            sim = s.get("similarity", 0)
            lines.append(
                f"- [{s.get('title') or 'analýza'}] "
                f"{s.get('source')} | podobnost: {sim:.2%} | `{s['analysisId']}`"
            )
    if not has_emb:
        lines.append("\n⚠️ Podobnostní vyhledávání nebylo použito (analyzy nemají embedding nebo OpenAI není nakonfigurováno).")

    return _cap_output("\n".join(lines))


@mcp.tool()
async def ask_general(
    question: str,
    top_k: int = 5,
) -> str:
    """
    Položí RAG dotaz přes analýzy VŠECH inzerátů v databázi.
    Ideální pro otázky jako "který inzerát má největší pozemek pod 2M Kč?" nebo
    "porovnej výhody inzerátů z Moravy".

    Args:
        question: Otázka v přirozeném jazyce
        top_k: Počet nejpodobnějších analýz z celé databáze (default 5)
    """
    payload = {"question": question, "topK": top_k}
    result = await _call_api("post", "/api/rag/ask", json=payload)

    answer = result.get("answer", "")
    sources = result.get("sources", [])

    lines = [answer, ""]
    if sources:
        lines.append(f"---\n*Použité zdroje ({len(sources)}):*")
        for s in sources:
            sim = s.get("similarity", 0)
            lines.append(
                f"- [{s.get('title') or 'analýza'}] "
                f"inzerát `{s.get('listingId', s.get('analysisId'))}` | "
                f"podobnost: {sim:.2%}"
            )

    return _cap_output("\n".join(lines))


@mcp.tool()
async def list_sources() -> str:
    """
    Vrátí seznam aktivních realitních zdrojů (portálů) a počty jejich inzerátů.
    """
    sources = await _call_api("get", "/api/sources")

    if not sources:
        return "Žádné aktivní zdroje nenalezeny."

    lines = [f"**{len(sources)} aktivních zdrojů:**\n"]
    for s in sources:
        lines.append(
            f"- **{s.get('name', s.get('code'))}** (`{s.get('code')}`)"
            f" – {s.get('listingCount', '?')} inzerátů | {s.get('baseUrl', '')}"
        )
    return "\n".join(lines)


@mcp.tool()
async def get_rag_status() -> str:
    """
    Vrátí stav RAG systému: počty analýz, embeddingů a zda je OpenAI nakonfigurováno.
    """
    status = await _call_api("get", "/api/rag/status")

    configured = status.get("openAiConfigured", False)
    emb_icon = "✅" if configured else "❌"

    return (
        f"## RAG Status\n"
        f"{emb_icon} **OpenAI:** {'nakonfigurováno' if configured else 'NENÍ nakonfigurováno (embeddingy nefungují)'}\n"
        f"📝 **Celkem analýz:** {status.get('totalAnalyses', 0)}\n"
        f"🔢 **S embeddingem:** {status.get('withEmbedding', 0)}\n"
        f"⚠️ **Bez embeddingu:** {status.get('withoutEmbedding', 0)}\n"
        f"🏠 **Inzerátů s analýzou:** {status.get('listingsWithAnalyses', 0)}"
    )


@mcp.tool()
async def embed_description(listing_id: str) -> str:
    """
    Embeduje popis inzerátu jako 'auto' analýzu do RAG znalostní báze.
    Idempotentní – přeskočí pokud embedding již existuje.
    Je nutné spustit jednou před prvním dotazem (ask_listing).
    """
    result = await _call_api("post", f"/api/listings/{listing_id}/embed-description")
    if result.get("alreadyExists"):
        return "✅ Popis inzerátu je již embedován."
    analysis = result
    has_emb = analysis.get("hasEmbedding", False)
    emb_icon = "✅" if has_emb else "⚠️"
    return (
        f"{emb_icon} Popis embedován jako analýza\n"
        f"ID: {analysis.get('id')}\n"
        f"Titulek: {analysis.get('title')}\n"
        f"Embedding: {'ano' if has_emb else 'ne (Ollama nedostupná?)'}"
    )


@mcp.tool()
async def bulk_embed_descriptions(limit: int = 100) -> str:
    """
    Batch embed popisů inzerátů bez 'auto' analýzy.
    Vhodné pro inicializaci knowledge base.
    limit: maximální počet inzerátů ke zpracování (výchozí 100).
    """
    result = await _call_api("post", "/api/rag/embed-descriptions", json={"limit": limit})
    processed = result.get("processed", 0)
    return f"✅ Zpracováno {processed} inzerátů ({limit} max limit).\n\n{result.get('message', '')}"


# ─── Entrypoint ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    if TRANSPORT == "sse":
        import asyncio
        logger.info("Starting MCP server in SSE mode on %s:%d", "0.0.0.0", PORT)
        asyncio.run(mcp.run_http_async(transport="sse", host="0.0.0.0", port=PORT))
    else:
        logger.info("Starting MCP server in stdio mode (API: %s)", API_BASE_URL)
        mcp.run()
