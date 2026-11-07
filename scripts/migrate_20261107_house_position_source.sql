-- KONTEXT: kontrola 7. 11. 2026 – u 11 ze 45 domů nesouhlasila listings.house_position se skutečností,
-- obrazový model četl společnou zeď v katastrální mapě jako mezeru a jeho „detached" přebíjel
-- makléřovo „Řadový". HousePositionService teď měří společné zdi z půdorysů v katastru (ČÚZK INSPIRE
-- Buildings) a výsledek slaďuje s makléřem a modelem (2 ze 3).
--   house_position_source   – co rozhodlo: cadastre / broker / vision; NULL = určeno před sladěním
--                             (TargetProfile u takových řádků dá přednost makléři)
--   house_position_cadastre – poloha podle půdorysů v katastru; NULL = z katastru nejde určit
ALTER TABLE re_realestate.listings
    ADD COLUMN IF NOT EXISTS house_position_source   TEXT,
    ADD COLUMN IF NOT EXISTS house_position_cadastre TEXT;
