-- ============================================================================
-- Oprava fotek, které sdílí jeden soubor na disku (kolize stored_url)
-- ============================================================================
-- Spuštění:
--   docker cp scripts/migrate_fix_photo_stored_url_collisions.sql realestate-db:/tmp/ && \
--   docker exec realestate-db psql -U postgres -d realestate_dev -f /tmp/migrate_fix_photo_stored_url_collisions.sql
--
-- KONTEXT
-- Scraper ukládal inline stažené fotky jako {order_index}.jpg. Když inzerát dostal
-- nové fotky, stáhly se na pozici, kterou už měla jiná fotka, a přepsaly její soubor.
-- Obě řádky pak ukazovaly na stejný stored_url → UI i klasifikace viděly cizí obrázek.
-- (Září 2026: 762 skupin, 1 537 fotek, 247 inzerátů.)
--
-- Scraper nově pojmenovává soubor hashem original_url (database.py → photo_file_stem).
-- Tenhle skript uklidí, co v DB už je:
--   1. zazálohuje dotčené řádky do listing_photos_collision_backup,
--   2. vynuluje jim stored_url (UI spadne na original_url) a vše, co se odvodilo
--      z obrázku (klasifikace, popisy, alt text) – nevíme, čí obrázek model viděl.
-- Soubory na disku se nemažou.
-- ============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS re_realestate.listing_photos_collision_backup AS
SELECT *, now() AS backed_up_at FROM re_realestate.listing_photos WITH NO DATA;

WITH collided AS (
    SELECT listing_id, stored_url
    FROM re_realestate.listing_photos
    WHERE stored_url IS NOT NULL
    GROUP BY listing_id, stored_url
    HAVING count(*) > 1
)
INSERT INTO re_realestate.listing_photos_collision_backup
SELECT p.*, now()
FROM re_realestate.listing_photos p
JOIN collided c USING (listing_id, stored_url);

\echo '── Zálohováno (dnes) ──'
SELECT count(*) AS fotek, count(classified_at) AS klasifikovanych, count(DISTINCT listing_id) AS inzeratu
FROM re_realestate.listing_photos_collision_backup
WHERE backed_up_at >= date_trunc('day', now());

UPDATE re_realestate.listing_photos p
SET stored_url                = NULL,
    classified_at             = NULL,
    photo_category            = NULL,
    photo_labels              = NULL,
    damage_detected           = false,
    classification_confidence = NULL,
    photo_description         = NULL,
    classification_feedback   = NULL,
    alt_text                  = NULL,
    ai_description            = NULL
FROM (
    SELECT listing_id, stored_url
    FROM re_realestate.listing_photos
    WHERE stored_url IS NOT NULL
    GROUP BY listing_id, stored_url
    HAVING count(*) > 1
) c
WHERE p.listing_id = c.listing_id AND p.stored_url = c.stored_url;

\echo '── Zbývající kolize (má být 0) ──'
SELECT count(*) AS skupin FROM (
    SELECT 1 FROM re_realestate.listing_photos
    WHERE stored_url IS NOT NULL
    GROUP BY listing_id, stored_url HAVING count(*) > 1
) x;

COMMIT;

-- ── Další krok ──────────────────────────────────────────────────────────────
-- Znovu stáhnout (API ukládá s GUID v názvu, kolize nevzniká):
--   POST /api/photos/bulk-download?listingId=<id>   pro každý listing ze zálohy
-- Pak znovu klasifikovat inzeráty, které klasifikované byly:
--   POST /api/photos/bulk-classify?listingId=<id>
-- Fotky, jejichž original_url vrátí 404/410, download service smaže (záloha zůstává).
