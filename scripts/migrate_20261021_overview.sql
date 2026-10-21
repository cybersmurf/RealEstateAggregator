-- KONTEXT: Stránka /prehled pro Petra a Lenku (Dnes / Kandidáti / Prohlídky): hlasy k domům,
-- ruční pořadí kandidátů, prohlídky s checklistem, denní záznam pro záložku Dnes a strukturované
-- hodnocení u analýz (aby se karta domu neparsovala z markdownu). Vše se váže na inzerát a čte
-- za celou skupinu duplicit. Lenka (vlastní účet lenka@blackies.cz) se stává členem Petrova
-- workspace jako writer, aby viděla jeho stavy inzerátů (K návštěvě / Líbí se) a mohla hlasovat
-- pod svým jménem; Petrův účet dostane jméno „Petr" místo „Admin".

CREATE TABLE IF NOT EXISTS re_realestate.house_votes (
    id            uuid PRIMARY KEY,
    listing_id    uuid NOT NULL REFERENCES re_realestate.listings(id) ON DELETE CASCADE,
    user_id       uuid NOT NULL REFERENCES re_realestate.users(id) ON DELETE CASCADE,
    value         varchar(10) NOT NULL,
    note          text,
    price_at_vote numeric(15, 2),
    updated_at    timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_house_votes_listing_user ON re_realestate.house_votes (listing_id, user_id);

CREATE TABLE IF NOT EXISTS re_realestate.house_ranks (
    id         uuid PRIMARY KEY,
    listing_id uuid NOT NULL REFERENCES re_realestate.listings(id) ON DELETE CASCADE,
    owner_id   uuid NOT NULL,
    bucket     varchar(10) NOT NULL,
    position   integer NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_house_ranks_owner_listing ON re_realestate.house_ranks (owner_id, listing_id);

CREATE TABLE IF NOT EXISTS re_realestate.house_visits (
    id                 uuid PRIMARY KEY,
    listing_id         uuid NOT NULL REFERENCES re_realestate.listings(id) ON DELETE CASCADE,
    owner_id           uuid NOT NULL,
    scheduled_at       timestamptz,
    agent_name         varchar(200),
    agent_phone        varchar(50),
    address            varchar(500),
    checklist          jsonb NOT NULL DEFAULT '[]'::jsonb,
    note               text,
    status             varchar(20) NOT NULL DEFAULT 'planned',
    created_by_user_id uuid,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_house_visits_owner_scheduled ON re_realestate.house_visits (owner_id, scheduled_at);

CREATE TABLE IF NOT EXISTS re_realestate.daily_digests (
    id           uuid PRIMARY KEY,
    owner_id     uuid NOT NULL,
    digest_date  date NOT NULL,
    generated_at timestamptz NOT NULL DEFAULT now(),
    summary      text NOT NULL DEFAULT '',
    items        jsonb NOT NULL DEFAULT '[]'::jsonb
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_daily_digests_owner_date ON re_realestate.daily_digests (owner_id, digest_date);

ALTER TABLE re_realestate.listing_analyses ADD COLUMN IF NOT EXISTS structured jsonb;

UPDATE re_realestate.users SET display_name = 'Petr'
WHERE id = '00000000-0000-0000-0000-000000000001' AND (display_name IS NULL OR display_name = 'Admin');

INSERT INTO re_realestate.workspace_members (id, owner_id, email, role, created_at)
SELECT gen_random_uuid(), '00000000-0000-0000-0000-000000000001', 'lenka@blackies.cz', 'writer', now()
WHERE EXISTS (SELECT 1 FROM re_realestate.users WHERE email = 'lenka@blackies.cz')
  AND NOT EXISTS (SELECT 1 FROM re_realestate.workspace_members WHERE email = 'lenka@blackies.cz');
