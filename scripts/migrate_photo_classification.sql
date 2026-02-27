-- ============================================================================
-- Migrace: Klasifikace fotek pomocí Ollama Vision
-- Session 15 – 2026-02-27
-- Spustit: docker exec -i realestate-db psql -U postgres -d realestate_dev
-- ============================================================================

ALTER TABLE re_realestate.listing_photos
    ADD COLUMN IF NOT EXISTS photo_category       text,
    ADD COLUMN IF NOT EXISTS photo_description    text,        -- Popis fotky česky vygenerovaný Ollama Vision (1-2 věty)
    ADD COLUMN IF NOT EXISTS photo_labels         text,        -- JSON array: ["mold","water_damage"]
    ADD COLUMN IF NOT EXISTS damage_detected      boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS classification_confidence numeric(3,2),
    ADD COLUMN IF NOT EXISTS classified_at        timestamptz;

-- Index pro rychlé filtrování dle kategorie
CREATE INDEX IF NOT EXISTS idx_listing_photos_category
    ON re_realestate.listing_photos (photo_category)
    WHERE photo_category IS NOT NULL;

-- Index pro filtrování poškozených fotek
CREATE INDEX IF NOT EXISTS idx_listing_photos_damage
    ON re_realestate.listing_photos (listing_id)
    WHERE damage_detected = true;

-- Index pro batch classification (jen stažené, ještě neklasifikované)
CREATE INDEX IF NOT EXISTS idx_listing_photos_pending_classification
    ON re_realestate.listing_photos (listing_id, order_index)
    WHERE stored_url IS NOT NULL AND classified_at IS NULL;

COMMENT ON COLUMN re_realestate.listing_photos.photo_category IS 'Kategorie fotky: exterior|interior|kitchen|bathroom|living_room|bedroom|attic|basement|garage|land|floor_plan|damage|other';
COMMENT ON COLUMN re_realestate.listing_photos.photo_description IS 'Popis fotky česky vygenerovaný Ollama Vision (1-2 věty o stavu/materiálech/detailech)';
COMMENT ON COLUMN re_realestate.listing_photos.photo_labels IS 'JSON pole tagů: ["mold","water_damage","renovation_needed",...]';
COMMENT ON COLUMN re_realestate.listing_photos.damage_detected IS 'True pokud Ollama Vision detekovala viditelné poškození';
COMMENT ON COLUMN re_realestate.listing_photos.classification_confidence IS 'Confidence skóre klasifikace 0.00–1.00';
COMMENT ON COLUMN re_realestate.listing_photos.classified_at IS 'Čas kdy byla fotka klasifikována';
