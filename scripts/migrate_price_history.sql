-- Migration: Price history tracking
-- Run: docker exec -i realestate-db psql -U postgres -d realestate_dev < scripts/migrate_price_history.sql

-- 1. Tabulka pro historii cen
CREATE TABLE IF NOT EXISTS re_realestate.listing_price_history (
    id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id    UUID        NOT NULL REFERENCES re_realestate.listings(id) ON DELETE CASCADE,
    price         NUMERIC,
    recorded_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    source        VARCHAR(50) NOT NULL DEFAULT 'scraper'  -- 'scraper' | 'manual' | 'import'
);

CREATE INDEX IF NOT EXISTS idx_price_history_listing_time
    ON re_realestate.listing_price_history(listing_id, recorded_at DESC);

-- 2. Rozšíření listings tabulky o SReality-specific pole
ALTER TABLE re_realestate.listings
    ADD COLUMN IF NOT EXISTS view_count           INTEGER,
    ADD COLUMN IF NOT EXISTS date_created_source  TIMESTAMPTZ;

-- 3. Backfill: ulož aktuální cenu do historie pro všechny inzeráty s cenou
--    (jednou, při první migraci)
INSERT INTO re_realestate.listing_price_history (listing_id, price, recorded_at, source)
SELECT id, price, first_seen_at, 'import'
FROM re_realestate.listings
WHERE price IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM re_realestate.listing_price_history h WHERE h.listing_id = listings.id
  );

SELECT COUNT(*) AS backfilled FROM re_realestate.listing_price_history WHERE source = 'import';
