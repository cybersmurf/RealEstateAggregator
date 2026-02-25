# RAG + MCP Design – RealEstateAggregator

**Verze:** 1.0  
**Datum:** 25. února 2026 (Session 6)  
**Autoři:** AI-assisted design (Copilot + qwen2.5:14b), Petr Šrámek

---

## Obsah

1. [Přehled architektury](#přehled-architektury)
2. [Rozhodovací log](#rozhodovací-log)
3. [Databázová schéma](#databázová-schéma)
4. [Tok dat – Save Analysis](#tok-dat--save-analysis)
5. [Tok dat – RAG Query](#tok-dat--rag-query)
6. [API endpointy](#api-endpointy)
7. [MCP Server](#mcp-server)
8. [Embedding providers](#embedding-providers)
9. [Konfigurační reference](#konfigurační-reference)
10. [Deployment](#deployment)
11. [Testování](#testování)

---

## Přehled architektury

```
┌──────────────────────────────────────────────────────────────────┐
│                        AI Clients                                │
│   Claude Desktop (stdio)      HTTP (SSE :8002)                   │
│         │                           │                            │
│         └──────────┬────────────────┘                            │
└──────────────────────────────────────────────────────────────────┘
                     │
           ┌─────────▼─────────┐
           │   MCP Server      │  mcp/server.py
           │   FastMCP 3.x     │  7 nástrojů (tools)
           │   :8002 (Docker)  │  stdio (Claude Desktop)
           └─────────┬─────────┘
                     │ HTTP (httpx)
           ┌─────────▼─────────┐
           │  .NET API         │  RealEstate.Api :5001
           │  Minimal APIs     │  /api/rag/* endpointy
           │                   │  /api/listings/{id}/analyses
           └──────┬──────┬─────┘
                  │      │
     ┌────────────▼──┐ ┌─▼──────────────────┐
     │ PostgreSQL 15 │ │ Ollama :11434       │
     │ pgvector ext. │ │ (host machine / M2) │
     │ vector(768)   │ │                     │
     │ listing_      │ │ nomic-embed-text    │ ← embeddings (274 MB)
     │ analyses      │ │ qwen2.5:14b         │ ← chat (9 GB)
     └───────────────┘ └────────────────────┘
```

### Komponenty

| Komponenta | Technologie | Účel |
|---|---|---|
| **RagService** | C# (.NET 10) | Orchestrace: ukládání, vyhledávání, chat |
| **OllamaEmbeddingService** | C# + HttpClient | Volání Ollama API (embeddings + chat) |
| **OpenAIEmbeddingService** | C# + OpenAI NuGet | Fallback provider (API key) |
| **listing_analyses** | PostgreSQL + pgvector | Ukládání textů + vektorů |
| **RagEndpoints** | Minimal API | HTTP rozhraní pro Blazor/curl |
| **MCP Server** | Python + FastMCP 3.x | Integrace Claude Desktop / AI assistentů |

---

## Rozhodovací log

### Proč Ollama místo OpenAI?
| Kritérium | Ollama (zvoleno) | OpenAI API |
|---|---|---|
| Cena | Zdarma (lokální) | ~$0.02/1M tokenů (embedding) |
| Soukromí | 100 % lokální, žádná data ven | Data jdou na OpenAI servery |
| Kvalita embeddings | nomic-embed-text (MTEB score 62) | text-embedding-3-small (MTEB 62.3) |
| Offline fungování | ✅ Ano | ❌ Vyžaduje internet |
| HW nároky | M2 Ultra (72 GB RAM) – ideální | Žádné nároky |
| Latence | ~200 ms (lokální NVMe) | ~300–800 ms (sítě) |
| Závislost | Žádná | API key, účet, billing |

**Rozhodnutí:** Ollama jako primární provider. OpenAI jako fallback přes `Embedding__Provider=openai`.

### Proč pgvector místo Qdrant/Weaviate?
- PostgreSQL **již** v projektu → nulová infrastrukturní cena
- pgvector 0.7+ podporuje `IVFFlat` a `HNSW` indexy (dostatečné pro < 100k vektorů)
- Transakce – analýzy a seznam v jedné DB transakci
- Jednodušší backup (jeden pg_dump pokryje vše)

### Proč FastMCP 3.x místo přímého MCP SDK?
- FastMCP 3.x poskytuje `@mcp.tool()` dekorátor → čistý Python kód
- Podporuje obě transporty: **stdio** (Claude Desktop) a **SSE** (HTTP, Docker)
- Automaticky generuje JSON schema z type hints
- Aktivní vývoj, kompatibilní s MCP spec 2024-11-05

### Proč vector(768) a ne vector(1536)?
- `nomic-embed-text` produkuje 768-dimenzionální vektory
- `text-embedding-3-small` (OpenAI) produkuje 1 536-dim
- **768 dim je dostatečné** pro sémantické vyhledávání v realitním kontextu
- Menší vektory = ~2× rychlejší indexování a dotazy
- Při přechodu na OpenAI je nutná nová migrace dimenze

---

## Databázová schéma

### Tabulka `re_realestate.listing_analyses`

```sql
CREATE TABLE IF NOT EXISTS re_realestate.listing_analyses (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id  uuid        NOT NULL REFERENCES re_realestate.listings(id) ON DELETE CASCADE,
    content     text        NOT NULL,          -- Text analýzy / zápisku
    embedding   vector(768),                   -- nomic-embed-text dimenze (NULL = neembedováno)
    source      text        NOT NULL DEFAULT 'manual',  -- 'manual'|'claude'|'mcp'|'ai'
    title       text,                          -- Volitelný název zápisku
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);

-- Indexy
CREATE INDEX IF NOT EXISTS idx_listing_analyses_listing_id
    ON re_realestate.listing_analyses(listing_id);

CREATE INDEX IF NOT EXISTS idx_listing_analyses_embedding
    ON re_realestate.listing_analyses USING ivfflat (embedding vector_cosine_ops)
    WITH (lists = 100);
```

### Sloupce detailně

| Sloupec | Typ | Nullable | Popis |
|---|---|---|---|
| `id` | uuid | NOT NULL | PK, generovaný automaticky |
| `listing_id` | uuid | NOT NULL | FK → `listings.id`, CASCADE DELETE |
| `content` | text | NOT NULL | Plný text analýzy/poznámky |
| `embedding` | vector(768) | NULL | Vektor ze `nomic-embed-text`; NULL dokud neproběhne embedding |
| `source` | text | NOT NULL | Zdroj: `manual`, `claude`, `mcp`, `ai` |
| `title` | text | NULL | Volitelný nadpis zápisku |
| `created_at` | timestamptz | NOT NULL | Čas vytvoření |
| `updated_at` | timestamptz | NOT NULL | Čas poslední aktualizace |

### Migrace dimenze (768 ↔ 1536)

```sql
-- Bezpečná migrace dimenze (výsledek: smaže a vytvoří nový sloupec)
DO $$
BEGIN
  IF EXISTS (
    SELECT FROM information_schema.columns
    WHERE table_schema = 're_realestate'
      AND table_name = 'listing_analyses'
      AND column_name = 'embedding'
  ) THEN
    ALTER TABLE re_realestate.listing_analyses DROP COLUMN embedding;
    ALTER TABLE re_realestate.listing_analyses ADD COLUMN embedding vector(768);
  END IF;
END $$;
```

*Tato migrace probíhá automaticky při startu API přes `DbInitializer`.*

---

## Tok dat – Save Analysis

```
POST /api/listings/{id}/analyses
  │
  ├─► Ověření: listing {id} existuje
  │
  ├─► INSERT listing_analyses (content, title, source)
  │     → embedding = NULL (zatím)
  │
  ├─► Ollama POST /api/embed
  │     model: "nomic-embed-text"
  │     input: "Titulek inzerátu\n\nObsah analýzy..."
  │     → float[768]
  │
  ├─► UPDATE listing_analyses SET embedding = $1 WHERE id = $2
  │
  └─► Response: ListingAnalysisDto { hasEmbedding: true }
```

### Příklad request/response

**Request:**
```json
POST /api/listings/3fa85f64-5717-4562-b3fc-2c963f66afa6/analyses
{
  "content": "Lokalita je výborná - 5 min od vlakové stanice Pohořelice. Cena odpovídá trhu, nutná rekonstrukce kuchyně a koupelny.",
  "title": "Moje poznámka – 24.2.2026",
  "source": "manual"
}
```

**Response 201:**
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "listingId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "content": "Lokalita je výborná - 5 min od vlakové stanice Pohořelice...",
  "title": "Moje poznámka – 24.2.2026",
  "source": "manual",
  "hasEmbedding": true,
  "createdAt": "2026-02-25T14:30:00Z",
  "updatedAt": "2026-02-25T14:30:19Z"
}
```

---

## Tok dat – RAG Query

```
POST /api/listings/{id}/ask
  { "question": "Je tato nemovitost vhodná pro rodinu s dětmi?", "topK": 5 }
  │
  ├─► Ollama POST /api/embed
  │     model: "nomic-embed-text"
  │     input: "Je tato nemovitost vhodná pro rodinu s dětmi?"
  │     → queryVector float[768]
  │
  ├─► SQL:
  │     SELECT la.*, la.embedding <-> {queryVector} AS distance
  │     FROM re_realestate.listing_analyses la
  │     WHERE la.listing_id = {id}
  │       AND la.embedding IS NOT NULL
  │     ORDER BY la.embedding <-> {queryVector}
  │     LIMIT {topK}
  │
  ├─► Sestavení kontextu:
  │     Analýza 1: "Lokalita je výborná..."
  │     Analýza 2: "Cena odpovídá nabídce v okolí..."
  │
  ├─► Ollama POST /api/chat
  │     model: "qwen2.5:14b"
  │     system: "Jsi asistent pro hodnocení nemovitostí. Odpovídej česky..."
  │     messages: [{ role: user, content: "Kontext:\n{ctx}\n\nOtázka: {q}" }]
  │     stream: false
  │     → odpověď v češtině
  │
  └─► Response: AskResponseDto
        { answer: "...", sources: ["550e8400...", ...], hasEmbeddings: true }
```

### Příklad cross-listing query

```
POST /api/rag/ask
  { "question": "Které nemovitosti jsou vhodné pro investici do pronájmu?", "topK": 5 }
  │
  └─► Stejný tok, ale bez WHERE la.listing_id = {id}
        → hledá napříč VŠEMI analyzovanými inzeráty
```

---

## API endpointy

### Přehled RAG endpointů

| Metoda | Cesta | Popis |
|---|---|---|
| `GET` | `/api/listings/{id}/analyses` | Seznam analýz inzerátu |
| `POST` | `/api/listings/{id}/analyses` | Uložit analýzu + embedding |
| `DELETE` | `/api/listings/{id}/analyses/{analysisId}` | Smazat analýzu |
| `POST` | `/api/listings/{id}/ask` | RAG otázka pro jeden inzerát |
| `POST` | `/api/rag/ask` | RAG otázka napříč všemi inzeráty |
| `GET` | `/api/rag/status` | Health + počty (embedded/total) |

### GET /api/listings/{id}/analyses

**Response 200:**
```json
[
  {
    "id": "550e8400-e29b-41d4-a716-446655440000",
    "listingId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "content": "Lokalita je výborná - 5 min od vlakové stanice...",
    "title": "Moje poznámka – 24.2.2026",
    "source": "manual",
    "hasEmbedding": true,
    "createdAt": "2026-02-25T14:30:00Z",
    "updatedAt": "2026-02-25T14:30:19Z"
  }
]
```

### POST /api/listings/{id}/analyses

**Request:**
```json
{
  "content": "string (povinné)",
  "title": "string (volitelné)",
  "source": "manual | claude | mcp | ai"
}
```

**Response 201:** `ListingAnalysisDto` (viz výše)  
**Response 404:** Inzerát nenalezen

### DELETE /api/listings/{id}/analyses/{analysisId}

**Response 204:** Smazáno  
**Response 404:** Analýza / inzerát nenalezena

### POST /api/listings/{id}/ask

**Request:**
```json
{
  "question": "Otázka v přirozeném jazyce",
  "topK": 5
}
```

**Response 200:**
```json
{
  "answer": "Na základě uložených analýz...",
  "sources": ["550e8400-...", "661f9500-..."],
  "hasEmbeddings": true
}
```

**Response 200 (bez analýz):**
```json
{
  "answer": "Pro tento inzerát zatím nejsou uloženy žádné analýzy.",
  "sources": [],
  "hasEmbeddings": false
}
```

### POST /api/rag/ask

**Request:** Stejný jako `/ask` výše (bez filtrování na listing)

### GET /api/rag/status

**Response 200:**
```json
{
  "provider": "ollama",
  "isConfigured": true,
  "ollamaBaseUrl": "http://localhost:11434",
  "embeddingModel": "nomic-embed-text",
  "chatModel": "qwen2.5:14b",
  "totalAnalyses": 12,
  "embeddedAnalyses": 10,
  "vectorDimensions": 768
}
```

---

## MCP Server

### Přehled

MCP (Model Context Protocol) server umožňuje AI asistentům (Claude Desktop, Cursor, ...) přímo přistupovat k datům Real Estate Aggregatoru bez copy-paste.

**Soubor:** `mcp/server.py`  
**Framework:** FastMCP 3.x  
**Transport:** stdio (Claude Desktop) nebo SSE/HTTP (Docker :8002)

### Dostupné nástroje (tools)

| Tool | Popis | Vstupy |
|---|---|---|
| `search_listings` | Hledání inzerátů (fulltextové + filtry) | query, property_type, offer_type, price_min, price_max, page |
| `get_listing` | Detail konkrétního inzerátu | listing_id |
| `get_analyses` | Analýzy inzerátu | listing_id |
| `save_analysis` | Uložit analýzu a vytvořit embedding | listing_id, content, title, source |
| `ask_listing` | RAG otázka pro jeden inzerát | listing_id, question, top_k |
| `ask_general` | RAG otázka napříč všemi inzeráty | question, top_k |
| `list_sources` | Seznam aktivních zdrojů | — |
| `get_rag_status` | Stav RAG systému | — |

### Konfigurační soubory

#### Claude Desktop (stdio transport)

`~/Library/Application Support/Claude/claude_desktop_config.json`:
```json
{
  "mcpServers": {
    "realestate": {
      "command": "python",
      "args": ["/Users/petrsramek/Projects/RealEstateAggregator/mcp/server.py"],
      "env": {
        "API_BASE_URL": "http://localhost:5001"
      }
    }
  }
}
```

#### Docker (SSE transport)

V `docker-compose.yml`:
```yaml
mcp:
  build:
    context: ./mcp
    dockerfile: Dockerfile
  ports:
    - "8002:8002"
  environment:
    - API_BASE_URL=http://api:5001
    - MCP_TRANSPORT=sse
  depends_on:
    api:
      condition: service_healthy
  restart: unless-stopped
```

### Příklady použití (Claude Desktop)

```
Uživatel: "Co víš o nemovitosti s ID 3fa85f64?"
Claude → get_listing(listing_id="3fa85f64-...") → detail
Claude: "Jedná se o byt 3+1 v Pohořelicích za 3,2 mil. Kč..."

Uživatel: "Ulož poznámku: cena je vyjednatelná"
Claude → save_analysis(listing_id="3fa85f64-...", content="Cena je vyjednatelná", source="claude")
Claude: "Analýza uložena a embedded."

Uživatel: "Které byty v Brně jsou pod 4 miliony?"
Claude → search_listings(query="Brno byt", offer_type="Sale", price_max=4000000)
Claude: "Nalezeno 12 bytů v Brně do 4 mil. Kč..."
```

---

## Embedding providers

### OllamaEmbeddingService (primární)

```
POST http://localhost:11434/api/embed
{
  "model": "nomic-embed-text",
  "input": "text k embeddingu"
}
→ { "embeddings": [[0.12, -0.03, ...]] }  // float[768]

POST http://localhost:11434/api/chat
{
  "model": "qwen2.5:14b",
  "messages": [
    { "role": "system", "content": "Jsi asistent..." },
    { "role": "user", "content": "..." }
  ],
  "stream": false
}
→ { "message": { "content": "Odpověď v češtině..." } }
```

### OpenAIEmbeddingService (fallback)

```
POST https://api.openai.com/v1/embeddings
Authorization: Bearer {ApiKey}
{
  "model": "text-embedding-3-small",
  "input": "text k embeddingu"
}
→ { "data": [{ "embedding": [0.12, -0.03, ...] }] }  // float[1536]

POST https://api.openai.com/v1/chat/completions
{
  "model": "gpt-4o-mini",
  "messages": [...]
}
→ { "choices": [{ "message": { "content": "..." } }] }
```

### Porovnání providerů

| Vlastnost | Ollama (nomic-embed-text) | OpenAI (text-embedding-3-small) |
|---|---|---|
| Dimenze | 768 | 1536 |
| Velikost modelu | 274 MB | N/A (cloud) |
| MTEB score | ~62 | ~62.3 |
| Latence (lokální M2) | ~150–300 ms | ~300–800 ms |
| Cena | Zdarma | $0.02/1M tokenů |
| Offline | ✅ | ❌ |
| Přepínač | `Embedding__Provider=ollama` | `Embedding__Provider=openai` |

**Poznámka:** Při přechodu mezi providery je nutné **smazat všechny existující embeddingy** (různé dimenze nejsou kompatibilní):
```sql
UPDATE re_realestate.listing_analyses SET embedding = NULL;
```

---

## Konfigurační reference

### appsettings.json

```json
{
  "OpenAI": {
    "ApiKey": "",
    "EmbeddingModel": "text-embedding-3-small",
    "ChatModel": "gpt-4o-mini"
  },
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text",
    "ChatModel": "qwen2.5:14b"
  },
  "Embedding": {
    "Provider": "ollama",
    "VectorDimensions": "768"
  }
}
```

### Environment variables (Docker)

| Proměnná | Hodnota (Docker) | Popis |
|---|---|---|
| `Embedding__Provider` | `ollama` | Výběr provideru |
| `Embedding__VectorDimensions` | `768` | Dimenze vektoru |
| `Ollama__BaseUrl` | `http://host.docker.internal:11434` | Ollama v Docker |
| `Ollama__EmbeddingModel` | `nomic-embed-text` | Model pro embeddingy |
| `Ollama__ChatModel` | `qwen2.5:14b` | Model pro chat |
| `OpenAI__ApiKey` | `sk-...` | OpenAI klíč (volitelné) |
| `API_BASE_URL` | `http://api:5001` | Pro MCP server v Docker |
| `MCP_TRANSPORT` | `sse` | Transport pro MCP v Docker |

### Provider selection logic (ServiceCollectionExtensions.cs)

```csharp
var provider = config["Embedding:Provider"] ?? "ollama";
var ollamaUrl = config["Ollama:BaseUrl"];
var openAiKey = config["OpenAI:ApiKey"];

if (provider == "ollama" || (ollamaUrl != null && string.IsNullOrEmpty(openAiKey)))
    services.AddSingleton<IEmbeddingService, OllamaEmbeddingService>();
else
    services.AddSingleton<IEmbeddingService, OpenAIEmbeddingService>();
```

---

## Deployment

### 1. Lokální vývoj

```bash
# 1. Spustit Ollama (jednou)
ollama pull nomic-embed-text
ollama pull qwen2.5:14b
ollama serve  # běží na :11434

# 2. Spustit API
dotnet run --project src/RealEstate.Api --urls "http://localhost:5001"

# 3. Otestovat embedding
curl -X POST http://localhost:5001/api/rag/status

# 4. Uložit první analýzu
curl -X POST http://localhost:5001/api/listings/{id}/analyses \
  -H "Content-Type: application/json" \
  -d '{"content":"Test analýzy","source":"manual"}'

# 5. Spustit MCP server (stdio pro Claude Desktop)
cd mcp && pip install -r requirements.txt
API_BASE_URL=http://localhost:5001 python server.py
```

### 2. Docker deployment

```bash
# Ollama musí běžet na host mašině (ne v kontejneru)
# M2 Mac: ollama serve  (agilně využívá MPS/Metal GPU)

# Build + Deploy (po změnách v C# kódu)
docker compose build --no-cache api mcp
docker compose up -d --no-deps api mcp

# Ověření
curl http://localhost:5001/api/rag/status
docker logs realestate-mcp
```

### 3. Claude Desktop integrace

1. Otevřít `~/Library/Application Support/Claude/claude_desktop_config.json`
2. Přidat MCP server config (viz sekce MCP Server výše)
3. Restartovat Claude Desktop
4. Ověřit: Claude by měl zobrazit "realestate" v seznamu dostupných nástrojů

---

## Testování

### curl – kompletní testovací sekvence

```bash
BASE="http://localhost:5001"
LISTING_ID="<skutečné-uuid-z-db>"

# 1. Status check
curl $BASE/api/rag/status | jq

# 2. Uložit analýzu
ANALYSIS=$(curl -s -X POST $BASE/api/listings/$LISTING_ID/analyses \
  -H "Content-Type: application/json" \
  -d '{
    "content": "Dům je v klidné části, blízkost lesa, starší okna ke výměně.",
    "title": "Osobní prohlídka 25.2.2026",
    "source": "manual"
  }')
echo $ANALYSIS | jq
ANALYSIS_ID=$(echo $ANALYSIS | jq -r '.id')

# 3. Načíst analýzy
curl $BASE/api/listings/$LISTING_ID/analyses | jq

# 4. RAG dotaz (jeden inzerát)
curl -X POST $BASE/api/listings/$LISTING_ID/ask \
  -H "Content-Type: application/json" \
  -d '{
    "question": "Jaké jsou nevýhody této nemovitosti?",
    "topK": 5
  }' | jq

# 5. RAG dotaz (všechny inzeráty)
curl -X POST $BASE/api/rag/ask \
  -H "Content-Type: application/json" \
  -d '{
    "question": "Které nemovitosti jsou vhodné pro rodinu?",
    "topK": 3
  }' | jq

# 6. Smazat analýzu
curl -X DELETE $BASE/api/listings/$LISTING_ID/analyses/$ANALYSIS_ID
```

### Ověření v PostgreSQL

```sql
-- Počty analýz a embedded
SELECT
  COUNT(*) AS total,
  COUNT(embedding) AS embedded,
  COUNT(*) - COUNT(embedding) AS missing_embedding
FROM re_realestate.listing_analyses;

-- Vyhledávání nejpodobnějších (approx. cosine distance)
SELECT la.title, la.source, la.embedding <-> '[0.1, 0.2, ...]'::vector AS dist
FROM re_realestate.listing_analyses la
WHERE la.embedding IS NOT NULL
ORDER BY dist
LIMIT 5;
```

---

## Ingestor pattern

Každý zdroj dat (popis inzerátu, PDF smlouva, e-mail, Drive dokument) se stává **jedním záznamem v `listing_analyses`**. RAG logika je vždy stejná – liší se pouze `source`.

### Existující ingestory

| Source | Spuštění | Popis |
|---|---|---|
| `manual` | UI nebo Claude Desktop | Ruční poznámka uživatele |
| `claude` | MCP `save_analysis` tool | Závěr AI agenta |
| `mcp` | MCP `save_analysis` tool | Import přes MCP |
| `auto` | `POST /api/listings/{id}/embed-description` | Popis inzerátu – automaticky  |

### Bulk embed

```bash
# Embed všech aktivních inzerátů bez "auto" analýzy (max 100 najednou)
curl -X POST http://localhost:5001/api/rag/embed-descriptions \
  -H "Content-Type: application/json" \
  -d '{ "limit": 200 }'

# Response: { "processed": 148, "message": "Zpracováno 148 inzerátů" }
```

### Vlastní ingestor (Drive / PDF / e-mail)

Každý ingestor je jen tenký wrapper nad `POST /api/listings/{id}/analyses`:

```python
# Příklad: Python ingestor pro PDF z Google Drive
async def ingest_pdf(listing_id: str, pdf_text: str, source_label: str = "import"):
    async with httpx.AsyncClient() as http:
        await http.post(
            f"http://localhost:5001/api/listings/{listing_id}/analyses",
            json={
                "content": pdf_text,
                "title": f"Import – {source_label}",
                "source": "import"
            }
        )
```

Výhoda: RAG logika se nemění, jen přibývají záznamy v `listing_analyses`.

---

## Budoucí vylepšení

| Priorita | Funkce | Popis | Stav |
|---|---|---|---|
| High | **Batch embedding** | Při importu scrapeovaných dat automaticky embedovat description | ✅ Hotovo – `POST /api/rag/embed-descriptions` + `POST /api/listings/{id}/embed-description` |
| High | **UI – RAG chat** | Blazor komponenta pro chat s inzerátem | ✅ Hotovo – RAG chat sekce v `ListingDetail.razor` |
| Medium | **Přepínač v UI** | Ollama ↔ OpenAI bez restartu | 🔲 Pending |
| Medium | **HNSW index** | Rychlejší přibližné vyhledávání pro > 10k vektorů | 🔲 Pending |
| Medium | **Hybrid search** | Kombinace BM25 (tsvector) + cosine similarity | 🔲 Pending |
| Medium | **Ingestor pattern** | Drive / PDF / e-mail jako záznamy v `listing_analyses` | ✅ Zdokumentováno |
| Low | **Multi-modal** | Embeddingy z fotek (clip/llava) | 🔲 Pending |
| Low | **Agent mode** | MCP server spouští scraping za uživatele | 🔲 Pending |

---

**Konec RAG/MCP Design dokumentu** • Verze 1.0 • 25. února 2026
