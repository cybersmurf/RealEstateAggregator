# Instrukce pro AI analýzu nemovitosti

## ZÁKLADNÍ ÚDAJE

**Adresa / lokalita:** {{LOCATION}}
**Typ:** {{PROPERTY_TYPE}} / {{OFFER_TYPE}}
**Nabídková cena:** {{PRICE}}{{PRICE_NOTE}}
**Plocha:** {{AREA}}
{{ROOMS_LINE}}{{CONSTRUCTION_TYPE_LINE}}{{CONDITION_LINE}}**Kategorie stáří (age_category):** 🆕 NOVOSTAVBA / VE VÝSTAVBĚ
**Zdroj inzerátu:** {{SOURCE_NAME}} ({{SOURCE_CODE}})
**URL:** [{{URL}}]({{URL}})

---

## TVŮJ ÚKOL

Prohlédni si fotky (URL jsou v DATA.json v poli `photo_urls`) a přečti `INFO.md` + `DATA.json`.  
Proveď **komplexní analýzu této nemovitosti** z pohledu potenciálního kupce / investora.

{{PHOTO_LINKS_SECTION}}

### DŮLEŽITÁ PRAVIDLA

1. **Drž se pouze informací v INFO.md, DATA.json a z fotek.**  
   - Pokud něco v datech NENÍ, konstatuj „z dat nelze určit" a NEVYMÝŠLEJ si.  
   - Nepřidávej konkrétní srovnávací inzeráty ani přesné ceny jiných domů, pokud nejsou přímo v datech.  
   - O cenách konkurence mluv jen obecně (nižší / vyšší / podobná) nebo v hrubém rozmezí.
2. **`age_category = new_build` — NEPIŠ NIC o rekonstrukci, opotřebení ani nutnosti oprav.**  
   Dům bude dokončen / kolaudován v budoucnu. Hodnoť kvalitu projektu, developera a záruky.  
   Zmiňuj pouze běžnou údržbu v horizontu 10–20 let (servis technologií, výmalba apod.).
3. Pokud si nejsi jistý, **explicitně to řekni** (např. „Z dostupných dat nelze posoudit, doporučuji ověřit při prohlídce modelu").
4. **Odpovídej stručně, v bodech, česky**, bez marketingových frází.
5. **Strukturuj výstup jako profesionální analýzu:**  
   - Začni tabulkou základních parametrů.  
   - Používej emoji ikony: ✅ = dobré / ⚠️ = ověřit / 🔴 = kritické / 🟡 = středně důležité / 🟢 = nízké riziko.

---

## STRUKTURA VÝSTUPU

Na začátek dej hlavičku:

```
**ANALÝZA NEMOVITOSTI — NOVOSTAVBA**

{Lokalita} | {Typ} {Dispozice} | {Plocha užitná} m² / {Plocha pozemku} m² | {Cena} Kč

📋 **Základní parametry**

| Parametr | Hodnota |
|---|---|
| Adresa | {z DATA.json} |
| Dispozice | {z DATA.json nebo popisu} |
| Užitná plocha | {z DATA.json} |
| Pozemek | {z DATA.json} |
| Cena nabídková | {z DATA.json} |
| Cena za m² užitné plochy | {vypočítej: cena / užitná plocha} |
| Typ stavby / konstrukční systém | {z DATA.json nebo popisu} |
| Termín dokončení / kolaudace | {z DATA.json nebo popisu} |
| Vytápění | {z DATA.json nebo popisu} |
| Zdroj | {{SOURCE_NAME}} |
```

---

### 1. ANALÝZA KVALITY PROJEKTU A DEVELOPERA

**Klíčové technologie a vybavení** (místo „Co bylo renovováno" — dům je nový):
- ✅ {moderní systémy z popisu: LOXONE, tepelné čerpadlo, rekuperace, klimatizace, závlahy, FVE apod.}
- „Pokud technologie nejsou v datech: Technologické vybavení není v inzerátu specifikováno — vyžádat."

**Pozitiva dle fotografií a popisu:**
- ✅ {standard provedení, kvalita materiálů, interiér, exteriér}

**Potenciální negativa / nezjišťené skutečnosti:**
- ⚠️ {co nelze z fotek posoudit: skutečný stav dokončení, PENB, záruky developera, kolaudace}
- ⚠️ **NEPIŠ o rekonstrukci** — nemovitost je nová.

---

### 2. HODNOCENÍ CENY

**Srovnání s trhem** (jen obecně, bez konkrétních inzerátů):
- „Cena {{PRICE}} za {plocha} m² působí v kontextu parametrů spíše nízká / odpovídající / vyšší."

**Finanční kalkulace (celková investice)**:

| Položka | Částka |
|---|---|
| Kupní cena (nabídková) | {{PRICE}} |
| Daň z nabytí (4 % — dle smlouvy) | {vypočítej: cena × 0,04} |
| Právní poradenství + notář | ~30 000 Kč |
| Vybavení / kuchyň / podlahy (standard developer) | {odhad: 100–300k nebo 0 dle standardu} |
| **CELKOVÁ INVESTICE (odhad)** | **{rozmezí Kč}** |
| **Maximální rozumná nabídková cena** | **{rozmezí Kč}** |

_Poznámka: odhad oprav = 0 (novostavba, záruční lhůta min. 3 roky)._

---

### 3. LOKACE A OKOLÍ

- Plusy a mínusy dle textu (INFO/DATA) — doprava, služby, klid/hluk, charakter obce, vzdálenost do města.
- Pokud data chybí → „Hluk, kriminalita a průmysl v okolí nelze z inzerátu posoudit — ověřit samostatně."
- Rizika lokality: venkovská poloha = závislost na auto, nízká likvidita trhu.

---

### 4. TECHNICKÉ ŘEŠENÍ A STANDARDY

| Položka | Stav | Poznámka |
|---|---|---|
| Konstrukční systém | ✅/⚠️ | {zděný / dřevostavba / panel} |
| Energetická třída (PENB) | ✅/⚠️ | {A/B/C — vyžádat} |
| Tepelné čerpadlo / topení | ✅/⚠️ | {typ, zdroj energie} |
| Rekuperace | ✅/⚠️ | {ano/ne/nespecifikováno} |
| Smart home | ✅/⚠️ | {LOXONE / KNX / standard} |
| Okna a podlahy | ✅/⚠️ | {standard dle inzerátu} |
| Parkování / garáž | ✅/⚠️ | {v ceně / příplatek} |
| Sklep / terasa / zahrada | ✅/⚠️ | {v ceně / příplatek} |

---

### 5. DISPOZICE A VYUŽITELNOST

- Rodina / pár / investice do nájmu.
- Světlost, návaznost místností, zahrada.
- Možnost úprav standardu v rámci developerského procesu (kuchyňská linka, obklady).
- Pokud chybí půdorys: popiš jen to, co je z fotek / popisu zjevné.

---

### 6. RIZIKA A RED FLAGS

**🔴 Kritické body** (vyžadují prověření před podpisem):
- {např. chybí kolaudační rozhodnutí, developer v insolvenci, kupní cena bez vinkulace}

**🟡 Středně důležité body** (ověřit v smlouvě nebo při prohlídce modelu):
- {např. termín dokončení bez sankcí, změny projektu bez souhlasu, nejasný energetický štítek}

**🟢 Nízká rizika**:
- {např. standardní developerský projekt, záruky dle NOZ min. 3 roky, notářská úschova sjednána}

---

### 7. INVESTIČNÍ ANALÝZA

**Odhadni reálný nájem po dokončení** (rozmezí) v Kč/měsíc:
- Dlouhodobý pronájem (rodina / pár).
- Případně krátkodobý (Airbnb / turistika — jen pokud relevantní lokalita).

| Položka | Hodnota |
|---|---|
| Odhadovaný nájem (měsíční) | {rozmezí} Kč/měsíc |
| Roční příjem hrubý | {roční nájem} Kč |
| Celková investice (vč. vybavení) | {z sekce 2} Kč |
| Hrubý yield (nižší scénář) | {(roční nájem / investice) × 100} % |
| Hrubý yield (vyšší scénář) | {(roční nájem / investice) × 100} % |
| Čistý yield po nákladech (~25 %) | {hrubý yield × 0,75} % |
| Prostá návratnost (payback) | {investice / roční čistý příjem} let |

_Odhad nájmu je hrubý, nutno ověřit na lokálním trhu._

---

### 8. DOPORUČENÍ

**{🟢/🟡/🔴} VERDIKT** — např. „🟡 VYJEDNÁVAT – podmíněně doporučuji ke koupi jako investici do nájmu"

**Odůvodnění:** 3–5 bodů (výhody novostavby, klíčová rizika, klíčové předpoklady).

**Maximální rozumná nabídková cena:**  
**{rozmezí} Kč** (prostor pro vyjednávání: {sleva v Kč / %})

**Co prověřit před podpisem smlouvy:**
- Kolaudační rozhodnutí / oznámení o budoucím užívání
- Vinkulace kupní ceny (notářská úschova / bankovní akreditiv)
- Smlouva o smlouvě budoucí — sankce za prodlení, exit klauzule
- Výpis z katastru — zástavní práva developera
- Energetický průkaz (PENB) — třída A/B je pro novostavbu standard
- {Další body specifické pro tuto nemovitost — 3–5 položek}

---

*Analýza zpracována na základě dat z inzerátu. Nemůže nahradit fyzickou prohlídku modelu, posudek odborníka ani právní due diligence.*

---

## POZNÁMKY Z PROHLÍDKY MODELU _(vyplň ručně po prohlídce)_

| Položka | Poznámka |
|---|---|
| Celkový dojem z modelu / vzorové jednotky | |
| Kvalita provedení a materiálů | |
| Co se mi líbilo | |
| Co mě znepokojilo | |
| Co říkal makléř / developer | |
| Nesrovnalosti s inzerátem | |

## DOPLŇUJÍCÍ KONTEXT _(pro lidského uživatele, AI může ignorovat pokud není vyplněno)_

**Můj rozpočet:** _(max cena včetně případných nákladů na vybavení)_  
**Účel:** _(vlastní bydlení / investice / pronájem)_  
**Timeline:** _(jak rychle potřebuji koupit / dokdy čekám na dokončení)_

{{DRIVE_FOLDER_SECTION}}
