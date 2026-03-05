# Instrukce pro AI analýzu nemovitosti

## ZÁKLADNÍ ÚDAJE

**Adresa / lokalita:** {{LOCATION}}
**Typ:** {{PROPERTY_TYPE}} / {{OFFER_TYPE}}
**Nabídková cena:** {{PRICE}}{{PRICE_NOTE}}
**Plocha:** {{AREA}}
{{ROOMS_LINE}}{{CONSTRUCTION_TYPE_LINE}}{{CONDITION_LINE}}**Kategorie stáří (age_category):** existující nemovitost
**Zdroj inzerátu:** {{SOURCE_NAME}} ({{SOURCE_CODE}})
**URL:** [{{URL}}]({{URL}})

---

## TVŮJ ÚKOL

Prohlédni si fotky (URL jsou v DATA.json v poli `photo_urls`) a přečti `INFO.md` + `DATA.json`.  
Proveď **komplexní analýzu této nemovitosti** z pohledu potenciálního kupce / investora.

{{PHOTO_LINKS_SECTION}}

### DŮLEŽITÁ PRAVIDLA

1. **Drž se pouze informací v datech a z fotek.**  
   - Pokud něco v datech NENÍ, konstatuj „z dat nelze určit" a NEVYMÝŠLEJ si.  
   - Nepřidávej konkrétní srovnávací inzeráty ani přesné ceny jiných domů, pokud nejsou přímo v datech.  
   - O cenách konkurence mluv jen obecně (nižší / vyšší / podobná) nebo v hrubém rozmezí.
2. **Respektuj `age_category = existing`** — hodnoť viditelný stav, rekonstrukce a opotřebení.
3. Pokud si nejsi jistý, **explicitně to řekni** (např. „Stav střechy nelze z fotek posoudit, doporučuji odbornou prohlídku").
4. **Odpovídej stručně, v bodech, česky**, bez marketingových frází.
5. **Strukturuj výstup jako profesionální analýzu:**
6. ⚠️ **KRITICKÉ — Maximální rozumná nabídková cena MUSÍ být NIŽŠÍ než nabídková cena {{PRICE}}.**  
   Cílem je vyjednat slevu, ne zaplatit víc. Vzorec:  
   `max cena = nabídková cena − odhadované opravy − rezerva pro vyjednávání (3–8 %)`  
   Příklad: nabídková 4 200 000 Kč, opravy 400 000 Kč, rezerva 5 % → max nabídka ≈ 3 600 000 Kč.  
   **Nikdy nenapisuj max cenu vyšší nebo rovnou nabídkové ceně.**
7. ⚠️ **KRITICKÉ — Sekci „POZNÁMKY Z PROHLÍDKY" NEVYPLŇUJ.** Ponech všechny buňky tabulky prázdné — kupec ji vyplní ručně po fyzické návštěvě.  
   - Začni tabulkou základních parametrů.  
   - Používej emoji ikony: ✅ = dobré / ⚠️ = ověřit / 🔴 = kritické / 🟡 = středně důležité / 🟢 = nízké riziko.

---

## STRUKTURA VÝSTUPU

Na začátek dej hlavičku:

```
**ANALÝZA NEMOVITOSTI**

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
| Typ stavby | {z DATA.json nebo popisu} |
| Stav | {z DATA.json nebo popisu} |
| Vytápění | {z DATA.json nebo popisu} |
| Zdroj | {{SOURCE_NAME}} |
```

---

### 1. ANALÝZA STAVU A KVALITY

**Co bylo renovováno** (dle popisu nebo fotek — uveď, proč je to důležité):
- {seznam provedených rekonstrukcí/oprav — nová střecha, fasáda, okna, rozvody, koupelna apod.}
- Pokud nic není uvedeno: „Z dostupných dat nelze renovace posoudit."

**Pozitiva dle fotografií a popisu:**
- ✅ {seznam pozitivních prvků}

**Negativa / otázky:**
- ⚠️ {viditelné problémy: starší topný systém, zastaralé instalace, chybějící koupelna v patře, vlhkost apod.}
- ⚠️ „{Co nelze z fotek posoudit — doporučit prohlídku odborníka}"

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
| {Doplňkové opravy dle stavu — např. dostavba koupelny 150–250k, výměna topení 100–400k, oprava střechy 200–500k} | {hrubý odhad rozmezí} |
| **CELKOVÁ INVESTICE (odhad)** | **{rozmezí Kč}** |
| **Maximální rozumná nabídková cena** | **{rozmezí Kč}** |

---

### 3. LOKACE A OKOLÍ

- Plusy a mínusy dle textu (INFO/DATA) — doprava, služby, klid/hluk, charakter obce, vzdálenost do města.
- Pokud data chybí → „Hluk, kriminalita a průmysl v okolí nelze z inzerátu posoudit — ověřit samostatně."
- Rizika lokality: venkovská poloha = závislost na auto, nízká likvidita trhu, povodňové riziko (ověřit dle ČHMÚ).

---

### 4. TECHNICKÝ STAV

| Položka | Stav | Poznámka |
|---|---|---|
| Střecha | ✅/⚠️/🔴 | {stav, nutnost výměny} |
| Fasáda | ✅/⚠️/🔴 | {zateplení, PENB} |
| Okna | ✅/⚠️/🔴 | {plastová/dřevěná, trojsklo} |
| Elektrorozvody | ✅/⚠️/🔴 | {nové/staré} |
| Topení | ✅/⚠️/🔴 | {typ: TČ/plyn/tuhá paliva, stáří} |
| Koupelna | ✅/⚠️/🔴 | {stav, chybějící v 2NP?} |
| Vlhkost / plísně | ⚠️ | {ověřit při prohlídce, zejm. sklep} |
| Kanalizace / vodovod | ⚠️ | {veřejná síť vs. septik} |
| Energetický štítek (PENB) | ⚠️ | {vyžádat, vliv na hodnotu} |

---

### 5. DISPOZICE A VYUŽITELNOST

- Rodina / pár / investice do nájmu.
- Světlost, návaznost místností, využitelnost zahrady.
- Dvougenerační potenciál (pokud relevantní).
- Pokud chybí půdorys: popiš jen to, co je z fotek zjevné.

---

### 6. RIZIKA A RED FLAGS

**🔴 Kritické body** (vyžadují řešení před koupí nebo výraznou slevou):
- {např. chybí koupelna v 2NP, voda odpojená, PENB chybí, koryto na pozemku — povodně}

**🟡 Středně důležité body** (ověřit při prohlídce nebo vyjednávání):
- {např. starší topný systém — výměna do 5 let, hluk ze silnice, septik vs. kanalizace}

**🟢 Nízká rizika**:
- {např. právní spory — standardní ověření KN, povodňové riziko nízké}

**📋 Co ověřit v katastru nemovitostí (KN):**

| Co ověřit | Proč | Závažnost |
|---|---|---|
| Zástavní práva (hypotéky, exekuce) na LV | Kupující může přebrat dluh prodávajícího | 🔴 Kritické |
| Věcná břemena (průchod, vedení sítí, nájemní právo) | Omezuje užívání nemovitosti | 🟡 Střední |
| Skutečná výměra pozemku vs. inzerát | Rozdíl = argument pro slevu nebo problém při kolaudaci | ⚠️ Ověřit |
| Druh pozemku (zahrada / zastavěná plocha / orná půda) | Orná půda má omezení stavby a jiný převod | ⚠️ Ověřit |
| Přístupová cesta – vlastní, nebo cizí pozemek? | Bez přístupu nelze nemovitost užívat ani prodat | 🔴 Kritické |

*Link na nahlížení.cuzk.cz je dostupný na detailu inzerátu (tlačítko „Otevřít v KN").*

---

### 7. INVESTIČNÍ ANALÝZA

**Odhadni reálný nájem** (rozmezí) v Kč/měsíc:
- Dlouhodobý pronájem (rodina / pár).
- Případně dvougenerační využití (1NP vlastní + 2NP pronájem).

| Položka | Hodnota |
|---|---|
| Odhadovaný nájem (měsíční) | {rozmezí} Kč/měsíc |
| Roční příjem hrubý | {roční nájem} Kč |
| Celková investice (vč. úprav) | {z sekce 2} Kč |
| Hrubý yield (nižší scénář) | {(roční nájem / investice) × 100} % |
| Hrubý yield (vyšší scénář) | {(roční nájem / investice) × 100} % |
| Čistý yield po nákladech (~25 %) | {hrubý yield × 0,75} % |
| Prostá návratnost (payback) | {investice / roční čistý příjem} let |

_Odhad nájmu je hrubý, nutno ověřit na lokálním trhu._

---

### 8. DOPORUČENÍ

**{🟢/🟡/🔴} VERDIKT** — např. „🟡 VYJEDNÁVAT – podmíněně doporučuji ke koupi pro vlastní bydlení"

**Odůvodnění:** 3–5 bodů (výhody, nevýhody, klíčové předpoklady).

**Maximální rozumná nabídková cena:**  
**{částka} Kč** — MUSÍ být nižší než {{PRICE}} (viz pravidlo č. 6)  
_(vyjednávací prostor: {sleva v Kč} Kč / {sleva v %} % oproti nabídkové ceně)_

**Co prověřit při prohlídce / před podpisem:**
- Kolaudační rozhodnutí nebo oznámení o užívání stavby
- Výpis z katastru — zástavní práva, věcná břemena
- Energetický průkaz (PENB) — třída A/B je žádoucí
- {Další body specifické pro tuto nemovitost — 5–10 položek}

---

*Analýza zpracována na základě dat z inzerátu. Nemůže nahradit fyzickou prohlídku, posudek odborníka ani právní due diligence.*

---

## POZNÁMKY Z PROHLÍDKY _(vyplň ručně po prohlídce — AI tuto sekci NEVYPLŇUJE, buňky nechej prázdné)_

| Položka | Poznámka |
|---|---|
| Celkový dojem | |
| Co se mi líbilo | |
| Co mě znepokojilo | |
| Co říkal makléř / prodejce | |
| Nesrovnalosti s inzerátem | |
| Vůně, sousedé, okolí | |

## DOPLŇUJÍCÍ KONTEXT _(pro lidského uživatele, AI může ignorovat pokud není vyplněno)_

**Můj rozpočet:** _(max cena včetně případných oprav)_  
**Účel:** _(vlastní bydlení / investice / pronájem)_  
**Timeline:** _(jak rychle potřebuji koupit)_

{{DRIVE_FOLDER_SECTION}}
