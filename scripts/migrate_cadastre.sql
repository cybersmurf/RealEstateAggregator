-- ============================================================================
-- Migrace: Katastr nemovitostí (ČÚZK/RUIAN) integrace
-- Verze: 1.3 → 1.4 (cadastre)
-- Datum: 25. února 2026
-- Spuštění: psql -U postgres -d realestate_dev -f scripts/migrate_cadastre.sql
-- ============================================================================

-- 1. Tabulka listing_cadastre_data
CREATE TABLE IF NOT EXISTS re_realestate.listing_cadastre_data (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id          uuid NOT NULL REFERENCES re_realestate.listings(id) ON DELETE CASCADE,

    -- RUIAN identifikátory
    ruian_kod           bigint,                   -- Kód adresního místa z RUIAN
    parcel_number       text,                     -- Parcelní číslo (z RUIAN nebo manuálně)
    lv_number           text,                     -- Číslo listu vlastnictví (manuálně)

    -- Základní katastrální data
    land_area_m2        int,                      -- Výměra pozemku z katastru (ověřená)
    land_type           text,                     -- Druh pozemku (zastavěná plocha, zahrada, …)
    owner_type          text,                     -- Fyzická osoba / právnická osoba / stát

    -- Břemena (JSONB pro flexibilitu)
    encumbrances        jsonb DEFAULT '[]'::jsonb, -- Zástavní práva, věcná břemena, nájemní práva

    -- Metadata
    address_searched    text NOT NULL,            -- Adresa použitá pro vyhledávání
    cadastre_url        text,                     -- Přímý link na nahlížení.cuzk.cz
    fetch_status        text NOT NULL DEFAULT 'pending',  -- pending / found / not_found / error
    fetch_error         text,
    fetched_at          timestamptz DEFAULT now(),
    raw_ruian           jsonb,                    -- Surová odpověď z RUIAN API

    UNIQUE(listing_id)
);

COMMENT ON TABLE re_realestate.listing_cadastre_data IS 'Data z katastru nemovitostí (ČÚZK/RUIAN) pro inzeráty';
COMMENT ON COLUMN re_realestate.listing_cadastre_data.ruian_kod IS 'Kód adresního místa z RUIAN (https://nahlizenidokn.cuzk.cz/ZobrazitMapu/Basic?typeCode=adresniMisto&id={ruian_kod})';
COMMENT ON COLUMN re_realestate.listing_cadastre_data.encumbrances IS 'Pole objektů: [{type, description, who}]';

-- 2. Index pro rychlé vyhledávání
CREATE INDEX IF NOT EXISTS idx_listing_cadastre_data_listing_id
    ON re_realestate.listing_cadastre_data(listing_id);

CREATE INDEX IF NOT EXISTS idx_listing_cadastre_data_ruian_kod
    ON re_realestate.listing_cadastre_data(ruian_kod)
    WHERE ruian_kod IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_listing_cadastre_data_fetch_status
    ON re_realestate.listing_cadastre_data(fetch_status);

-- 3. Výsledek
SELECT
    'listing_cadastre_data' AS table_name,
    COUNT(*) AS row_count
FROM re_realestate.listing_cadastre_data;

DO $$ BEGIN RAISE NOTICE 'Katastralní tabulka listing_cadastre_data pripravena.'; END $$;
