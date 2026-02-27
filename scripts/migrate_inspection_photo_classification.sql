-- Migration: Add AI classification columns to user_listing_photos
-- Run: docker exec -i realestate-db psql -U postgres -d realestate_dev < scripts/migrate_inspection_photo_classification.sql

ALTER TABLE re_realestate.user_listing_photos
    ADD COLUMN IF NOT EXISTS photo_category         text,
    ADD COLUMN IF NOT EXISTS photo_labels           text,
    ADD COLUMN IF NOT EXISTS damage_detected        boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS classification_confidence numeric(3,2),
    ADD COLUMN IF NOT EXISTS classified_at          timestamptz;

-- Index for "find unclassified inspection photos"
CREATE INDEX IF NOT EXISTS ix_user_listing_photos_classified_at
    ON re_realestate.user_listing_photos (classified_at)
    WHERE classified_at IS NULL;

-- Index for category filtering
CREATE INDEX IF NOT EXISTS ix_user_listing_photos_photo_category
    ON re_realestate.user_listing_photos (photo_category)
    WHERE photo_category IS NOT NULL;

\echo 'Migration user_listing_photos classification columns: OK'
