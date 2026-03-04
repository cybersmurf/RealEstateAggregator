#!/usr/bin/env python3
"""
Test: dokáže qwen2.5:14b generovat kvalitní finální analýzu nemovitosti?
Vezme reálný inzerát z DB, naplní šablonu, pošle modelu a zobrazí výsledek.
"""

import asyncio
import asyncpg
import httpx
import sys
from pathlib import Path
from datetime import datetime

OLLAMA_URL = "http://localhost:11434"
MODEL = "qwen2.5:14b"
DB_DSN = "postgresql://postgres:dev@localhost:5432/realestate_dev"
TEMPLATE_PATH = Path(__file__).parent.parent / "src/RealEstate.Api/Templates/ai_instrukce_existing.md"


async def get_listing(listing_id: str | None = None) -> dict:
    conn = await asyncpg.connect(DB_DSN)
    try:
        if listing_id:
            row = await conn.fetchrow("""
                SELECT l.id, l.title, l.price, l.price_note, l.location_text,
                       l.description, l.area_built_up, l.area_land,
                       l.property_type, l.offer_type, l.construction_type,
                       l.condition, l.municipality, l.region, l.url,
                       s.name as source_name, s.code as source_code
                FROM re_realestate.listings l
                JOIN re_realestate.sources s ON l.source_id = s.id
                WHERE l.id = $1
            """, listing_id)
        else:
            # vezme dům nebo byt s delším popisem
            row = await conn.fetchrow("""
                SELECT l.id, l.title, l.price, l.price_note, l.location_text,
                       l.description, l.area_built_up, l.area_land,
                       l.property_type, l.offer_type, l.construction_type,
                       l.condition, l.municipality, l.region, l.url,
                       s.name as source_name, s.code as source_code
                FROM re_realestate.listings l
                JOIN re_realestate.sources s ON l.source_id = s.id
                WHERE l.is_active = true
                  AND l.price IS NOT NULL
                  AND l.description IS NOT NULL
                  AND length(l.description) > 800
                  AND l.property_type IN ('House', 'Apartment')
                ORDER BY length(l.description) DESC
                LIMIT 1
            """)
        return dict(row) if row else {}
    finally:
        await conn.close()


def fill_template(template: str, listing: dict) -> str:
    price_str = f"{listing['price']:,.0f} Kč".replace(",", " ") if listing.get("price") else "neuvedena"
    area_str = f"{listing.get('area_built_up', 0):.0f} m²" if listing.get("area_built_up") else "neuvedena"

    replacements = {
        "{{LOCATION}}": listing.get("location_text") or listing.get("municipality") or "nezjištěno",
        "{{PROPERTY_TYPE}}": listing.get("property_type") or "nezjištěno",
        "{{OFFER_TYPE}}": listing.get("offer_type") or "nezjištěno",
        "{{PRICE}}": price_str,
        "{{PRICE_NOTE}}": f" ({listing['price_note']})" if listing.get("price_note") else "",
        "{{AREA}}": area_str,
        "{{ROOMS_LINE}}": "",
        "{{CONSTRUCTION_TYPE_LINE}}": f"**Typ stavby:** {listing['construction_type']}\n" if listing.get("construction_type") else "",
        "{{CONDITION_LINE}}": f"**Stav:** {listing['condition']}\n" if listing.get("condition") else "",
        "{{SOURCE_NAME}}": listing.get("source_name") or "nezjištěno",
        "{{SOURCE_CODE}}": listing.get("source_code") or "?",
        "{{URL}}": listing.get("url") or "#",
        "{{PHOTO_LINKS_SECTION}}": "",
        "{{DRIVE_FOLDER_SECTION}}": "",
    }
    for placeholder, value in replacements.items():
        template = template.replace(placeholder, value)
    return template


async def analyze_with_ollama(prompt: str, description: str) -> str:
    """Pošle prompt + popis modelu a vrátí odpověď."""
    system_prompt = prompt
    user_message = f"""## POPIS NEMOVITOSTI Z INZERÁTU

{description}

---

Nyní proveď kompletní analýzu dle instrukcí výše. Piš v češtině, strukturovaně."""

    async with httpx.AsyncClient(timeout=300) as client:
        resp = await client.post(
            f"{OLLAMA_URL}/api/chat",
            json={
                "model": MODEL,
                "messages": [
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": user_message},
                ],
                "stream": False,
                "options": {
                    "temperature": 0.3,
                    "num_predict": 3000,
                },
            },
        )
        resp.raise_for_status()
        data = resp.json()
        return data["message"]["content"]


async def main():
    listing_id = sys.argv[1] if len(sys.argv) > 1 else None

    print(f"=== Test lokální analýzy: {MODEL} ===\n")

    # 1. Načti inzerát
    print("📦 Načítám inzerát z DB...")
    listing = await get_listing(listing_id)
    if not listing:
        print("❌ Inzerát nenalezen")
        return

    print(f"  ✓ {listing['title']}")
    print(f"  📍 {listing['location_text']}")
    print(f"  💰 {listing['price']:,.0f} Kč" if listing.get("price") else "  💰 cena neuvedena")
    print(f"  📄 Délka popisu: {len(listing.get('description', ''))} znaků\n")

    # 2. Načti a naplň šablonu
    print("📝 Připravuji prompt ze šablony...")
    template = TEMPLATE_PATH.read_text(encoding="utf-8")
    filled_prompt = fill_template(template, listing)
    print(f"  ✓ Prompt připraven ({len(filled_prompt)} znaků)\n")

    # 3. Spusť analýzu
    print(f"🤖 Generuji analýzu pomocí {MODEL}...")
    start = datetime.now()
    analysis = await analyze_with_ollama(filled_prompt, listing.get("description", ""))
    elapsed = (datetime.now() - start).total_seconds()

    tokens_approx = len(analysis.split())
    print(f"  ✓ Hotovo za {elapsed:.1f}s (~{tokens_approx} slov)\n")

    # 4. Výstup
    separator = "=" * 70
    print(separator)
    print("VÝSLEDEK ANALÝZY")
    print(separator)
    print(analysis)
    print(separator)

    # 5. Uložit do souboru
    output_file = Path(f"/tmp/analysis_{listing['id'][:8]}.md")
    output_file.write_text(f"# Analýza: {listing['title']}\n\n{analysis}", encoding="utf-8")
    print(f"\n✅ Uloženo do: {output_file}")
    print(f"\n💡 Kvalita OK? Pak mohu:")
    print("   1. Přidat endpoint POST /api/listings/{id}/analyze-local")
    print("   2. Přidat DOCX export přes pandoc (brew install pandoc)")


if __name__ == "__main__":
    asyncio.run(main())
