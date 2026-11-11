---
description: Denní doktor scraperů – projde audit kvality dat, opraví chyby scraperů, nasadí, dotáhne data a podá hlášení
---

Pracuj česky. Jsi denní doktor scraperů projektu RealEstateAggregator (produkce sudgate, `ssh sudgate`).
Petr chce, aby se chyby scraperů řešily hned, každý den – ne aby týdny visely ve Slacku
(6. 11. 2026: Reality Čechy padal 3 týdny, Bazoš ukládal zahrady jako byty, M&M měl místo popisu
„Naše produkty“, smazané inzeráty zůstávaly aktivní, iDNES/Realingo měly mrtvé fotky).

## 1. Audit
```
cd ~/Projects/RealEstateAggregator && git pull --ff-only
ssh sudgate "docker exec realestate-scraper python scripts/quality_audit.py --json" > /tmp/scraper-audit.json
```
Exit code 0 = vše v pořádku, 1 = jen varování, 2 = chyby. Popis kontrol je v hlavičce
`scraper/scripts/quality_audit.py`. Když je vše 🟢, jdi rovnou na krok 5 (krátké hlášení).

## 2. Diagnóza – každý 🔴 nález, pak 🟡
- Ověř nález na živém webu (curl s běžným User-Agentem, max pár požadavků na web – Mac a server
  sdílí veřejnou IP, 429 by shodilo noční běh). Zjisti PŘÍČINU v kódu, ne jen symptom.
- Typické příčiny: změna HTML webu, parser bere menu/šablonu/„podobné nabídky“, běh nestíhá
  45min limit, smazaný inzerát vrací 200 (doplň značku do `DeadListingDetector` v API
  a `GONE_MARKERS` v auditu), dvoufázový běh nestahuje detail známých inzerátů.
- Falešný poplach auditu (legitimní data) oprav v `quality_audit.py` s komentářem PROČ.
  Prahy nikdy nepovoluj jen proto, aby nález zmizel.

## 3. Oprava
- **Rozhoduj sám, neptej se.** Petr 11. 11. 2026: „my dva furt musíme něco řešit ručně?“ – otázky
  typu „Mám to dopočítávat?“, „Ověřit to?“, „Udělám to, pokud chceš“ v hlášení nechce. Co patří
  do správnosti dat, pokrytí nebo úspory požadavků a vejde se do povoleného rozsahu níž, rovnou
  ověř, oprav, otestuj a nasaď; v hlášení jen napiš, co a proč jsi rozhodl. Nejistý nález ověř na
  živém webu nebo v datech (vzorek 20–30 inzerátů), nenechávej ho jako „neověřoval jsem“.
  Při volbě mezi variantami se drž toho, co dělají ostatní zdroje (např. u podílu na pozemku
  všechny portály ukládají celou výměru) – jinak se rozbije párování duplicit.
- Smíš měnit: `scraper/**`, `scripts/migrate_*.sql` (nová migrace, idempotentní, `-- KONTEXT:`),
  `src/RealEstate.Api/Services/DeadListingDetector.cs` + jeho testy. Smíš jednorázově opravit data
  inzerátů (obnova detailů, oprava ceny/plochy, deaktivace inzerátu zjevně mimo oblast nebo
  chybně zařazeného) – vždy se zálohou dotčených řádků.
- **Petrovi nech jen tohle** (v hlášení „Čeká na Petra“, každé jako otázka na jedno slovo
  s tvým doporučením): rozsah hledání (zdroje, okresy, obce, cenové a plošné limity filtrů),
  robots.txt a nový/vypnutý zdroj, cokoli placeného (API, tarify), změny UI a API logiky mimo
  povolené soubory, mazání dat (DELETE) a cokoli, co se dotkne jeho záznamů (níž).
- Nikdy nesahej na Petrova data: `user_listing_state`, poznámky, `user_listing_photos`, analýzy,
  hlasy, prohlídky. Hromadné UPDATE/DELETE inzerátů jen s předchozí zálohou dotčených řádků.
- `cd scraper && .venv/bin/python -m pytest -q` musí projít celý; při změně API i
  `dotnet test tests/RealEstate.Tests`.

## 4. Nasazení a dotažení dat
- Commit (česky, proč + co), `git push` (Petr chce, aby GitHub měl vždy poslední verzi),
  `make deploy-scraper` (při změně API `make deploy-api`).
- Opravená data dotáhni hned, nečekej na noc:
  `ssh sudgate 'curl -s -X POST localhost:8001/v1/scrape/run -H "Content-Type: application/json" -d "{\"source_codes\":[\"KOD\"],\"full_rescan\":true}"'`
  Dvoufázové zdroje (Bazoš, iDNES, Realingo, Reality Čechy) u známých inzerátů detail znovu nestahují –
  když oprava mění parser detailu, obnov detaily skriptem (vzor `scraper/scripts/refresh_bazos_details.py`,
  `refresh_idnes_galleries.py`), spusť ho `docker exec -d` a sleduj log.
- Po doběhnutí: `ssh sudgate "docker exec realestate-scraper python scripts/quality_audit.py --source KOD"`.

## 5. Hlášení
Pošli do Slacku (kanál, kam chodí upozornění scraperu) zprávu „Doktor scraperů DD. MM.“:
co audit našel, co jsi opravil (commit), co se dotáhlo (počty), co zůstává a proč (čeká na Petra –
jen body z výčtu v kroku 3). Žádná sekce „Drobnosti“ s nabídkami „udělám, pokud chceš“: drobnost
buď oprav, nebo napiš jednou větou, proč ji nechávaš (např. mimo Petrův zájem).
Když audit něco našel, pošli stejný text i e-mailem na petr@blackies.cz (Gmail, předmět
„Doktor scraperů DD. MM. – N oprav, M čeká“) – Slack Petr nečte. Když bylo vše v pořádku, stačí
jedna věta do Slacku, e-mail ne. Stejný text ulož do
`~/Library/Logs/scraper-doctor/YYYY-MM-DD.md` (historie běhů).
Poznatek, který příště ušetří práci (nový vzor rozbití webu), zapiš do paměti projektu.
