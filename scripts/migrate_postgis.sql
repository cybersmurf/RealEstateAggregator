-- ============================================================================
-- Migrace: Přidání PostGIS spatial sloupců do existující databáze
-- Verze: 1.2 → 1.3 (PostGIS)
-- Datum: 25. února 2026
-- Spuštění: psql -U postgres -d realestate_dev -f scripts/migrate_postgis.sql
-- ============================================================================

-- 1. Aktivuj PostGIS extension
CREATE EXTENSION IF NOT EXISTS postgis;

-- 2. Přidej GPS sloupce do listings (idempotentní)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_schema = 're_realestate'
                     AND table_name = 'listings'
                     AND column_name = 'latitude') THEN
        ALTER TABLE re_realestate.listings
            ADD COLUMN latitude       double precision,
            ADD COLUMN longitude      double precision,
            ADD COLUMN location_point geometry(Point, 4326),
            ADD COLUMN geocoded_at    timestamptz,
            ADD COLUMN geocode_source text;
        RAISE NOTICE 'Sloupce latitude/longitude/location_point přidány do listings';
    ELSE
        RAISE NOTICE 'Sloupce latitude/longitude/location_point již existují – přeskakuji';
    END IF;
END $$;

-- 3. GIST index pro prostorové dotazy (idempotentní)
CREATE INDEX IF NOT EXISTS idx_listings_location_point
    ON re_realestate.listings
    USING GIST (location_point)
    WHERE location_point IS NOT NULL;

-- 4. Trigger: automatická synchronizace location_point ← lat/lng
CREATE OR REPLACE FUNCTION re_realestate.sync_location_point()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.latitude IS NOT NULL AND NEW.longitude IS NOT NULL THEN
        NEW.location_point := ST_SetSRID(ST_MakePoint(NEW.longitude, NEW.latitude), 4326);
    ELSE
        NEW.location_point := NULL;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_listings_sync_location ON re_realestate.listings;
CREATE TRIGGER trg_listings_sync_location
    BEFORE INSERT OR UPDATE OF latitude, longitude
    ON re_realestate.listings
    FOR EACH ROW EXECUTE FUNCTION re_realestate.sync_location_point();

-- 5. Tabulka spatial_areas (idempotentní)
CREATE TABLE IF NOT EXISTS re_realestate.spatial_areas (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name        text NOT NULL,
    description text,
    area_type   text NOT NULL DEFAULT 'corridor',
    geom        geometry(Geometry, 4326) NOT NULL,
    start_city  text,
    end_city    text,
    buffer_m    integer,
    is_active   boolean NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_spatial_areas_geom
    ON re_realestate.spatial_areas USING GIST (geom);

CREATE INDEX IF NOT EXISTS idx_spatial_areas_is_active
    ON re_realestate.spatial_areas (is_active);

-- Trigger updated_at pro spatial_areas
-- Funkce musí existovat před triggerem (v existující DB ji mohlo přidat až init-db.sql)
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS update_spatial_areas_updated_at ON re_realestate.spatial_areas;
CREATE TRIGGER update_spatial_areas_updated_at
    BEFORE UPDATE ON re_realestate.spatial_areas
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- 6. Backfill location_point z existujících SReality inzerátů (pokud by lat/lng bylo v jiném sloupci)
--    (Prozatím prázdný – přidáno až po naplnění lat/lng scrapery)

-- 7. Analýza tabulky
VACUUM ANALYZE re_realestate.listings;
VACUUM ANALYZE re_realestate.spatial_areas;

SELECT 'Migrace PostGIS dokončena!' AS status,
       COUNT(*) FILTER (WHERE location_point IS NOT NULL) AS listings_with_coords,
       COUNT(*) AS total_listings
FROM re_realestate.listings;
