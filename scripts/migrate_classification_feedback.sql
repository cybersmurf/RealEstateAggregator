-- Migration: Add classification_feedback to listing_photos
-- Session 16 – photo browser with AI classification feedback
-- Run: docker exec -i realestate-db psql -U postgres -d realestate_dev

ALTER TABLE re_realestate.listing_photos
    ADD COLUMN IF NOT EXISTS classification_feedback text
        CHECK (classification_feedback IN ('correct', 'wrong'));

COMMENT ON COLUMN re_realestate.listing_photos.classification_feedback
    IS 'User feedback on Ollama classification accuracy: correct | wrong | NULL (not yet rated)';
