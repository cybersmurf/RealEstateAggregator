INSERT INTO re_realestate.sources (id, code, name, base_url, is_active, supports_url_scrape, supports_list_scrape, scraper_type, created_at, updated_at)
VALUES
  (gen_random_uuid(), 'NEMZNOJMO',    'Nemovitosti Znojmo',        'https://www.nemovitostiznojmo.cz', true, false, true, 'Python', NOW(), NOW()),
  (gen_random_uuid(), 'HVREALITY',    'HV Reality',                'https://www.hvreality.cz',         true, false, true, 'Python', NOW(), NOW()),
  (gen_random_uuid(), 'PREMIAREALITY','Premiera Reality',          'https://www.premiareality.cz',     true, false, true, 'Python', NOW(), NOW()),
  (gen_random_uuid(), 'DELUXREALITY', 'Delux Reality',             'https://www.deluxreality.cz',      true, false, true, 'Python', NOW(), NOW()),
  (gen_random_uuid(), 'LEXAMO',       'Lexamo',                    'https://www.lexamo.cz',            true, false, true, 'Python', NOW(), NOW()),
  (gen_random_uuid(), 'CENTURY21',    'CENTURY 21 Czech Republic', 'https://www.century21.cz',         true, false, true, 'Python', NOW(), NOW())
ON CONFLICT (code) DO NOTHING;

SELECT code, name FROM re_realestate.sources ORDER BY code;
