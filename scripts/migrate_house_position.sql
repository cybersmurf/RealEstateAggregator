-- Migration: poloha domu vůči sousedním stavbám (6. 10. 2026)
-- Idempotentní – stejné příkazy pouští i DbInitializer při startu API.
--   house_position        – určeno z venkovních a leteckých fotek: detached / semi_detached / terraced / corner / unknown
--   house_position_reason – česky, co je na které straně domu
--   house_position_listed – co uvedl makléř ve zdroji (Sreality „Poloha domu": Samostatný / Řadový / Rohový / V bloku)
ALTER TABLE re_realestate.listings
    ADD COLUMN IF NOT EXISTS house_position        TEXT,
    ADD COLUMN IF NOT EXISTS house_position_reason TEXT,
    ADD COLUMN IF NOT EXISTS house_position_at     TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS house_position_listed TEXT;
