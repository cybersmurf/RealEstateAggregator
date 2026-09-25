-- Migration: účty, uložená hledání, API klíče, leady, doba na trhu, aukce, AI shrnutí
-- Run: docker exec -i realestate-db psql -U postgres -d realestate_dev < scripts/migrate_accounts_market.sql
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
