using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure.Security;

namespace RealEstate.Infrastructure;

public static class DbInitializer
{
    public static async Task SeedAsync(RealEstateDbContext dbContext, CancellationToken cancellationToken = default, ILogger? logger = null)
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
            -- Legacy OneDrive sloupce odstraněny migrací scripts/migrate_drop_onedrive.sql
            ALTER TABLE re_realestate.listings DROP COLUMN IF EXISTS onedrive_folder_id;
            ALTER TABLE re_realestate.listings DROP COLUMN IF EXISTS onedrive_inspection_folder_id;

            -- Duplicate detection napříč zdroji (stejný dům, jiný source)
            ALTER TABLE re_realestate.listings ADD COLUMN IF NOT EXISTS duplicate_of_listing_id uuid REFERENCES re_realestate.listings(id) ON DELETE SET NULL;
            CREATE INDEX IF NOT EXISTS ix_listings_duplicate_of ON re_realestate.listings(duplicate_of_listing_id);

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

        // ── Účty, uložená hledání, API klíče, leady, doba na trhu, aukce, shrnutí ──
        // Stejný SQL jako scripts/migrate_accounts_market.sql (idempotentní).
        await dbContext.Database.ExecuteSqlRawAsync(AccountsMarketMigrationSql, cancellationToken);

        await SeedAdminAsync(dbContext, logger, cancellationToken);
    }
    /// <summary>
    /// Výchozí admin účet = historický DefaultUserId (00000000-…-0001), na který ukazují všechny
    /// user_listing_state z doby před účty. E-mail/heslo z ADMIN_EMAIL / ADMIN_PASSWORD –
    /// heslo se při každém startu srovná s proměnnou, takže vlastník nikdy nezůstane zamčený venku.
    /// </summary>
    private static async Task SeedAdminAsync(RealEstateDbContext dbContext, ILogger? logger, CancellationToken cancellationToken)
    {
        var adminEmail = Environment.GetEnvironmentVariable("ADMIN_EMAIL")?.Trim().ToLowerInvariant();
        var adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");

        var admin = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == UserPlans.DefaultAdminId, cancellationToken);
        if (admin is null)
        {
            admin = new User
            {
                Id = UserPlans.DefaultAdminId,
                Email = adminEmail ?? "admin@realestate.local",
                DisplayName = "Admin",
                Plan = UserPlans.Profi,
                IsAdmin = true,
            };
            dbContext.Users.Add(admin);
        }

        var changed = false;
        if (!string.IsNullOrEmpty(adminEmail) && !string.Equals(admin.Email, adminEmail, StringComparison.Ordinal))
        {
            var taken = await dbContext.Users.AnyAsync(u => u.Email == adminEmail && u.Id != admin.Id, cancellationToken);
            if (!taken)
            {
                admin.Email = adminEmail;
                changed = true;
            }
        }

        if (!string.IsNullOrEmpty(adminPassword) && !PasswordHashing.Verify(adminPassword, admin.PasswordHash))
        {
            admin.PasswordHash = PasswordHashing.Hash(adminPassword);
            changed = true;
        }

        if (admin.PasswordHash is null)
            logger?.LogWarning("Admin účet {Email} nemá heslo – nastavte ADMIN_PASSWORD, jinak se nelze přihlásit.", admin.Email);

        if (dbContext.Entry(admin).State == EntityState.Added || changed)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private const string AccountsMarketMigrationSql = """
        -- Migration: účty, uložená hledání, API klíče, leady, doba na trhu, aukce, AI shrnutí
        -- Idempotentní – stejné příkazy pouští i DbInitializer při startu API.

        -- 1. listings: doba na trhu, aukce, shrnutí
        ALTER TABLE re_realestate.listings
            ADD COLUMN IF NOT EXISTS deactivated_at          TIMESTAMPTZ,
            ADD COLUMN IF NOT EXISTS auction_date            TIMESTAMPTZ,
            ADD COLUMN IF NOT EXISTS auction_starting_price  NUMERIC(15,2),
            ADD COLUMN IF NOT EXISTS auction_deposit         NUMERIC(15,2),
            ADD COLUMN IF NOT EXISTS summary                 TEXT,
            ADD COLUMN IF NOT EXISTS summary_at              TIMESTAMPTZ;

        -- Backfill: u už neaktivních inzerátů je nejlepší odhad stažení poslední spatření
        UPDATE re_realestate.listings
        SET deactivated_at = COALESCE(last_seen_at, first_seen_at)
        WHERE is_active = false AND deactivated_at IS NULL;

        CREATE INDEX IF NOT EXISTS ix_listings_deactivated_at
            ON re_realestate.listings(deactivated_at) WHERE deactivated_at IS NOT NULL;

        -- 2. users
        CREATE TABLE IF NOT EXISTS re_realestate.users (
            id                      UUID PRIMARY KEY,
            email                   VARCHAR(320) NOT NULL,
            password_hash           TEXT,
            display_name            VARCHAR(200),
            plan                    VARCHAR(20) NOT NULL DEFAULT 'free',
            plan_valid_until        TIMESTAMPTZ,
            is_admin                BOOLEAN NOT NULL DEFAULT false,
            is_active               BOOLEAN NOT NULL DEFAULT true,
            stripe_customer_id      VARCHAR(100),
            stripe_subscription_id  VARCHAR(100),
            telegram_chat_id        VARCHAR(50),
            created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
            last_login_at           TIMESTAMPTZ
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_users_email ON re_realestate.users(lower(email));
        CREATE INDEX IF NOT EXISTS ix_users_stripe_customer ON re_realestate.users(stripe_customer_id);

        -- Výchozí admin = historický DefaultUserId, na který ukazují všechny stávající user_listing_state
        INSERT INTO re_realestate.users (id, email, display_name, plan, is_admin)
        VALUES ('00000000-0000-0000-0000-000000000001', 'admin@realestate.local', 'Admin', 'profi', true)
        ON CONFLICT (id) DO NOTHING;

        -- 3. saved_searches
        CREATE TABLE IF NOT EXISTS re_realestate.saved_searches (
            id                  UUID PRIMARY KEY,
            user_id             UUID NOT NULL REFERENCES re_realestate.users(id) ON DELETE CASCADE,
            name                VARCHAR(200) NOT NULL,
            filter_json         JSONB NOT NULL DEFAULT '{}'::jsonb,
            notify_email        BOOLEAN NOT NULL DEFAULT true,
            notify_telegram     BOOLEAN NOT NULL DEFAULT false,
            notify_new_listings BOOLEAN NOT NULL DEFAULT true,
            notify_price_drops  BOOLEAN NOT NULL DEFAULT true,
            is_active           BOOLEAN NOT NULL DEFAULT true,
            created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
            last_run_at         TIMESTAMPTZ,
            last_notified_at    TIMESTAMPTZ,
            total_notified      INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_saved_searches_user_active ON re_realestate.saved_searches(user_id, is_active);

        CREATE TABLE IF NOT EXISTS re_realestate.saved_search_notifications (
            id               UUID PRIMARY KEY,
            saved_search_id  UUID NOT NULL REFERENCES re_realestate.saved_searches(id) ON DELETE CASCADE,
            listing_id       UUID NOT NULL,
            kind             VARCHAR(20) NOT NULL,
            old_price        NUMERIC(15,2),
            new_price        NUMERIC(15,2),
            sent_at          TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_saved_search_notifications_once
            ON re_realestate.saved_search_notifications(saved_search_id, listing_id, kind);

        -- 4. api_keys
        CREATE TABLE IF NOT EXISTS re_realestate.api_keys (
            id              UUID PRIMARY KEY,
            user_id         UUID NOT NULL REFERENCES re_realestate.users(id) ON DELETE CASCADE,
            name            VARCHAR(200) NOT NULL,
            key_hash        VARCHAR(64) NOT NULL,
            key_prefix      VARCHAR(16) NOT NULL,
            daily_quota     INTEGER NOT NULL DEFAULT 1000,
            used_today      INTEGER NOT NULL DEFAULT 0,
            quota_day       DATE,
            total_requests  BIGINT NOT NULL DEFAULT 0,
            last_used_at    TIMESTAMPTZ,
            is_active       BOOLEAN NOT NULL DEFAULT true,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_api_keys_hash ON re_realestate.api_keys(key_hash);
        CREATE INDEX IF NOT EXISTS ix_api_keys_user ON re_realestate.api_keys(user_id);

        -- 5. leads
        CREATE TABLE IF NOT EXISTS re_realestate.leads (
            id              UUID PRIMARY KEY,
            listing_id      UUID,
            user_id         UUID,
            kind            VARCHAR(30) NOT NULL,
            name            VARCHAR(200) NOT NULL,
            email           VARCHAR(320) NOT NULL,
            phone           VARCHAR(50),
            message         TEXT,
            property_price  NUMERIC(15,2),
            loan_amount     NUMERIC(15,2),
            loan_years      INTEGER,
            source          VARCHAR(200),
            consent         BOOLEAN NOT NULL DEFAULT false,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            forwarded_at    TIMESTAMPTZ
        );
        CREATE INDEX IF NOT EXISTS ix_leads_created ON re_realestate.leads(created_at);

        -- 6. Index pro tržní statistiky (medián Kč/m² dle obce a dispozice)
        CREATE INDEX IF NOT EXISTS ix_listings_market_stats
            ON re_realestate.listings(municipality, property_type, offer_type, disposition)
            WHERE price IS NOT NULL AND area_built_up IS NOT NULL;
        """;
}
