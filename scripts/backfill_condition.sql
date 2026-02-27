-- ============================================================
-- Krok A2: Backfill condition + construction_type z description / title
-- Spuštění: docker cp scripts/backfill_condition.sql realestate-db:/tmp/ && docker exec realestate-db psql -U postgres -d realestate_dev -f /tmp/backfill_condition.sql
-- ============================================================

-- ── 1. condition ─────────────────────────────────────────────────────────────
UPDATE re_realestate.listings
SET condition = CASE
    WHEN description ~* 'novostavb|ve výstavb|pod klíč|developerský projekt'
        THEN 'Novostavba'
    WHEN description ~* 'po kompletní rekonstrukci|po celkové rekonstrukci|po rekonstrukci|kompletně zrekon'
        THEN 'Po rekonstrukci'
    WHEN description ~* 'před rekonstrukcí|k rekonstrukci|vyžaduje rekonstrukci|potřebuje rekonstrukci'
        THEN 'Před rekonstrukcí'
    WHEN description ~* 'k demolici|velmi špatný stav|havarijní'
        THEN 'K demolici'
    WHEN description ~* 'zachovalý stav|dobrý stav|udržovaný stav|v dobrém stavu|v zachovalém'
        THEN 'Dobrý stav'
    WHEN title ~* 'novostavb|ve výstavb|pod klíč'
        THEN 'Novostavba'
    ELSE NULL
END
WHERE is_active = true
  AND condition IS NULL
  AND (
      description ~* 'novostavb|ve výstavb|pod klíč|developerský projekt|po rekonstrukci|kompletně zrekon|před rekonstrukcí|k rekonstrukci|vyžaduje rekonstrukci|k demolici|havarijní|zachovalý stav|dobrý stav|udržovaný stav'
   OR title ~* 'novostavb|ve výstavb|pod klíč'
  );

-- ── 2. construction_type ─────────────────────────────────────────────────────
UPDATE re_realestate.listings
SET construction_type = CASE
    WHEN description ~* 'cihlová|cihlový|ciheln|cihla|z cihel|zděná vila|zděný dům|zděná budova'
      OR title ~* 'cihl|zděn'
        THEN 'Cihla'
    WHEN description ~* 'panel[oá]|panelový dům|panelová budova'
      OR title ~* 'panel'
        THEN 'Panel'
    WHEN description ~* 'dřevěn|srubov|rouben|ze dřeva'
        THEN 'Dřevo'
    WHEN description ~* 'montovan|prefabrikát|skelet'
        THEN 'Montovaná'
    WHEN description ~* '\bzděn[aáéý]\b' AND NOT description ~* 'cihlová|cihlový|ciheln'
        THEN 'Zděná'
    ELSE NULL
END
WHERE is_active = true
  AND construction_type IS NULL
  AND (
      description ~* 'cihlová|cihlový|ciheln|cihla|z cihel|panel[oá]|dřevěn|srubov|rouben|montovan|prefabrikát|skelet|\bzděn[aáéý]\b'
   OR title ~* 'cihl|zděn|panel'
  );

-- ── Výsledek ─────────────────────────────────────────────────────────────────
SELECT
    COUNT(*) FILTER (WHERE disposition IS NOT NULL)       AS disposition,
    COUNT(*) FILTER (WHERE rooms IS NOT NULL)             AS rooms,
    COUNT(*) FILTER (WHERE condition IS NOT NULL)         AS condition,
    COUNT(*) FILTER (WHERE construction_type IS NOT NULL) AS construction_type,
    COUNT(*)                                              AS total_active
FROM re_realestate.listings
WHERE is_active = true;

-- Distribuce condition
SELECT condition, COUNT(*) FROM re_realestate.listings
WHERE condition IS NOT NULL AND is_active=true
GROUP BY condition ORDER BY COUNT(*) DESC;

-- Distribuce construction_type
SELECT construction_type, COUNT(*) FROM re_realestate.listings
WHERE construction_type IS NOT NULL AND is_active=true
GROUP BY construction_type ORDER BY COUNT(*) DESC;
