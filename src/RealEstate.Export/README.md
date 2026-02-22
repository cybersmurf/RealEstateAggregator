# 📦 RealEstate.Export

CLI nástroj pro strukturovaný export inzerátů realitních nemovitostí do formátu Markdown/JSON/HTML. Optimalizované pro AI analýzu bez vlastního LLM API.

## 🎯 Cíl

Generuj **strukturované balíčky inzerátů** (MD/JSON), pak je zpracuj Copilot/Perplexity/Gemini, které už platíš.

**Bez potřeby vlastního LLM API!**

## 🚀 Quick Start

### Instalace

```bash
cd src/RealEstate.Export
dotnet build
```

### Spuštění

```bash
# Export jednoho inzerátu do Markdown
dotnet run -- export-listing --id 550e8400-e29b-41d4-a716-446655440000 --format markdown --output ./exports/

# Export více inzerátů s filtrem
dotnet run -- export-batch --region "Jihomoravský kraj" --price-max 5000000 --format markdown --output ./exports/
```

## 📋 Commands

### `export-listing`

Exportuj jeden inzerát.

```bash
dotnet run -- export-listing \
  --id <GUID> \
  --format markdown|json|html \
  --output ./exports/
```

**Výstup**: `listing-<id>.md` (~5 KB)

**Markdown obsahuje**:
- Metadata tabulka (cena, plocha, lokalita, stav…)
- Popis inzerátu
- Fotky (jako reference na URL)
- Timeline (kdy viděno, aktualizováno…)
- Status (aktivní, embeddingy…)

---

### `export-batch`

Exportuj více inzerátů s filtrem.

```bash
dotnet run -- export-batch \
  --region "Jihomoravský kraj" \
  --price-max 5000000 \
  --status "New" \
  --limit 10 \
  --format markdown \
  --output ./exports/
```

**Výstup**: `batch-<timestamp>.md` (~50-200 KB)

**Markdown obsahuje**:
- Obsah (index všech inzerátů)
- Jednotlivé sekce (jeden # h1 per listing)
- Vhodné pro přidání do Perplexity či dokumentace

---

## 🤖 Workflow: Export → AI → Insights

```
1. Export inzerátu → MD soubor
   └─ dotnet run -- export-listing --id <GUID>

2. Open MD v VSCode
   └─ @copilot "Shrnutí + checklist na prohlídku"
   └─ Copilot: "Riskovat? Budget? Otázky na makléře?"

3. Copy-paste do Perplexity
   └─ "Tržní cena? Je dobré místo? Daně?"
   └─ Perplexity: Rešerše + citace

4. Upload do Gemini
   └─ "Analýza fotek + doporučení"
   └─ Gemini: Visual check + priority

5. Compile insights → Rozhodnutí
   └─ Tabulka Pros/Cons/Risk/Budget
   └─ Jdu na prohlídku / Ignore
```

## 📚 Dokumentace

Přečti si **[docs/prompts/COPILOT_PERPLEXITY_PLAYBOOK.md](../prompts/COPILOT_PERPLEXITY_PLAYBOOK.md)** – tam máš:
- Praktické prompty pro Copilot Chat
- Prompty pro Perplexity research
- Prompty pro Gemini visual analysis
- Real-life příklady

## 🔧 Configuration

Connection string je v `appsettings.json` API projektu. Export projekt ho zdědí.

```json
"ConnectionStrings": {
  "RealEstate": "Host=localhost;Port=5432;Database=realestate_dev;Username=postgres;Password=dev"
}
```

## 📦 Output Formats

### Markdown
- Tabulky, heading hierarchie, links
- **Best for**: AI processing, copy-paste to Perplexity/Gemini, dokumentace
- Velikost: ~5 KB per listing

### JSON
- Strukturovaný, parsable
- **Best for**: Webhooky, integrace, programmatické zpracování
- Velikost: ~3 KB per listing

### HTML
- Zdarma preview v prohlížeči
- **Best for**: Tisk, sharing via email
- Velikost: ~8 KB per listing

## 🎁 Use Cases

### 1. Analýza jednoho domu

```bash
# Export
dotnet run -- export-listing --id 550e8400... -f markdown -o ~/tmp/

# Copilot Chat: "Checklist na prohlídku + rozpočet"
# → Vygeneruje checklist, otázky na makléře, rozpočet rekonstrukce
```

### 2. Porovnání více domů

```bash
# Export batch
dotnet run -- export-batch --price-max 5000000 --limit 10

# Perplexity: "Porovnej těch 10 domů. Ranking 1-10?"
# → Vygeneruje tabulku s porovnáním
```

### 3. Tržní research

```bash
# Export batch lokalita
dotnet run -- export-batch --region "Jihomoravský kraj" --limit 20

# Perplexity: "Jaké jsou ceny v regionu? Trendy za 2 roky?"
# → Market analysis
```

### 4. Rekonstrukční rozpočet

```bash
# Export
dotnet run -- export-listing --id <GUID>

# Copilot: "Stav: {Condition}. Rozpočet min/střed/premium na rekonstrukci"
# → Odhady nákladů
```

## ⚡ Příklady

### Příklad 1: Exportuj inzerát a analyzuj v Copilotu

```bash
# Terminal
dotnet run -- export-listing --id 550e8400-e29b-41d4-a716-446655440000 \
  --format markdown --output /tmp/

# VSCode
# 1. Open /tmp/listing-550e8400....md
# 2. Ctrl+Shift+P → "GitHub Copilot Chat"
# 3. Type: "@copilot Jaké jsou 3 největší rizika koupu tohoto domu?"
# 4. Copilot: "Detailní analýza s checklistem"
```

### Příklad 2: Batch porovnáním v Perplexity

```bash
# Terminal
dotnet run -- export-batch --region "Jihomoravský kraj" --price-max 5000000 --limit 5

# Browser
# 1. Open https://www.perplexity.ai
# 2. Paste obsah batch-...md
# 3. Type: "Porovnej těch 5 domů. Kterej je best?"
# 4. Perplexity: Tabulka + doporučení
```

## 🔍 Architektura

```
RealEstate.Export/
├── Program.cs                  # CLI commands (export-listing, export-batch)
├── Services/
│   ├── IExportService.cs       # Interface
│   └── MarkdownExporter.cs     # Implementace (MD/JSON/HTML)
└── RealEstate.Export.csproj    # Dependencies: System.CommandLine, EF Core
```

**Key classes**:
- **MarkdownExporter**: Generuje MD/JSON/HTML z `Listing` entit
- **ExportFormat**: Enum (Markdown, Json, Html)

## 📝 Extensibility

Chceš přidat nový formát (YAML, XML)?

1. V `MarkdownExporter` přidej nový `BuildXxx()` metod
2. Do `ExportFormat` enum přidej nový variant
3. Update CLI help text

```csharp
ExportFormat.Yaml => BuildYaml(listing)
```

---

**Pro detailní prompty a use cases čti**: [docs/prompts/COPILOT_PERPLEXITY_PLAYBOOK.md](../prompts/COPILOT_PERPLEXITY_PLAYBOOK.md)
