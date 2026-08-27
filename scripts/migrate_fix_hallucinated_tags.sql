-- ============================================================================
-- Úklid halucinovaných SmartTags a z nich odvozeného condition
-- ============================================================================
-- Spuštění:
--   docker cp scripts/migrate_fix_hallucinated_tags.sql realestate-db:/tmp/ && \
--   docker exec realestate-db psql -U postgres -d realestate_dev -f /tmp/migrate_fix_hallucinated_tags.sql
--
-- KONTEXT
-- Prompt pro SmartTags obsahoval větu "If fewer than 5 relevant tags exist, fill
-- remaining slots with the most relevant general tags", tedy přímý pokyn vymýšlet.
-- Reálný případ (BAZOS 222122556): 60 let starý dům "vhodný k modernizaci" dostal
-- tagy ["cihlový dům","novostavba","terasa","zahrada","kolaudovaný"] a z tagu
-- "novostavba" se pak odvodil condition = 'Novostavba'.
--
-- Prompt je opravený a zápis nově hlídá SmartTagValidator (porovnání kmene tagu
-- se začátkem slova ve zdrojovém textu). Tenhle skript řeší data, která už v DB jsou.
--
-- Regex \m = začátek slova – stejná logika jako validátor v C#.
-- Proto "nepodsklepený" nepotvrdí tag "sklep", zatímco "podsklepený" ano.
-- ============================================================================

BEGIN;

-- ── 1. Přehled před úklidem ─────────────────────────────────────────────────
\echo '── Stav před úklidem ──'
SELECT
    count(*) FILTER (WHERE condition = 'Novostavba') AS condition_novostavba,
    count(*) FILTER (WHERE smart_tags IS NOT NULL)   AS s_tagy
FROM re_realestate.listings WHERE is_active;

-- ── 2. condition = 'Novostavba' bez opory v textu ───────────────────────────
-- Vzniklo odvozením z halucinovaného tagu. Nastavíme NULL – regex enrichment
-- ve scraperu (database.py) hodnotu doplní správně při příštím upsertu.
UPDATE re_realestate.listings
SET condition = NULL
WHERE is_active
  AND condition = 'Novostavba'
  AND coalesce(title, '') || ' ' || coalesce(description, '')
      !~* '\m(novostavb|vystavb|výstavb|kolaudac|kolaudovan|developersk)';

\echo '── Vyčištěný condition (řádků) ──'

-- ── 3. Nepodložené tagy → smazat celou sadu, ať se přegeneruje ──────────────
-- Nemá smysl JSON pole ořezávat v SQL; nastavíme NULL a bulk job je vygeneruje
-- znovu, tentokrát přes validátor. Podmínka hledá tvrzení, která v textu nemají oporu.
UPDATE re_realestate.listings
SET smart_tags = NULL,
    smart_tags_at = NULL
WHERE is_active
  AND smart_tags IS NOT NULL
  AND (
        (smart_tags ~* 'novostavb'  AND coalesce(title,'') || ' ' || coalesce(description,'') !~* '\m(novostavb|vystavb|výstavb|kolaudac|kolaudovan|developersk)')
     OR (smart_tags ~* 'kolaudov'   AND coalesce(title,'') || ' ' || coalesce(description,'') !~* '\m(kolaudac|kolaudovan)')
     OR (smart_tags ~* 'teras'      AND coalesce(title,'') || ' ' || coalesce(description,'') !~* '\mteras')
     OR (smart_tags ~* 'baz[eé]n'   AND coalesce(title,'') || ' ' || coalesce(description,'') !~* '\mbaz[eé]n')
     OR (smart_tags ~* 'v[yý]tah'   AND coalesce(title,'') || ' ' || coalesce(description,'') !~* '\mv[yý]tah')
     OR (smart_tags ~* 'gar[aá][zž]' AND coalesce(title,'') || ' ' || coalesce(description,'') !~* '\mgar[aá][zž]')
     OR (smart_tags ~* 'sklep'      AND coalesce(title,'') || ' ' || coalesce(description,'') !~* '\m(sklep|podsklep)')
     OR (smart_tags ~* 'zahrad'     AND coalesce(title,'') || ' ' || coalesce(description,'') !~* '\mzahrad')
     OR (smart_tags ~* 'podkrov'    AND coalesce(title,'') || ' ' || coalesce(description,'') !~* '\m(podkrov|pudni|půdní|vestavb)')
  );

-- ── 4. Přehled po úklidu ────────────────────────────────────────────────────
\echo '── Stav po úklidu ──'
SELECT
    count(*) FILTER (WHERE condition = 'Novostavba') AS condition_novostavba,
    count(*) FILTER (WHERE smart_tags IS NOT NULL)   AS s_tagy,
    count(*) FILTER (WHERE smart_tags IS NULL)       AS k_pregenerovani
FROM re_realestate.listings WHERE is_active;

COMMIT;

-- ── 5. Další krok ───────────────────────────────────────────────────────────
-- Vyčištěné inzeráty přegenerovat bulk jobem (UI → Scrape, nebo
-- POST /api/ollama/bulk-smart-tags). Zápis nově chrání SmartTagValidator.
--
-- Pozn.: tenhle skript řeší jen tvrzení, která umí ověřit regex. Důkladná varianta
-- je smazat smart_tags úplně a nechat přegenerovat všech ~2 200 aktivních inzerátů –
-- s validátorem je to bezpečné, jen to stojí čas Ollamy.
