-- KONTEXT (7. 10. 2026, audit dat): RealityMIX a Realcity posílaly stav nemovitosti malými písmeny
-- a s příponou „stav" („velmi dobrý", „dobrý stav", „ve výstavbě (hrubá stavba)"); filtr Stav v API
-- porovnává hodnoty přesně, takže ~1 400 jejich inzerátů ve výsledcích chybělo. Scraper od teď
-- hodnoty sjednocuje (database.normalize_condition), tohle srovná staré řádky.
-- Druhá část: RealityMIX geokóduje podle názvu obce a 12 inzerátů mělo GPS v jiném kraji
-- (Kadov u Blatné místo Kadova na Znojemsku, Kuřim v Čechách…), Božice prohozenou šířku a délku.
-- Scraper takové souřadnice nově zahazuje (database.sanitize_gps); tady se opraví existující řádky.
SET search_path = re_realestate, public;

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
  AND condition <> CASE lower(btrim(condition))
    WHEN 'velmi dobrý' THEN 'Velmi dobrý' WHEN 'velmi dobrý stav' THEN 'Velmi dobrý'
    WHEN 'dobrý' THEN 'Dobrý' WHEN 'dobrý stav' THEN 'Dobrý' WHEN 'novostavba' THEN 'Novostavba'
    WHEN 'před rekonstrukcí' THEN 'Před rekonstrukcí' WHEN 'po rekonstrukci' THEN 'Po rekonstrukci'
    WHEN 've výstavbě' THEN 'Ve výstavbě' WHEN 've výstavbě (hrubá stavba)' THEN 'Ve výstavbě'
    WHEN 'špatný' THEN 'Špatný' WHEN 'špatný stav' THEN 'Špatný' WHEN 'horší stav' THEN 'Špatný'
    WHEN 'projekt' THEN 'Projekt' WHEN 'k demolici' THEN 'K demolici' WHEN 'určený k demolici' THEN 'K demolici'
    ELSE condition END;

-- Prohozená šířka a délka (Božice: 16.288, 48.829)
UPDATE listings SET latitude = longitude, longitude = latitude
WHERE latitude BETWEEN 15.2 AND 17.4 AND longitude BETWEEN 48.5 AND 49.7;

-- GPS mimo rámec Jihomoravského kraje = chyba zdroje; bez polohy je lepší než s cizí
UPDATE listings SET latitude = NULL, longitude = NULL, geocode_source = NULL, geocoded_at = NULL
WHERE latitude IS NOT NULL
  AND NOT (latitude BETWEEN 48.5 AND 49.7 AND longitude BETWEEN 15.2 AND 17.4);
