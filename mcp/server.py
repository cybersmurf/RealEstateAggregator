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
from typing import Optional
import httpx
from fastmcp import FastMCP

# ─── Konfigurace ──────────────────────────────────────────────────────────────

API_BASE_URL = os.getenv("API_BASE_URL", "http://localhost:5001")
API_TIMEOUT = float(os.getenv("API_TIMEOUT_SECONDS", "30"))
TRANSPORT = os.getenv("TRANSPORT", "stdio")   # "stdio" nebo "sse"
PORT = int(os.getenv("PORT", "8002"))

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
- get_listing: Detailní informace o konkrétním inzerátu včetně fotek
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

    return "\n".join(lines)


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

    # ── Fotky (URL) ──────────────────────────────────────────────────────────
    result_lines += ["", f"## 📸 Fotky z inzerátu ({len(photos)})"]
    if photos:
        for p in photos:
            url = p.get("storedUrl") or p.get("originalUrl") or ""
            result_lines.append(f"- {url}")
    else:
        result_lines.append("_Žádné fotky._")

    # ── Fotky z prohlídky (lokálně uložené) ──────────────────────────────────
    try:
        insp_photos = await _call_api("get", f"/api/listings/{listing['id']}/inspection-photos")
        if insp_photos:
            result_lines += ["", f"## 📷 Fotky z prohlídky ({len(insp_photos)} – vlastní)"]
            for p in insp_photos:
                result_lines.append(f"- {p.get('storedUrl', '')}  _{p.get('originalFileName', '')}_")
        else:
            result_lines += ["", "## 📷 Fotky z prohlídky", "_Žádné vlastní fotky z prohlídky._"]
    except Exception:
        pass  # endpoint neexistuje nebo vrátil chybu – ignoruj

    # ── Popis ────────────────────────────────────────────────────────────────
    result_lines += [
        "",
        "## Popis",
        listing.get("description", "Bez popisu")[:3000],
    ]
    if listing.get("description", "") and len(listing["description"]) > 3000:
        result_lines.append("_[popis zkrácen na 3000 znaků]_")

    return "\n".join(result_lines)


@mcp.tool()
async def get_inspection_photos(listing_id: str) -> str:
    """
    Vrátí seznam fotek z prohlídky (vlastní fotky uložené uživatelem).
    Fotky jsou dostupné jako lokální URL pro přímé zobrazení nebo analýzu.

    Args:
        listing_id: UUID inzerátu
    """
    try:
        photos = await _call_api("get", f"/api/listings/{listing_id}/inspection-photos")
    except httpx.HTTPStatusError as e:
        if e.response.status_code == 404:
            return f"Inzerát {listing_id} nenalezen."
        raise

    if not photos:
        return f"Pro inzerát {listing_id} nejsou uloženy žádné vlastní fotky z prohlídky.\n\nFotky se uloží automaticky při příštím nahrání přes UI → 'Nahrát fotky z prohlídky'."

    lines = [f"**{len(photos)} fotek z prohlídky** pro inzerát `{listing_id}`:\n"]
    for i, p in enumerate(photos, 1):
        lines.append(f"{i}. **{p.get('originalFileName', 'foto')}** ({p.get('fileSizeBytes', 0) // 1024} KB)")
        lines.append(f"   URL: {p.get('storedUrl', '')}")
        lines.append("")

    return "\n".join(lines)


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

    return "\n".join(lines)


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

    return "\n".join(lines)


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

    return "\n".join(lines)


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
