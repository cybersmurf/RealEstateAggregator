-- KONTEXT (8. 10. 2026, audit dat): ai_normalized_data vzniká jen z titulku a popisu, ale model
-- doplňoval i to, co v textu není – energy_class bez zmínky o třídě/PENB u 1 079 z 2 190 inzerátů
-- (Sreality ukazuje „G" jako výchozí bez průkazu), has_elevator bez slova výtah u 691 z 2 036,
-- tepelné čerpadlo bez „čerpadl" u 319 z 805. Nově hodnoty bez opory v textu vrací NormalizationValidator
-- na null; tahle migrace srovná existující řádky stejnými pravidly (kmeny bez diakritiky).
-- Druhá část: shrnutí ve třetině případů nejmenovalo obec. Prompt ji nově dostává zvlášť a vyžaduje;
-- stará shrnutí bez kmene obce dostanou summary_at = NULL a AiEnrichmentHostedService je
-- přegeneruje (text zůstane viditelný, dokud ho nové nenahradí).
SET search_path = re_realestate, public;

CREATE OR REPLACE FUNCTION pg_temp.ua(t text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
  SELECT lower(translate(coalesce(t, ''),
    'ěščřžýáíéůúďťňóĚŠČŘŽÝÁÍÉŮÚĎŤŇÓ', 'escrzyaieuudtnoESCRZYAIEUUDTNO'))
$$;

CREATE OR REPLACE FUNCTION pg_temp.has_stem(t text, VARIADIC stems text[]) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$
  SELECT EXISTS (SELECT 1 FROM unnest(stems) s WHERE position(s IN pg_temp.ua(t)) > 0)
$$;

-- Booleovské příznaky: true jen se zmínkou
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{has_pool}', 'null')
WHERE ai_normalized_data->>'has_pool' = 'true' AND NOT pg_temp.has_stem(title||' '||description, 'bazen');
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{has_garage}', 'null')
WHERE ai_normalized_data->>'has_garage' = 'true' AND NOT pg_temp.has_stem(title||' '||description, 'garaz');
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{has_elevator}', 'null')
WHERE ai_normalized_data->>'has_elevator' = 'true' AND NOT pg_temp.has_stem(title||' '||description, 'vytah');
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{has_terrace}', 'null')
WHERE ai_normalized_data->>'has_terrace' = 'true' AND NOT pg_temp.has_stem(title||' '||description, 'teras');
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{has_balcony}', 'null')
WHERE ai_normalized_data->>'has_balcony' = 'true' AND NOT pg_temp.has_stem(title||' '||description, 'balkon', 'lodzi');
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{has_basement}', 'null')
WHERE ai_normalized_data->>'has_basement' = 'true' AND NOT pg_temp.has_stem(title||' '||description, 'sklep', 'suteren');
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{has_garden}', 'null')
WHERE ai_normalized_data->>'has_garden' = 'true' AND NOT pg_temp.has_stem(title||' '||description, 'zahrad');
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{has_sauna}', 'null')
WHERE ai_normalized_data->>'has_sauna' = 'true' AND NOT pg_temp.has_stem(title||' '||description, 'saun');

-- Zdroj tepla jen pojmenovaný
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{heating_type}', 'null')
WHERE ai_normalized_data->>'heating_type' = 'heat_pump' AND NOT pg_temp.has_stem(title||' '||description, 'cerpadl');
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{heating_type}', 'null')
WHERE ai_normalized_data->>'heating_type' = 'gas' AND NOT pg_temp.has_stem(title||' '||description, 'plyn');
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{heating_type}', 'null')
WHERE ai_normalized_data->>'heating_type' = 'electric' AND NOT pg_temp.has_stem(title||' '||description, 'elektr', 'primotop', 'akumulac');
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{heating_type}', 'null')
WHERE ai_normalized_data->>'heating_type' = 'solid_fuel' AND NOT pg_temp.has_stem(title||' '||description, 'tuh', 'uhl', 'drev', 'pelet', 'kotel', 'krb', 'kamn');
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{heating_type}', 'null')
WHERE ai_normalized_data->>'heating_type' = 'district' AND NOT pg_temp.has_stem(title||' '||description, 'dalkov', 'czt', 'teplarn', 'centraln', 'ustredn');

-- Energetická třída jen jmenovaná (energetick|PENB|průkaz|třída|štítek … písmeno do 60 znaků)
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{energy_class}', 'null')
WHERE ai_normalized_data->>'energy_class' IS NOT NULL
  AND pg_temp.ua(title||' '||description) !~ ('(energetick|penb|prukaz|trid[ay]|stitek)[^.;]{0,60}?\m' || lower(ai_normalized_data->>'energy_class') || '\M');

-- Rok stavby jen napsaný v textu
UPDATE listings SET ai_normalized_data = jsonb_set(ai_normalized_data, '{year_built}', 'null')
WHERE ai_normalized_data->>'year_built' IS NOT NULL
  AND position(ai_normalized_data->>'year_built' IN title||' '||description) = 0;

-- Shrnutí bez obce → k přegenerování. Kmen = první slovo obce („Brno (Židenice)", „Brno-Bohunice" → brno)
-- zkrácené na 4 znaky, u krátkých názvů o jeden méně, aby prošlo skloňování („Brně", „Kuřimi").
CREATE OR REPLACE FUNCTION pg_temp.muni_stem(m text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
  SELECT left(w, least(4, length(w) - 1)) FROM (SELECT regexp_replace(pg_temp.ua(m), '[ (\-].*$', '') w) x
$$;
UPDATE listings SET summary_at = NULL
WHERE is_active AND summary IS NOT NULL AND summary_at IS NOT NULL
  AND municipality IS NOT NULL AND length(municipality) >= 3
  AND position(pg_temp.muni_stem(municipality) IN pg_temp.ua(summary)) = 0;
