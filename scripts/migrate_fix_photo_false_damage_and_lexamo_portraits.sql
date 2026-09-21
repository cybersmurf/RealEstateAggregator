-- ============================================================================
-- Falešné damage_detected u fotek + portréty makléřů LEXAMO mezi fotkami
-- ============================================================================
-- Spuštění:
--   docker cp scripts/migrate_fix_photo_false_damage_and_lexamo_portraits.sql realestate-db:/tmp/ && \
--   docker exec realestate-db psql -U postgres -d realestate_dev -f /tmp/migrate_fix_photo_false_damage_and_lexamo_portraits.sql
--
-- KONTEXT
-- 1. Vision model nastavoval damage_detected=true i tam, kde to ničím nedoložil
--    (štítky jen renovation_needed/brick_walls/wooden_beams, popis „good condition,
--    no visible defects"). Nově to hlídá PhotoDamageValidator; tady je stejné pravidlo
--    pro už uložené řádky: příznak zůstane jen s kategorií damage, štítkem poškození,
--    nebo když poškození zmiňuje (a nepopírá) popis.
-- 2. LEXAMO scraper bral portrét makléře (div.makler-photo-wrapper) jako fotku
--    nemovitosti. Fotky galerie mají v názvu "_<š>x<v>wm" (vodoznak), portréty ne.
--    Scraper je opravený; tady se smažou řádky, které už v DB jsou (22 ks, ověřeno ručně).
--
-- Původní řádky jdou do listing_photos_damage_portrait_backup. Soubory na disku se nemažou.
-- ============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS re_realestate.listing_photos_damage_portrait_backup AS
SELECT *, now() AS backed_up_at, ''::text AS reason FROM re_realestate.listing_photos WITH NO DATA;

-- ── 1. Falešné damage_detected ──────────────────────────────────────────────
CREATE TEMP TABLE false_damage ON COMMIT DROP AS
SELECT id
FROM re_realestate.listing_photos
WHERE damage_detected
  AND photo_category IS DISTINCT FROM 'damage'
  AND coalesce(photo_labels, '') !~ '"(mold|water_damage|crack|broken_windows|damaged_roof)"'
  AND regexp_replace(coalesce(photo_description, ai_description, ''),
                     '\m(no|without|free of)\M[^.;]*', ' ', 'gi')
      !~* '\m(crack\w*|mou?ld\w*|peel\w*|stain\w*|damp\w*|rot(ten|ting)?|damage\w*|dilapidated|crumbl\w*|deteriorat\w*|broken|leak\w*|rust\w*|disrepair|poor condition)\M';

INSERT INTO re_realestate.listing_photos_damage_portrait_backup
SELECT p.*, now(), 'false_damage'
FROM re_realestate.listing_photos p JOIN false_damage f USING (id);

UPDATE re_realestate.listing_photos p
SET damage_detected = false
FROM false_damage f
WHERE p.id = f.id;

\echo '── damage_detected: zrušeno / zůstává ──'
SELECT (SELECT count(*) FROM false_damage) AS zruseno,
       count(*) FILTER (WHERE damage_detected) AS zustava
FROM re_realestate.listing_photos;

-- ── 2. Portréty makléřů LEXAMO ──────────────────────────────────────────────
CREATE TEMP TABLE lexamo_portraits ON COMMIT DROP AS
SELECT p.id
FROM re_realestate.listing_photos p
JOIN re_realestate.listings l ON l.id = p.listing_id
WHERE l.source_code = 'LEXAMO'
  AND p.original_url !~ '_[0-9]+x[0-9]+wm';

INSERT INTO re_realestate.listing_photos_damage_portrait_backup
SELECT p.*, now(), 'lexamo_portrait'
FROM re_realestate.listing_photos p JOIN lexamo_portraits x USING (id);

DELETE FROM re_realestate.listing_photos p
USING lexamo_portraits x
WHERE p.id = x.id;

\echo '── LEXAMO portréty: smazáno ──'
SELECT count(*) AS smazano FROM lexamo_portraits;

COMMIT;
