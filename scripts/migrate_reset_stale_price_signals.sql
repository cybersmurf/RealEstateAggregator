-- ============================================================================
-- Reset cenových signálů postavených na špatných nebo neaktuálních vstupech
-- ============================================================================
-- Spuštění:
--   docker cp scripts/migrate_reset_stale_price_signals.sql realestate-db:/tmp/ && \
--   docker exec realestate-db psql -U postgres -d realestate_dev -f /tmp/migrate_reset_stale_price_signals.sql
--
-- KONTEXT
-- Cenový signál se generuje z Kč/m². Bulk job ale bere jen řádky s price_signal IS NULL,
-- takže jednou spočítaný signál tam zůstal navždy – i když se mezitím změnila cena
-- nebo se opravila plocha.
--
-- Reálný případ (dům v Lechovicích, 7 300 000 Kč):
--   BAZOS    – v ploše byla kuchyň (10 m²)      → 730 000 Kč/m² → "Nadhodnocená"
--   SREALITY – v ploše byl pozemek (1 238 m²)   →   5 897 Kč/m² → "Přiměřená"
-- Skutečná plocha domu je 129 m², tj. 56 589 Kč/m². Neplatil ani jeden signál.
--
-- Scraper nově signál invaliduje sám při změně ceny nebo plochy (database.py)
-- a API odmítne počítat z nevěrohodného Kč/m² (OllamaTextService). Tenhle skript
-- uklidí, co v DB už je.
-- ============================================================================

BEGIN;

\echo '── Před resetem ──'
SELECT count(*) FILTER (WHERE price_signal IS NOT NULL) AS se_signalem,
       count(*) FILTER (WHERE price_signal IS NULL)     AS bez_signalu
FROM re_realestate.listings WHERE is_active;

-- ── 1. Signály mimo věrohodné meze Kč/m² ────────────────────────────────────
-- Stejné meze jako OllamaTextService.PlausiblePricePerM2.
UPDATE re_realestate.listings
SET price_signal = NULL, price_signal_reason = NULL, price_signal_at = NULL
WHERE is_active AND price_signal IS NOT NULL AND price > 0
  AND (
        coalesce(area_built_up, area_land) IS NULL
     OR coalesce(area_built_up, area_land) <= 0
     OR (property_type <> 'Land'
         AND (price / coalesce(area_built_up, area_land) < 6000
           OR price / coalesce(area_built_up, area_land) > 250000))
     OR (property_type = 'Land'
         AND price / coalesce(area_built_up, area_land) > 50000)
  );

-- ── 2. Signály spočítané před poslední změnou ceny ──────────────────────────
UPDATE re_realestate.listings l
SET price_signal = NULL, price_signal_reason = NULL, price_signal_at = NULL
WHERE l.is_active AND l.price_signal IS NOT NULL
  AND EXISTS (
      SELECT 1 FROM re_realestate.listing_price_history h
      WHERE h.listing_id = l.id AND h.recorded_at > l.price_signal_at
  );

-- ── 3. Signály SREALITY domů z doby, kdy area_built_up držela plochu pozemku ─
-- Oprava parseru je v commitu 38cde17 (26. 8. 2026).
UPDATE re_realestate.listings
SET price_signal = NULL, price_signal_reason = NULL, price_signal_at = NULL
WHERE is_active AND price_signal IS NOT NULL
  AND source_code = 'SREALITY' AND property_type = 'House'
  AND price_signal_at < TIMESTAMPTZ '2026-08-26 00:00:00+02';

\echo '── Po resetu ──'
SELECT count(*) FILTER (WHERE price_signal IS NOT NULL) AS se_signalem,
       count(*) FILTER (WHERE price_signal IS NULL)     AS k_prepocitani
FROM re_realestate.listings WHERE is_active;

COMMIT;

-- ── Další krok ──────────────────────────────────────────────────────────────
-- Přepočítat bulk jobem (UI → Scrape, nebo POST /api/ollama/bulk-price-opinion).
-- Inzeráty s nevěrohodnou plochou signál nedostanou a zůstanou bez štítku –
-- to je záměr: prázdno je poctivější než sebejistý nesmysl.
-- Nejdřív ale nechat proběhnout scrape, ať se opraví plochy (SREALITY + BAZOS).
