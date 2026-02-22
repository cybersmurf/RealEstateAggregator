# 📚 Prompt Playbook – Analýza inzerátů bez vlastního LLM API

**Verze**: 1.0  
**Datum**: 22. února 2026  
**Cíl**: Generuj strukturované podklady + použ AI, které už platíš (Copilot/Perplexity/Gemini)

---

## 🎯 Filosofie

- **Nepotřebuješ** vlastní LLM API (OpenAI, Claude, atd.).
- **Použ** GitHub Copilot Pro (chat v VSCode), Perplexity (web), Gemini (app).
- **Vše je** v `RealEstate.Export` – balíčkuje data do MD/JSON, pak je zpracuješ.

---

## 🛠️ Setup (jednorazově)

### 1. Export projekt ready

```bash
cd src/RealEstate.Api
dotnet ef database update --project ../RealEstate.Infrastructure

# Projekt RealEstate.Export je součástí solution
cd ../RealEstate.Export
dotnet build
```

### 2. Postgres runuje (Docker nebo lokální)

```bash
docker-compose up -d postgres
# NEBO
psql -U postgres -c "SELECT version();"
```

---

## 📦 Export workflow

### A) Jeden inzerát

```bash
cd src/RealEstate.Export

# Export do Markdown (pro AI)
dotnet run -- export-listing --id <GUID> --format markdown --output ./exports/

# Příklad:
dotnet run -- export-listing --id 550e8400-e29b-41d4-a716-446655440000 -f markdown -o /tmp/
```

**Co se vygeneruje**:
- `/tmp/listing-550e8400....md` (3–5 KB)
- Metadata tabulka, Popis, Fotky (URL), Timeline

### B) Batch (s filtrem)

```bash
# Všechny inzeráty z Znojma (max 20, max cena 5M)
dotnet run -- export-batch \
  --region "Jihomoravský kraj" \
  --price-max 5000000 \
  --limit 20 \
  --format markdown \
  --output /tmp/exports/
```

**Co se vygeneruje**:
- `/tmp/exports/batch-20260222-153045.md` (50–200 KB)
- Obsah + jednotlivé sekce (one h1 per listing)

### C) JSON (pro programatické zpracování)

```bash
dotnet run -- export-listing --id <GUID> -f json
# Vygeneruje strukturovaný JSON (vhodný pro webhooky, next steps)
```

---

## 🤖 Prompts pro Copilot Chat (VSCode)

**Aktivace**: Otevři VSCode s `exports/listing-XXX.md`, napiš `@copilot` v chat.

### Prompt 1: Shrnutí + Checklist na prohlídku

```
@copilot Mám tady export inzerátu. Udělej mi:
1. Tabulka nejdůležitějších parametrů (cena, plocha, lokalita, stav)
2. Top 5 rizik, kterých si mám všimnout na prohlídce
3. Checklist otázek na makléře (otop, voda, elektrika, stavební povolení, hypotéka...)
4. Hrubý odhad rozpočtu na rekonstrukci (pokud je stav: "ToReconstruct" či "Demolished")
```

**Copilot vrátí**: Strukturovaný markdown se sekcemi, tabulkami, checklistem.

---

### Prompt 2: Cena vs. lokalita + Doporučení

```
@copilot Jako realitní expert: je tato cena férová pro {lokalita}?
Porovnání:
- Průměrná cena za m² v regionu: (dej tip)
- {Cena}/[Plocha] = {Kč/m²}

Doporučení: koupit / vyjednávat / ignorovat?
```

**Copilot vrátí**: Expert opinion s odkazem na „tržní normy" (které třeba zná ze své trénovací sady).

---

### Prompt 3: Rekonstrukce – rozpočet

```
@copilot Stav domu je "{Condition}". Fotky: {počet}.

Udělej:
1. Lineární rozpočet: co se dá opravit sám (DIY), co NE
2. Prioritizace: fáze rekonstrukce (Rok 1, Rok 2, …)
3. Varianty:
   - Minimální (jen bezpečnost): X,XXX Kč
   - Střední (obývatelné): X,XXX Kč
   - Premium (jako nový): X,XXX Kč
```

**Copilot vrátí**: Checklist s rozpočty (odhady z veřejných DB nářadí, zprávami, atd.).

---

### Prompt 4: Dostupnost a transport

```
@copilot Vygooglujem si info o {lokalita}.

Potřebuji:
- Jak daleko od zastávky autobusu / vlaku?
- Jak daleko od školy, obchodů, lékaře?
- Jak daleko do kanceláře (GPS, kolik minut autem/MHD)?
- Je lokalita bezpečná? (obecně, ne konkrétní dům – to se ví z článků)
```

**Copilot vrátí**: Analýza dostupnosti + doporučení (zda se hodí tvému stylu života).

---

## 🌐 Prompts pro Perplexity (webový chat)

**Postup**:
1. Otevři https://www.perplexity.ai
2. Nahraji export jako text / či dám public URL (Share → Get Link)
3. Napíšu prompt

### Prompt 1: Tržní analýza

```
Mám tady export inzerátu (viz přiložený soubor). Lokalita: {lokalita}, cena: {cena}.

Proveď tržní výzkum:
1. Jaká je průměrná cena nemovitostí v {lokalita}?
2. Jak se vyvíjí ceny za poslední 2 roky?
3. Je toto místo „восходящее" nebo stagnuje?
4. Jaké jsou daně / pojištění v tomto kraji?
```

**Perplexity vrátí**: (s citacemi ze zdrojů)
- Trenutne ceny v regionu
- Trendy a predikce
- Daňové zátěže (obecně, ne konkrétní výpočet)

---

### Prompt 2: Právní / Hypoteční aspekty

```
Bytová / rodinná práva, hypotéka:

Z exportu:
- Typ: {PropertyType}
- Stav: {Condition}
- Cena: {Price}
- Plocha: {Area}

Potřebuji vědět:
1. Jaký druh hypotéky se hodí? (Fixace, variabilní, spekulativní?)
2. Jaká jsou rizika, pokud si vezmu hypotéku na toho hause?
3. Jak to funguje s "stavebním povolením" a katastrem?
```

**Perplexity vrátí**: Právní přehled + waringy (s citacemi na právní zdroje).

---

### Prompt 3: Životní styl / Zda se hodí

```
Jsem {typ osoby} (např. "mladá rodina s dětmi", "senior po penzi", "remote worker").

Hodnotit inzerát:
1. Zda se lokalita hodí mému stylu?
2. Co bychom měli vědět předtím, než se nastěhujeme?
3. Jaký je "worst case scenario" pro tohle místo?
```

**Perplexity vrátí**: Kvalitativní analýza + komunity insights.

---

## 💬 Prompts pro Gemini (app)

Gemini je docela podobný Copilotu, ale může pracovat s obrázky.

### Prompt 1: Analýza fotek

Pokud máš fotky (local URL nebo uploadnuté):

```
Podívej se na fotky (přiložená):
1. Jaký je stav střechy / zděnění / fasády?
2. Vidíš nějaké známky vlhkosti / plísní / hmyzu?
3. Opravy, kterých se ti líbí; opravy v queue
4. Design – chutné / hezké interiéry?
```

**Gemini vrátí**: Auto-analýzu fotek (AI vision).

---

### Prompt 2: Komplexní rodin-plán

```
Jsem {typ}.

Balíček exportu: {přilož či text}
Fotky: {přilož či URL}

"Postav mi plán":
1. Jak dlouho to bude trvat, než si vezmu hypotéku?
2. Timeline rekonstrukce
3. Co udělat v prvním měsíci?
```

**Gemini vrátí**: Interaktivní plán (FAQ, čeklisty, upozornění).

---

## 🔄 Workflow: Export → AI → Rozhodnutí

```
1. Find listing v databázi.
   └─ dotnet run -- export-listing --id <GUID> -f markdown -o ~/tmp
   
2. Open ~/tmp/listing-<GUID>.md in VSCode
   └─ @copilot "Shrnutí + checklist"
   └─ Copilot generates analysis
   
3. Paste to Perplexity / Gemini for extended research
   └─ "Tržní cena, právní aspekty"
   └─ Gemini / Perplexity do research
   
4. Compile insights
   └─ Vytvoř si tabulku: Pros / Cons / Risk / Timeline / Budget
   
5. Decision: Go / No-go?
   └─ Jdi na prohlídku / Zdřív se ještě zeptej makeléře
```

---

## 📝 Praktický příklad (Real-life)

### Nalezeníí inzerátu

```
ID: 550e8400-e29b-41d4-a716-446655440000
Lokalita: Znojmo, Pod Klášterem
Cena: 4.8M Kč
Plocha: 350 m²
Stav: Třeba "ToReconstruct"
```

### Krok 1: Export

```bash
dotnet run -- export-listing \
  --id 550e8400-e29b-41d4-a716-446655440000 \
  --format markdown \
  --output ~/Downloads/
  
# Vygeneruje: ~/Downloads/listing-550e8400....md (4 KB)
```

### Krok 2: Copilot Chat (VSCode)

```
Open ~/Downloads/listing-550e8400....md

@copilot: Tady je export. Jsem mladá rodina s dítětem, máme 1M příjmu/rok.
Jaké jsou rizika koupu tohoto domu?
```

Copilot vrátí: checklist + rozpočet na dostavbu.

### Krok 3: Perplexity

```
Paste MD content + prompt:
"Je Znojmo dobré místo pro rodinu? Co se tam stalo v posledních 5 letech?"
```

Perplexity vrátí: Místní info, školství, bezpečnost, trendy.

### Krok 4: Gemini + fotky

```
Upload fotky z exportu + prompt:
"Jaký je opravdu stav tohoto domu? Co bych měl dělat v prvně řadě?"
```

Gemini vrátí: Vizuální analýza + priority.

### Krok 5: Rozhodnutí

```
Máš:
- Copilotův checklist + rozpočet
- Perplexityho market research
- Geminiho visual check
- Svůj SVOT (Strengths, Weaknesses, Opportunities, Threats)

Rozhodneš se: Go na prohlídku / Ignore
```

---

## 🎁 Bonus: Batch analýza (porovnání domů)

```bash
# Exportuji všechny domy v Znojmě (max 5M, max 10 domů)
dotnet run -- export-batch \
  --region "Jihomoravský kraj" \
  --price-max 5000000 \
  --limit 10
  
# Vygeneruje: batch-20260222-153045.md (100 KB)
```

**Prompt pro Perplexity:**

```
Porovnej pro mě těch 10 domů:
1. Kterej má nejlepší poměr cena/plocha?
2. Kterej je nejblíž škole + obchodům + nádraží?
3. Kterej by byl nejlevnější na rekonstrukci?
4. Seřaď je: rank 1–10 (best → worst) dle mého kritéria
```

**Vyjde**: Tabulka s porovnáním (excelentní pro rozhodování).

---

## ⚡ TL;DR

| Operace | Příkaz | Výstup | AI nástroj |
|---------|--------|--------|-----------|
| 1 inzerát | `export-listing --id <GUID>` | `.md` | Copilot Chat |
| Batch (filtr) | `export-batch --region X --price-max Y` | `.md` | Perplexity |
| Fotky + detail | Export + upload | `.md` + images | Gemini |
| Porovnání | `export-batch` + batch prompt | `.md` (10+ domů) | Perplexity |

---

## 🔗 Užitečné zdroje

- **Markdownu**: [CommonMark spec](https://commonmark.org/) – všechny exports jsou platný CommonMark
- **Perplexity**: https://www.perplexity.ai
- **Gemini**: https://gemini.google.com
- **Copilot Chat**: VSCode Extension (GitHub Copilot)

---

## 🚀 Dodatek: Automatizace (Future)

Pokud bys chtěl víc automatiky:

1. **Cron job** na export nových inzerátů
   ```bash
   # Každý den v 8:00
   0 8 * * * dotnet run --export-batch --status New
   ```

2. **Webhook** do Telegram/Discord
   ```
   "Nový inzerát v Znojmě! Cena: 4.8M, Plocha: 350m²"
   → [Stáhni export](link)
   ```

3. **Auto-export na Google Drive**
   ```bash
   dotnet run --export-batch --upload-gdrive --folder "My Real Estate"
   ```

Ale to je beyond scope tohoto playbooku. Zatím jsi lepší se zaměřit na质quality analytických promptu.

---

**Hotovo. Měj se! 🏡**
