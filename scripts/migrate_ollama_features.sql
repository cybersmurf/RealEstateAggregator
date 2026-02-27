-- =============================================================================
-- Migrate: Ollama text features
-- Session 17 – Smart Tags, Price Opinion, Description Normalization, Alt Text
-- =============================================================================

-- ── listing_photos: alt text voor accessibility (WCAG 2.2 AA) ────────────────
ALTER TABLE re_realestate.listing_photos
    ADD COLUMN IF NOT EXISTS alt_text text;

COMMENT ON COLUMN re_realestate.listing_photos.alt_text IS
    'Accessibility alt text pro fotku (generováno Ollama Vision, WCAG 2.2 AA)';

-- ── listings: smart tags (JSON array 5 tagů) ──────────────────────────────────
ALTER TABLE re_realestate.listings
    ADD COLUMN IF NOT EXISTS smart_tags   text,          -- JSON: ["sklep","zahrada","novostavba"]
    ADD COLUMN IF NOT EXISTS smart_tags_at timestamptz;

COMMENT ON COLUMN re_realestate.listings.smart_tags IS
    'JSON pole 5 klíčových tagů z popisu (generováno Ollama llama3.2)';

-- ── listings: AI normalizace popisu (strukturovaná extra data z textu) ─────────
ALTER TABLE re_realestate.listings
    ADD COLUMN IF NOT EXISTS ai_normalized_data jsonb,   -- {year_built, floor, has_elevator, ...}
    ADD COLUMN IF NOT EXISTS ai_normalized_at   timestamptz;

COMMENT ON COLUMN re_realestate.listings.ai_normalized_data IS
    'Strukturovaná data z popisu: rok stavby, patro, výtah, sklep, zahrada, ... (Ollama llama3.2)';

-- ── listings: cenový signál ────────────────────────────────────────────────────
ALTER TABLE re_realestate.listings
    ADD COLUMN IF NOT EXISTS price_signal        text CHECK (price_signal IN ('low', 'fair', 'high')),
    ADD COLUMN IF NOT EXISTS price_signal_reason text,
    ADD COLUMN IF NOT EXISTS price_signal_at     timestamptz;

COMMENT ON COLUMN re_realestate.listings.price_signal IS
    'Cenový signál: low=podhodnocená, fair=přiměřená, high=nadhodnocená (Ollama llama3.2)';

SELECT 'Migration migrate_ollama_features.sql OK' AS status;
