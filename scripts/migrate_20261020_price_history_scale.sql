-- KONTEXT: listing_price_history.price byl numeric bez omezení a scraper do něj posílal Python float,
-- který asyncpg zapsal s přesným binárním rozvojem (328.58999999999997498889570124447345733642578125,
-- 7799193.799999999813735485076904296875). Npgsql takové číslo nepřevede na System.Decimal
-- (OverflowException) a POST /api/listings/search padal na 500, jakmile byl na stránce inzerát
-- s takovým řádkem (2 611 řádků od 5. 9. 2026, hlavně nájmy Sreality s cenou za m²).
-- Porovnání Decimal != float navíc každou noc zapsalo „změnu ceny“ se stejnou cenou (1 672 řádků).
-- Řádky se zaokrouhlí na haléře, po sobě jdoucí shodné ceny se smažou a sloupec dostane numeric(15,2)
-- jako listings.price. Scraper od téhož dne posílá Decimal zaokrouhlený na 2 místa.

UPDATE re_realestate.listing_price_history
SET price = round(price, 2)
WHERE price IS NOT NULL AND scale(price) > 2;

-- Řádek, který cenu oproti předchozímu záznamu téhož inzerátu nemění, není změna ceny.
DELETE FROM re_realestate.listing_price_history h
USING (
    SELECT id
    FROM (
        SELECT id, price,
               lag(price) OVER (PARTITION BY listing_id ORDER BY recorded_at, id) AS prev_price
        FROM re_realestate.listing_price_history
    ) x
    WHERE price IS NOT NULL AND price = prev_price
) dup
WHERE h.id = dup.id;

DO $$
BEGIN
    IF (SELECT numeric_precision
        FROM information_schema.columns
        WHERE table_schema = 're_realestate'
          AND table_name = 'listing_price_history'
          AND column_name = 'price') IS DISTINCT FROM 15 THEN
        ALTER TABLE re_realestate.listing_price_history
            ALTER COLUMN price TYPE numeric(15, 2);
    END IF;
END $$;
