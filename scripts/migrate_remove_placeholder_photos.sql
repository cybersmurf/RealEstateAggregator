-- ============================================================================
-- Úklid zástupných obrázků portálů uložených jako fotky nemovitosti
-- ============================================================================
-- Spuštění:
--   docker cp scripts/migrate_remove_placeholder_photos.sql realestate-db:/tmp/ && \
--   docker exec realestate-db psql -U postgres -d realestate_dev -f /tmp/migrate_remove_placeholder_photos.sql
--
-- KONTEXT
-- Inzerát bez fotek má na RealityMIX v og:image obecný obrázek portálu
-- (//realitymix.cz/build/images/rmix_og-image_…jpg) a na Reality Čechy ve výpisu
-- zástupný náhled (/www/images/default_foto.jpg). Scrapery je ukládaly jako jedinou
-- „fotku“ – inzerát pak vypadal, že fotku má (Trstěnice 4+1, dražba Šumná č. p. 9).
--
-- Scrapery už zástupný obrázek neukládají, ale upsert fotky při prázdném seznamu
-- nemaže, takže co v DB je, uklidí až tenhle skript.
-- ============================================================================

BEGIN;

\echo '── Zástupné obrázky podle zdroje ──'
SELECT s.code, count(*) AS zastupnych
FROM re_realestate.listing_photos p
JOIN re_realestate.listings l ON l.id = p.listing_id
JOIN re_realestate.sources s ON s.id = l.source_id
WHERE p.original_url LIKE '%/build/images/rmix_og-image%'
   OR p.original_url LIKE '%/www/images/default_foto%'
GROUP BY s.code;

DELETE FROM re_realestate.listing_photos
WHERE original_url LIKE '%/build/images/rmix_og-image%'
   OR original_url LIKE '%/www/images/default_foto%';

COMMIT;
