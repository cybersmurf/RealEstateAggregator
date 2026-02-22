-- ============================================================================
-- Real Estate Aggregator - Seed Data Script
-- ============================================================================
-- Date: 22. února 2026
-- Purpose: Seed initial sources and sample listings for development

SET search_path TO re_realestate;

-- ============================================================================
-- INSERT SOURCES (Realitní kanceláře)
-- ============================================================================

DELETE FROM listing_photos;
DELETE FROM user_listing_state;
DELETE FROM listings;
DELETE FROM sources;

-- Vložit zdroje
INSERT INTO sources (id, code, name, base_url, is_active, supports_url_scrape, supports_list_scrape, scraper_type, created_at, updated_at)
VALUES
    (gen_random_uuid(), 'REMAX', 'RE/MAX Czech Republic', 'https://www.remax-czech.cz', true, true, true, 'Python', now(), now()),
    (gen_random_uuid(), 'MMR', 'M&M Reality', 'https://www.mmreality.cz', true, true, true, 'Python', now(), now()),
    (gen_random_uuid(), 'PRODEJMETO', 'Prodejme.to', 'https://www.prodejme.to', true, false, true, 'Python', now(), now());

-- ============================================================================
-- INSERT SAMPLE LISTINGS (Pro testování CLI)
-- ============================================================================

-- Byt 1 - REMAX
INSERT INTO listings (id, source_id, source_code, source_name, external_id, url, title, description, 
    location_text, region, district, municipality, property_type, offer_type, price, 
    area_built_up, area_land, rooms, has_kitchen, construction_type, condition,
    created_at_source, updated_at_source, first_seen_at, last_seen_at, is_active)
SELECT 
    gen_random_uuid(),
    id,
    'REMAX', 'REMAX',
    'REMAX-001',
    'https://example.com/remax-001',
    'Útulný byt 3+1 v Brně - ulice Nádraží',
    'Prodám útulný byt 3+1 v centru Brna. Byt je situován v pěkné cihlové budově se dvěma výtahy. Dispozice: obývací pokoj s kuchyněti (20 m²), ložnice (15 m²), dětský pokoj (12 m²), předsíňka, koupelna se sprchovým koutem a toilet separat. V bytě je plastové okno, dřevěné parkety, elektřina v pořádku.',
    'Brno-střed', 'Jihomoravský', 'Brno', 'Brno',
    'Byt', 'Prodej', 4800000::numeric,
    80::double precision, NULL::double precision, 3, true, 'Cihlová', 'Dobrý',
    now() - INTERVAL '30 days', now() - INTERVAL '10 days', now() - INTERVAL '30 days', now() - INTERVAL '10 days', true
FROM sources WHERE code = 'REMAX' LIMIT 1;

-- Byt 2 - REMAX
INSERT INTO listings (id, source_id, source_code, source_name, external_id, url, title, description,
    location_text, region, district, municipality, property_type, offer_type, price,
    area_built_up, area_land, rooms, has_kitchen, construction_type, condition,
    created_at_source, updated_at_source, first_seen_at, last_seen_at, is_active)
SELECT
    gen_random_uuid(),
    id,
    'REMAX', 'REMAX',
    'REMAX-002',
    'https://example.com/remax-002',
    'Mezonetový byt 2+1 v Praze - Vinohrady',
    'Zajímavý mezonetový byt s terasou v horní části Vinohrad. Byt má nový interiér, nový kotel na plyn. Garážové stání v domě. Pěkný výhled na Prahu. Klidné místo s parkem v blízkosti.',
    'Praha-Vinohrady', 'Praha', 'Praha', 'Praha',
    'Byt', 'Prodej', 6200000::numeric,
    90::double precision, NULL::double precision, 2, true, 'Cihlová', 'Výborný',
    now() - INTERVAL '20 days', now() - INTERVAL '5 days', now() - INTERVAL '20 days', now() - INTERVAL '5 days', true
FROM sources WHERE code = 'REMAX' LIMIT 1;

-- Rodinný dům 1 - M&M Reality
INSERT INTO listings (id, source_id, source_code, source_name, external_id, url, title, description,
    location_text, region, district, municipality, property_type, offer_type, price,
    area_built_up, area_land, rooms, has_kitchen, construction_type, condition,
    created_at_source, updated_at_source, first_seen_at, last_seen_at, is_active)
SELECT
    gen_random_uuid(),
    id,
    'MMR', 'M&M Reality',
    'MMR-001',
    'https://example.com/mmr-001',
    'Rodinný dům v Jihomoravském kraji - Znojmo',
    'Prodám rodinný dům v Znojmě. Dům má tři podlaží plus podkroví. Celková plocha domu je 160 m². Na pozemku je i malá zahrada a letní kuchyně. Parkování na vlastním pozemku. Ideální pro rodinu se dvěma dětmi.',
    'Znojmo', 'Jihomoravský', 'Znojmo', 'Znojmo',
    'Dům', 'Prodej', 5500000::numeric,
    160::double precision, 800::double precision, 5, true, 'Cihlová', 'Dobrý',
    now() - INTERVAL '15 days', now() - INTERVAL '3 days', now() - INTERVAL '15 days', now() - INTERVAL '3 days', true
FROM sources WHERE code = 'MMR' LIMIT 1;

-- Studio byt 1 - Prodejme.to
INSERT INTO listings (id, source_id, source_code, source_name, external_id, url, title, description,
    location_text, region, district, municipality, property_type, offer_type, price,
    area_built_up, area_land, rooms, has_kitchen, construction_type, condition,
    created_at_source, updated_at_source, first_seen_at, last_seen_at, is_active)
SELECT
    gen_random_uuid(),
    id,
    'PRODEJMETO', 'Prodejme.to',
    'PROD-001',
    'https://example.com/prod-001',
    'Studio byt v centru Prahy - Praha 1',
    'Pronajmu moderní studio byt v absolutním centru Prahy. Nově zrekonstruovaný byt s kuchyňským koutem. Výhled na Vltavu. Všechny potřeby jsou v chůzi - obchody, restaurace, kulturní akce. Bezpečné okolí.',
    'Praha-Staré Město', 'Praha', 'Praha', 'Praha',
    'Byt', 'Pronájem', 18000::numeric,
    35::double precision, NULL::double precision, 1, true, 'Kamenná', 'Výborný',
    now() - INTERVAL '7 days', now() - INTERVAL '1 days', now() - INTERVAL '7 days', now() - INTERVAL '1 days', true
FROM sources WHERE code = 'PRODEJMETO' LIMIT 1;

-- ============================================================================
-- INSERT SAMPLE PHOTOS (Obrázky k inzerátům)
-- ============================================================================

INSERT INTO listing_photos (id, listing_id, original_url, order_index)
SELECT
    gen_random_uuid(),
    id,
    'https://via.placeholder.com/600x400?text=Listing+' || substring(id::text, 1, 8),
    1
FROM listings;

-- ============================================================================
-- SUMMARY
-- ============================================================================

SELECT 
    (SELECT COUNT(*) FROM sources) as source_count,
    (SELECT COUNT(*) FROM listings) as listing_count,
    (SELECT COUNT(*) FROM listing_photos) as photo_count;
