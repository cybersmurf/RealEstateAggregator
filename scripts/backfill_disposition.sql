-- ============================================================
-- Krok A1: Backfill disposition + rooms z title / description
-- Spuštění: docker exec -i realestate-db psql -U postgres -d realestate_dev < scripts/backfill_disposition.sql
-- ============================================================

WITH extracted AS (
    SELECT
        id,
        COALESCE(
            (regexp_match(title,       '(\d+\+(?:\d+|kk))', 'i'))[1],
            (regexp_match(description, '(\d+\+(?:\d+|kk))', 'i'))[1]
        ) AS disp_raw
    FROM re_realestate.listings
    WHERE is_active = true
      AND disposition IS NULL
),
normalized AS (
    SELECT
        id,
        UPPER(TRIM(disp_raw)) AS disp
    FROM extracted
    WHERE disp_raw IS NOT NULL
)
UPDATE re_realestate.listings l
SET
    disposition = n.disp,
    rooms = (regexp_match(n.disp, '^(\d+)'))[1]::int
FROM normalized n
WHERE l.id = n.id;

-- Výsledek
SELECT
    COUNT(*) FILTER (WHERE disposition IS NOT NULL) AS disposition_filled,
    COUNT(*) FILTER (WHERE rooms IS NOT NULL)       AS rooms_filled,
    COUNT(*)                                        AS total_active
FROM re_realestate.listings
WHERE is_active = true;
