-- KONTEXT (7. 10. 2026, audit dat): RealityMIX posílá „ý" jako y + kombinující čárka (NFD), takže
-- „dobrý" mělo 6 znaků, nerovnalo se „Dobrý" a předchozí migrace (porovnání přes lower()) ho
-- přeskočila – 302 inzerátů. Scraper od teď texty převádí do NFC (database.normalize_unicode_fields);
-- tohle srovná staré řádky (stav + 24 popisů) a znovu sjednotí zápis stavu.
SET search_path = re_realestate, public;

UPDATE listings SET condition = normalize(condition, NFC)
WHERE condition IS DISTINCT FROM normalize(condition, NFC);

UPDATE listings SET description = normalize(description, NFC)
WHERE description IS DISTINCT FROM normalize(description, NFC);

UPDATE listings SET condition = CASE lower(btrim(condition))
    WHEN 'velmi dobrý' THEN 'Velmi dobrý'
    WHEN 'velmi dobrý stav' THEN 'Velmi dobrý'
    WHEN 'dobrý' THEN 'Dobrý'
    WHEN 'dobrý stav' THEN 'Dobrý'
    WHEN 'novostavba' THEN 'Novostavba'
    WHEN 'před rekonstrukcí' THEN 'Před rekonstrukcí'
    WHEN 'po rekonstrukci' THEN 'Po rekonstrukci'
    WHEN 've výstavbě' THEN 'Ve výstavbě'
    WHEN 've výstavbě (hrubá stavba)' THEN 'Ve výstavbě'
    WHEN 'špatný' THEN 'Špatný'
    WHEN 'špatný stav' THEN 'Špatný'
    WHEN 'horší stav' THEN 'Špatný'
    WHEN 'projekt' THEN 'Projekt'
    WHEN 'k demolici' THEN 'K demolici'
    WHEN 'určený k demolici' THEN 'K demolici'
    ELSE condition END
WHERE condition IS NOT NULL
  AND lower(btrim(condition)) IN ('velmi dobrý', 'velmi dobrý stav', 'dobrý', 'dobrý stav', 'novostavba',
      'před rekonstrukcí', 'po rekonstrukci', 've výstavbě', 've výstavbě (hrubá stavba)', 'špatný',
      'špatný stav', 'horší stav', 'projekt', 'k demolici', 'určený k demolici')
  AND condition = lower(condition);
