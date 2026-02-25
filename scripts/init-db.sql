-- ============================================================================
-- Real Estate Aggregator - PostgreSQL Database Schema
-- ============================================================================
-- Version: 1.1
-- Date: 22. února 2026
-- PostgreSQL: 15+
-- Extensions: pgvector
-- Schema: re_realestate
-- ============================================================================

-- ============================================================================
-- SCHEMA & EXTENSIONS
-- ============================================================================

CREATE SCHEMA IF NOT EXISTS re_realestate;

SET search_path TO re_realestate, public;

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS vector;   -- pgvector pro semantic search
CREATE EXTENSION IF NOT EXISTS postgis;  -- PostGIS pro prostorové dotazy (ST_Buffer, ST_Intersects atd.)

-- ============================================================================
-- TABLES
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Sources (Realitní kanceláře)
-- ----------------------------------------------------------------------------
CREATE TABLE re_realestate.sources (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code text NOT NULL UNIQUE,              -- "REMAX", "MMR", "PRODEJMETO"
    name text NOT NULL,                     -- "RE/MAX Czech Republic"
    base_url text NOT NULL,                 -- "https://www.remax-czech.cz"
    is_active boolean NOT NULL DEFAULT true,
    
    supports_url_scrape boolean NOT NULL DEFAULT true,   -- Scraping by URL
    supports_list_scrape boolean NOT NULL DEFAULT true,  -- Scraping list pages
    scraper_type text NOT NULL DEFAULT 'Python',         -- "Python", "PlaywrightNet"
    
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_sources_is_active ON re_realestate.sources (is_active);
CREATE INDEX idx_sources_code ON re_realestate.sources (code);

COMMENT ON TABLE re_realestate.sources IS 'Realitní kanceláře a jejich konfigurace';
COMMENT ON COLUMN re_realestate.sources.scraper_type IS 'Typ scraperu (Python/PlaywrightNet)';

-- ----------------------------------------------------------------------------
-- Listings (Realitní inzeráty) - Main entity + pgvector
-- ----------------------------------------------------------------------------
CREATE TABLE re_realestate.listings (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    source_id uuid NOT NULL,
    source_code text NOT NULL,              -- "REMAX", "MMR", "PRODEJMETO"...
    source_name text NOT NULL,              -- Lidský název pro zobrazení
    
    external_id text,                       -- ID z webu RK, pokud existuje
    url text NOT NULL,                      -- URL detailu inzerátu
    
    title text NOT NULL,
    description text NOT NULL,
    location_text text NOT NULL,            -- Jak je v inzerátu
    region text,                            -- Jihomoravský kraj...
    district text,                          -- Znojmo...
    municipality text,                      -- Znojmo...
    
    property_type text NOT NULL,            -- House, Apartment, Cottage, Land, Commercial
    offer_type text NOT NULL,               -- Sale, Rent
    
    price numeric(15,2),
    price_note text,
    
    area_built_up double precision,         -- m²
    area_land double precision,             -- m²
    rooms integer,
    disposition text,                       -- "1+1", "1+kk", "2+1", "4+1", "4+kk", atd.
    has_kitchen boolean,
    construction_type text,                 -- Brick, Panel, Wood, Stone, etc.
    condition text,                         -- New, VeryGood, Good, ToReconstruct, Demolished
    
    created_at_source timestamptz,
    updated_at_source timestamptz,
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    is_active boolean NOT NULL DEFAULT true,
    
    -- � GPS souřadnice (vyplní scraper nebo geocoder)
    latitude  double precision,             -- WGS84 zeměpisná šířka
    longitude double precision,             -- WGS84 zeměpisná délka
    location_point geometry(Point, 4326),   -- Computed z lat/lng pomocí triggeru
    geocoded_at timestamptz,                -- Kdy bylo geokódováno
    geocode_source text,                    -- 'scraper' | 'nominatim' | 'manual'
    
    -- �🔥 PGVECTOR: Embedding popisu pro semantické vyhledávání
    description_embedding vector(1536),     -- OpenAI text-embedding-3-small
    
    CONSTRAINT listings_url_unique UNIQUE (url),
    CONSTRAINT listings_source_external_id_unique UNIQUE (source_id, external_id)
);

COMMENT ON TABLE re_realestate.listings IS 'Realitní inzeráty ze všech zdrojů';
COMMENT ON COLUMN re_realestate.listings.description_embedding IS 'OpenAI embedding (1536 dim) pro semantic search';
COMMENT ON COLUMN re_realestate.listings.external_id IS 'ID inzerátu v systému RK';

-- Indexy pro klasické filtrování
CREATE INDEX idx_listings_active_region_price
    ON re_realestate.listings (is_active, region, price)
    WHERE is_active = true;

CREATE INDEX idx_listings_active_municipality_price
    ON re_realestate.listings (is_active, municipality, price)
    WHERE is_active = true;

CREATE INDEX idx_listings_first_seen_at
    ON re_realestate.listings (first_seen_at DESC);

CREATE INDEX idx_listings_property_offer
    ON re_realestate.listings (property_type, offer_type)
    WHERE is_active = true;

CREATE INDEX idx_listings_source_code
    ON re_realestate.listings (source_code);

CREATE INDEX idx_listings_is_active
    ON re_realestate.listings (is_active)
    WHERE is_active = true;

-- 🔥 PGVECTOR: HNSW index pro L2 distance similarity search
CREATE INDEX idx_listings_description_embedding_hnsw
    ON re_realestate.listings
    USING hnsw (description_embedding vector_l2_ops)
    WITH (m = 16, ef_construction = 64);

-- 📍 PostGIS: GIST index pro prostorové dotazy (ST_Intersects, ST_DWithin)
CREATE INDEX idx_listings_location_point
    ON re_realestate.listings
    USING GIST (location_point)
    WHERE location_point IS NOT NULL;

-- Trigger: automaticky udržuje location_point synchronizovaný s lat/lng
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

CREATE TRIGGER trg_listings_sync_location
    BEFORE INSERT OR UPDATE OF latitude, longitude
    ON re_realestate.listings
    FOR EACH ROW EXECUTE FUNCTION re_realestate.sync_location_point();

-- Full-text search index (optional, but recommended)
-- Generovaný sloupec pro kombinovaný fulltext search
ALTER TABLE re_realestate.listings
    ADD COLUMN search_tsv tsvector GENERATED ALWAYS AS (
        setweight(to_tsvector('simple', coalesce(title, '')), 'A') ||
        setweight(to_tsvector('simple', coalesce(location_text, '')), 'B') ||
        setweight(to_tsvector('simple', coalesce(description, '')), 'C')
    ) STORED;

CREATE INDEX idx_listings_search_tsv
    ON re_realestate.listings
    USING gin (search_tsv);

-- ----------------------------------------------------------------------------
-- Listing Photos (Fotografie inzerátů)
-- ----------------------------------------------------------------------------
CREATE TABLE re_realestate.listing_photos (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id uuid NOT NULL REFERENCES re_realestate.listings (id) ON DELETE CASCADE,
    original_url text NOT NULL,             -- URL z webu RK
    stored_url text,                        -- URL v našem storage (S3/Drive), pokud kopírujeme
    order_index integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_listing_photos_listing_id
    ON re_realestate.listing_photos (listing_id, order_index);

COMMENT ON TABLE re_realestate.listing_photos IS 'Fotografie realitních inzerátů';
COMMENT ON COLUMN re_realestate.listing_photos.original_url IS 'Původní URL fotky z webu RK';
COMMENT ON COLUMN re_realestate.listing_photos.stored_url IS 'URL v našem storage, pokud ukládáme kopii';

-- ----------------------------------------------------------------------------
-- User Listing States (Uživatelské stavy inzerátů)
-- ----------------------------------------------------------------------------
CREATE TABLE re_realestate.user_listing_state (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,                  -- Pro MVP můžeš použít 'default user' UUID
    listing_id uuid NOT NULL REFERENCES re_realestate.listings (id) ON DELETE CASCADE,
    
    status text NOT NULL,                   -- "New", "Liked", "Disliked", "ToVisit", "Visited"
    notes text,
    last_updated timestamptz NOT NULL DEFAULT now(),
    
    CONSTRAINT user_listing_state_unique UNIQUE (user_id, listing_id)
);

CREATE INDEX idx_user_listing_state_user_status
    ON re_realestate.user_listing_state (user_id, status);

CREATE INDEX idx_user_listing_state_listing
    ON re_realestate.user_listing_state (listing_id);

COMMENT ON TABLE re_realestate.user_listing_state IS 'Uživatelské stavy a poznámky k inzerátům';
COMMENT ON COLUMN re_realestate.user_listing_state.status IS 'Stav z pohledu uživatele (New, Liked, Disliked, ToVisit, Visited)';

-- ----------------------------------------------------------------------------
-- User Listing Photos (fotky pořízené uživatelem při prohlídce nemovitosti)
-- ----------------------------------------------------------------------------
CREATE TABLE re_realestate.user_listing_photos (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id uuid NOT NULL REFERENCES re_realestate.listings (id) ON DELETE CASCADE,
    stored_url text NOT NULL,
    original_file_name text NOT NULL,
    file_size_bytes bigint,
    taken_at timestamptz,
    uploaded_at timestamptz NOT NULL DEFAULT now(),
    notes text
);

CREATE INDEX idx_user_listing_photos_listing
    ON re_realestate.user_listing_photos (listing_id);

CREATE INDEX idx_user_listing_photos_uploaded_at
    ON re_realestate.user_listing_photos (uploaded_at DESC);

COMMENT ON TABLE re_realestate.user_listing_photos IS 'Fotky pořízené uživatelem při prohlídce nemovitosti';

-- ----------------------------------------------------------------------------
-- Analysis Jobs (Export pro AI analýzu)
-- ----------------------------------------------------------------------------
CREATE TABLE re_realestate.analysis_jobs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id uuid NOT NULL REFERENCES re_realestate.listings (id) ON DELETE CASCADE,
    user_id uuid NOT NULL,
    
    status text NOT NULL DEFAULT 'Pending',  -- "Pending", "Running", "Succeeded", "Failed"
    storage_provider text NOT NULL,          -- "GoogleDrive", "OneDrive", "Local"
    storage_url text,                        -- URL na dokument / složku (share link)
    storage_path text,                       -- Technická cesta
    
    requested_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz,
    error_message text
);

CREATE INDEX idx_analysis_jobs_listing
    ON re_realestate.analysis_jobs (listing_id);

CREATE INDEX idx_analysis_jobs_user
    ON re_realestate.analysis_jobs (user_id);

CREATE INDEX idx_analysis_jobs_status_requested_at
    ON re_realestate.analysis_jobs (status, requested_at DESC);

COMMENT ON TABLE re_realestate.analysis_jobs IS 'Joby pro export inzerátů do cloudu pro AI analýzu';
COMMENT ON COLUMN re_realestate.analysis_jobs.storage_provider IS 'Poskytovatel cloud storage (GoogleDrive/OneDrive/Local)';

-- ----------------------------------------------------------------------------
-- Scrape Runs (Monitoring běhů scraperu)
-- ----------------------------------------------------------------------------
CREATE TABLE re_realestate.scrape_runs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    source_id uuid NOT NULL,
    source_code text NOT NULL,              -- Denormalizovaný pro rychlé queries
    started_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz,
    status text NOT NULL DEFAULT 'Running', -- "Running", "Succeeded", "Failed"
    
    total_seen integer NOT NULL DEFAULT 0,
    total_new integer NOT NULL DEFAULT 0,
    total_updated integer NOT NULL DEFAULT 0,
    total_inactivated integer NOT NULL DEFAULT 0, -- Kolik bylo označeno jako neaktivní
    error_message text
);

CREATE INDEX idx_scrape_runs_source_started_at
    ON re_realestate.scrape_runs (source_code, started_at DESC);

CREATE INDEX idx_scrape_runs_status
    ON re_realestate.scrape_runs (status);

COMMENT ON TABLE re_realestate.scrape_runs IS 'Historie běhů scraperu pro monitoring';
COMMENT ON COLUMN re_realestate.scrape_runs.total_inactivated IS 'Počet inzerátů označených jako neaktivní (zmizely z webu)';

-- ----------------------------------------------------------------------------
-- Scrape Jobs (Tracking jednotlivých async scrapingových jobů)
-- ----------------------------------------------------------------------------
CREATE TABLE re_realestate.scrape_jobs (
    id uuid PRIMARY KEY,                    -- Job ID (UUID)
    source_codes text[] NOT NULL,           -- Array: ["REMAX", "MMR", "SREALITY"]
    full_rescan boolean NOT NULL DEFAULT false,
    status text NOT NULL DEFAULT 'Queued',  -- "Queued", "Running", "Succeeded", "Failed"
    progress integer NOT NULL DEFAULT 0,    -- Procent (0-100)
    
    listings_found integer NOT NULL DEFAULT 0,
    listings_new integer NOT NULL DEFAULT 0,
    listings_updated integer NOT NULL DEFAULT 0,
    
    error_message text,
    
    created_at timestamptz NOT NULL DEFAULT now(),
    started_at timestamptz,
    finished_at timestamptz
);

CREATE INDEX idx_scrape_jobs_status_created_at
    ON re_realestate.scrape_jobs (status, created_at DESC);

CREATE INDEX idx_scrape_jobs_created_at
    ON re_realestate.scrape_jobs (created_at DESC);

COMMENT ON TABLE re_realestate.scrape_jobs IS 'Async scraping joby - tracking single run iniciování desde API';
COMMENT ON COLUMN re_realestate.scrape_jobs.source_codes IS 'Array zdrojů pour scrapeovat (REMAX, MMR, SREALITY, atd.)';
COMMENT ON COLUMN re_realestate.scrape_jobs.full_rescan IS 'true = full rescan všeho, false = pouze oposledy změné';

-- ----------------------------------------------------------------------------
-- Spatial Areas (Pojmenované prostorové oblasti pro filtrování)
-- Ukládají koridory, obdélníky, polygony pojmenované uživatelem
-- ----------------------------------------------------------------------------
CREATE TABLE re_realestate.spatial_areas (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name        text NOT NULL,                  -- "Brno–Znojmo trasa 5km"
    description text,
    area_type   text NOT NULL DEFAULT 'corridor', -- 'corridor' | 'bbox' | 'polygon' | 'circle'
    
    -- Geometrie v WGS84 (EPSG:4326)
    geom        geometry(Geometry, 4326) NOT NULL,
    
    -- Metadata koridoru (pro corridor type)
    start_city  text,   -- "Brno"
    end_city    text,   -- "Znojmo"
    buffer_m    integer, -- Buffer v metrech (default 5000 = 5km)
    
    is_active   boolean NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_spatial_areas_geom
    ON re_realestate.spatial_areas
    USING GIST (geom);

CREATE INDEX idx_spatial_areas_is_active
    ON re_realestate.spatial_areas (is_active);

COMMENT ON TABLE re_realestate.spatial_areas IS 'Uložené prostorové oblasti pro filtrování inzerátů (koridory, polygony)';

-- ============================================================================
-- FUNCTIONS & TRIGGERS
-- ============================================================================

-- Automatically update updated_at timestamp
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Apply trigger to sources
CREATE TRIGGER update_sources_updated_at
    BEFORE UPDATE ON re_realestate.sources
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

-- Apply trigger to spatial_areas
CREATE TRIGGER update_spatial_areas_updated_at
    BEFORE UPDATE ON re_realestate.spatial_areas
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

-- ============================================================================
-- SEED DATA
-- ============================================================================

-- Insert default sources
INSERT INTO re_realestate.sources (code, name, base_url, is_active, supports_url_scrape, supports_list_scrape, scraper_type)
VALUES 
    ('REMAX', 'RE/MAX Czech Republic', 'https://www.remax-czech.cz', true, true, true, 'Python'),
    ('MMR', 'M&M Reality', 'https://www.mmreality.cz', true, true, true, 'Python'),
    ('PRODEJMETO', 'Prodejme.to', 'https://www.prodejme.to', true, true, true, 'Python'),
    ('ZNOJMOREALITY', 'Znojmo Reality', 'https://www.znojmoreality.cz', true, true, true, 'Python'),
    ('SREALITY', 'Sreality', 'https://www.sreality.cz', true, true, true, 'Python'),
    ('NEMZNOJMO', 'Nemovitosti Znojmo', 'https://www.nemovitostiznojmo.cz', true, true, true, 'Python'),
    ('HVREALITY', 'Horák & Vetchý reality', 'https://hvreality.cz', true, true, true, 'Python'),
    ('PREMIAREALITY', 'PREMIA Reality s.r.o.', 'https://www.premiareality.cz', true, true, true, 'Python'),
    ('DELUXREALITY', 'DeluXreality Znojmo', 'https://deluxreality.cz', true, true, true, 'Python'),
    ('LEXAMO', 'Lexamo Reality', 'https://www.lexamo.cz', true, true, true, 'Python'),
    ('CENTURY21', 'CENTURY 21 Czech Republic', 'https://www.century21.cz', true, true, true, 'Python'),
    ('IDNES', 'iDnes Reality', 'https://reality.idnes.cz', true, true, true, 'Python'),
    ('REAS', 'Reas.cz', 'https://www.reas.cz', true, false, true, 'Python')
ON CONFLICT (code) DO NOTHING;

-- ============================================================================
-- VIEWS (Optional - pro reporting a debugging)
-- ============================================================================

-- Active listings s počtem fotek
CREATE OR REPLACE VIEW re_realestate.v_listings_with_photo_count AS
SELECT 
    l.*,
    COUNT(p.id) as photo_count,
    MIN(p.original_url) FILTER (WHERE p.order_index = 0) as main_photo_url
FROM re_realestate.listings l
LEFT JOIN re_realestate.listing_photos p ON l.id = p.listing_id
WHERE l.is_active = true
GROUP BY l.id;

-- Statistika scraperů
CREATE OR REPLACE VIEW re_realestate.v_scrape_run_stats AS
SELECT 
    s.name as source_name,
    sr.source_code,
    COUNT(*) as total_runs,
    COUNT(*) FILTER (WHERE sr.status = 'Succeeded') as successful_runs,
    COUNT(*) FILTER (WHERE sr.status = 'Failed') as failed_runs,
    SUM(sr.total_new) as total_new,
    SUM(sr.total_updated) as total_updated,
    SUM(sr.total_inactivated) as total_inactivated,
    MAX(sr.started_at) as last_run_at,
    AVG(EXTRACT(EPOCH FROM (sr.finished_at - sr.started_at))) as avg_duration_seconds
FROM re_realestate.scrape_runs sr
LEFT JOIN re_realestate.sources s ON sr.source_id = s.id
GROUP BY s.name, sr.source_code;

-- ============================================================================
-- EXAMPLE QUERIES
-- ============================================================================

/*
-- 1. Klasický search: aktivní domy ve Znojmě, cena, plocha
SELECT *
FROM re_realestate.listings l
LEFT JOIN re_realestate.user_listing_state s
  ON s.listing_id = l.id AND s.user_id = :user_id
WHERE l.is_active = true
  AND l.region = 'Jihomoravský kraj'
  AND l.district = 'Znojmo'
  AND l.price BETWEEN 5000000 AND 7500000
  AND l.area_land >= 600
ORDER BY l.first_seen_at DESC
LIMIT 50 OFFSET 0;

-- 2. Fulltext search
SELECT *
FROM re_realestate.listings
WHERE is_active = true
  AND search_tsv @@ plainto_tsquery('simple', 'studna plyn rekonstrukce')
ORDER BY first_seen_at DESC
LIMIT 50;

-- 3. Semantic search (pgvector)
-- Query embedding jako parametr :query_embedding (array float[1536])
SELECT id, title, location_text, price, area_land,
       description_embedding <-> :query_embedding as distance
FROM re_realestate.listings
WHERE is_active = true
  AND description_embedding IS NOT NULL
ORDER BY description_embedding <-> :query_embedding
LIMIT 20;

-- 4. Hybrid: Filtry + Semantic search
SELECT id, title, location_text, price, area_land,
       description_embedding <-> :query_embedding as distance
FROM re_realestate.listings
WHERE is_active = true
  AND region = 'Jihomoravský kraj'
  AND price BETWEEN 2000000 AND 5000000
  AND description_embedding IS NOT NULL
ORDER BY description_embedding <-> :query_embedding
LIMIT 20;

-- 5. Statistika nových inzerátů za týden
SELECT 
    source_code,
    COUNT(*) as new_listings
FROM re_realestate.listings
WHERE first_seen_at >= now() - interval '7 days'
GROUP BY source_code
ORDER BY new_listings DESC;

-- 6. Listings bez embeddingů (pro background job)
SELECT id, title, description
FROM re_realestate.listings
WHERE is_active = true
  AND description_embedding IS NULL
LIMIT 100;
*/

-- ============================================================================
-- MAINTENANCE NOTES
-- ============================================================================

/*
Performance tips:
1. pgvector HNSW parametry:
   - m: počet connections per layer (8-32, default 16)
   - ef_construction: quality při build (32-128, default 64)
   
2. Pro fastest search s trade-off přesnosti:
   CREATE INDEX ... WITH (m = 8, ef_construction = 32);
   
3. Pro best accuracy na produkci:
   CREATE INDEX ... WITH (m = 32, ef_construction = 128);

4. Semantic search query optimization:
   - Filtruj nejdřív klasickými WHERE klauzulemi (region, price)
   - Pak teprve semantic search na podmnožině (AND description_embedding IS NOT NULL)
   
5. Embedding batch update:
   - Generuj embeddy v dávkách přes background job
   - Rate limit OpenAI API calls (3,500 RPM na tier 1)

6. Reindex pgvector po hromadném insertu:
   REINDEX INDEX CONCURRENTLY idx_listings_description_embedding_hnsw;

7. Vacuum analyze pro optimalizaci:
   VACUUM ANALYZE re_realestate.listings;
   VACUUM ANALYZE re_realestate.listing_photos;

8. Pro large datasets (>1M listings) zvažit:
   - Partitioning by region nebo year
   - Separate read replicas
   - Connection pooling (PgBouncer)
*/
