-- KONTEXT: audit scraperů 6. 11. 2026. Stejná URL fotky u 3+ různých aktivních inzerátů jednoho
-- zdroje není fotka domu, ale portrét makléře (Century 21, 2. místo galerie u 70 inzerátů), placeholder
-- portálu (iDNES no-image-gallery.png, map-marker.png) nebo náhled cizí nabídky (Premia thumbs_306_172).
-- Scraper je od teď neukládá (DatabaseManager._drop_shared_photos), tady se smažou staré řádky.
-- Hash index zrychlí kontrolu při každém uložení galerie (URL bývají dlouhé, btree by mohl narazit na limit).

CREATE INDEX IF NOT EXISTS ix_listing_photos_original_url_hash
    ON re_realestate.listing_photos USING hash (original_url);

DELETE FROM re_realestate.listing_photos p
USING (
    SELECT p2.original_url, l.source_id
    FROM re_realestate.listing_photos p2
    JOIN re_realestate.listings l ON l.id = p2.listing_id
    WHERE l.is_active
    GROUP BY p2.original_url, l.source_id
    HAVING count(DISTINCT l.id) >= 3
) shared, re_realestate.listings l
WHERE l.id = p.listing_id
  AND l.source_id = shared.source_id
  AND p.original_url = shared.original_url;
