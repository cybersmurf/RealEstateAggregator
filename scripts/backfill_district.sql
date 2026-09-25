-- Backfill okresu u inzerátů bez district (Bazoš, iDnes, malé realitky posílají jen "PSČ Město").
-- Run: docker exec -i realestate-db psql -U postgres -d realestate_dev < scripts/backfill_district.sql
-- 1) podle obce – převezmi okres, který má stejná obec u jiných inzerátů (nejčastější hodnota)
UPDATE re_realestate.listings l
SET district = m.district
FROM (
    SELECT municipality, mode() WITHIN GROUP (ORDER BY district) AS district
    FROM re_realestate.listings
    WHERE district IS NOT NULL AND municipality IS NOT NULL
    GROUP BY municipality
) m
WHERE l.district IS NULL AND l.municipality = m.municipality;

-- 2) podle PSČ v location_text
UPDATE re_realestate.listings SET district = 'Znojmo'
WHERE district IS NULL AND location_text ~ '(^|\D)(669|671) ?\d{2}(\D|$)';
UPDATE re_realestate.listings SET district = 'Brno-venkov'
WHERE district IS NULL AND location_text ~ '(^|\D)(664|665|667) ?\d{2}(\D|$)';
UPDATE re_realestate.listings SET district = 'Brno-město'
WHERE district IS NULL AND location_text ~ '(^|\D)6(0[023]|1[2-9]|2[013-8]|3[4-9]|4[1-4]) ?\d{2}(\D|$)';

-- 3) podle klíčového slova
UPDATE re_realestate.listings SET district = 'Znojmo'
WHERE district IS NULL AND (location_text ILIKE '%znojm%' OR municipality ILIKE '%znojmo%');
UPDATE re_realestate.listings SET district = 'Brno-venkov'
WHERE district IS NULL AND location_text ILIKE '%brno-venkov%';

SELECT count(*) FILTER (WHERE district IS NULL) AS bez_okresu, count(*) AS aktivnich
FROM re_realestate.listings WHERE is_active;
