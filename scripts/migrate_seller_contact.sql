-- Migration: kontakt na makléře u inzerátu (jméno, e-mail, telefon, realitka)
-- Idempotentní – stejné příkazy pouští i DbInitializer při startu API.
-- Plní scraper (zatím SREALITY z detailu v1 API: user + premise); u ostatních zdrojů
-- zůstává NULL a API si kontakt půjčí od člena skupiny duplicit, který ho má.

ALTER TABLE re_realestate.listings
    ADD COLUMN IF NOT EXISTS seller_name    TEXT,
    ADD COLUMN IF NOT EXISTS seller_email   TEXT,
    ADD COLUMN IF NOT EXISTS seller_phone   TEXT,
    ADD COLUMN IF NOT EXISTS seller_company TEXT;
