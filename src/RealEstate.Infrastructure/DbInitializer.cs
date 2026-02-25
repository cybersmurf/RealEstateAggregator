using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure;

public static class DbInitializer
{
    public static async Task SeedAsync(RealEstateDbContext dbContext, CancellationToken cancellationToken = default)
    {
        // Upsert logika: přidá chybějící sources, stávající nevymaže
        var existingCodes = await dbContext.Sources
            .Select(s => s.Code)
            .ToHashSetAsync(cancellationToken);

        var allSources = new List<Source>
        {
            new()
            {
                Code = "REMAX",
                Name = "RE/MAX Czech Republic",
                BaseUrl = "https://www.remax-czech.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "MMR",
                Name = "M&M Reality",
                BaseUrl = "https://www.mmreality.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "PRODEJMETO",
                Name = "Prodejme.to",
                BaseUrl = "https://www.prodejme.to",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "ZNOJMOREALITY",
                Name = "Znojmo Reality",
                BaseUrl = "https://www.znojmoreality.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "SREALITY",
                Name = "Sreality",
                BaseUrl = "https://www.sreality.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "NEMZNOJMO",
                Name = "Nemovitosti Znojmo",
                BaseUrl = "https://www.nemovitostiznojmo.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "HVREALITY",
                Name = "Horák & Vetchý reality",
                BaseUrl = "https://hvreality.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "PREMIAREALITY",
                Name = "PREMIA Reality s.r.o.",
                BaseUrl = "https://www.premiareality.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "DELUXREALITY",
                Name = "DeluXreality Znojmo",
                BaseUrl = "https://deluxreality.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "LEXAMO",
                Name = "Lexamo Reality",
                BaseUrl = "https://www.lexamo.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "CENTURY21",
                Name = "CENTURY 21 Czech Republic",
                BaseUrl = "https://www.century21.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "IDNES",
                Name = "iDnes Reality",
                BaseUrl = "https://reality.idnes.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
        };

        var newSources = allSources.Where(s => !existingCodes.Contains(s.Code)).ToList();
        if (newSources.Count > 0)
        {
            dbContext.Sources.AddRange(newSources);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // ── Schema migrations (idempotentní SQL patche) ──────────────────────────
        // Přidáme search_tsv GENERATED sloupec + GIN index, pokud ještě neexistují.
        // EnsureCreatedAsync nevytváří sloupce přidané po iniciálním vytvoření.
        await dbContext.Database.ExecuteSqlRawAsync("""
            ALTER TABLE re_realestate.listings
                ADD COLUMN IF NOT EXISTS search_tsv tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('simple', coalesce(title, '')), 'A') ||
                    setweight(to_tsvector('simple', coalesce(location_text, '')), 'B') ||
                    setweight(to_tsvector('simple', coalesce(description, '')), 'C')
                ) STORED;

            CREATE INDEX IF NOT EXISTS idx_listings_search_tsv
                ON re_realestate.listings
                USING gin (search_tsv);
            
            -- Export folder IDs – ukládáme po exportu, idempotentní export + upload bez session state
            ALTER TABLE re_realestate.listings ADD COLUMN IF NOT EXISTS drive_folder_id text;
            ALTER TABLE re_realestate.listings ADD COLUMN IF NOT EXISTS drive_inspection_folder_id text;
            ALTER TABLE re_realestate.listings ADD COLUMN IF NOT EXISTS onedrive_folder_id text;
            ALTER TABLE re_realestate.listings ADD COLUMN IF NOT EXISTS onedrive_inspection_folder_id text;

            -- RAG: listing_analyses tabulka s pgvector embeddingy
            -- Dimenze 768 = nomic-embed-text (Ollama). Pro OpenAI text-embedding-3-small použij 1536.
            CREATE TABLE IF NOT EXISTS re_realestate.listing_analyses (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                listing_id uuid NOT NULL REFERENCES re_realestate.listings(id) ON DELETE CASCADE,
                content text NOT NULL,
                embedding vector(768),
                source text NOT NULL DEFAULT 'manual',
                title text,
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now()
            );

            -- Pokud tabulka existovala se starým vector(1536), přetypuj sloupec
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 're_realestate'
                      AND table_name = 'listing_analyses'
                      AND column_name = 'embedding'
                      AND udt_name = 'vector'
                ) THEN
                    -- Zjistíme dimenzi přes pg_attribute
                    IF (SELECT atttypmod FROM pg_attribute
                        JOIN pg_class ON attrelid = pg_class.oid
                        JOIN pg_namespace ON relnamespace = pg_namespace.oid
                        WHERE nspname = 're_realestate' AND relname = 'listing_analyses'
                          AND attname = 'embedding') != 768 THEN
                        ALTER TABLE re_realestate.listing_analyses
                            ALTER COLUMN embedding TYPE vector(768) USING NULL;
                    END IF;
                END IF;
            END $$;

            CREATE INDEX IF NOT EXISTS idx_listing_analyses_listing_id
                ON re_realestate.listing_analyses(listing_id);

            CREATE INDEX IF NOT EXISTS idx_listing_analyses_created_at
                ON re_realestate.listing_analyses(created_at DESC);
            """, cancellationToken);
    }
}
