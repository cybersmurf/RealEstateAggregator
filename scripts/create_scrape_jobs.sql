-- scrape_jobs: job tracking pro Python scraper API
-- Každý job může mít více source_codes (na rozdíl od scrape_runs, která jsou per-source)
CREATE TABLE IF NOT EXISTS re_realestate.scrape_jobs (
    id              uuid                     NOT NULL,
    source_codes    text[]                   NOT NULL,
    full_rescan     boolean                  NOT NULL DEFAULT false,
    status          character varying(20)    NOT NULL DEFAULT 'Queued',
    progress        integer                  NOT NULL DEFAULT 0,
    listings_found  integer,
    listings_new    integer,
    listings_updated integer,
    error_message   text,
    created_at      timestamp with time zone NOT NULL DEFAULT NOW(),
    started_at      timestamp with time zone,
    finished_at     timestamp with time zone,
    CONSTRAINT pk_scrape_jobs PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS ix_scrape_jobs_status     ON re_realestate.scrape_jobs (status);
CREATE INDEX IF NOT EXISTS ix_scrape_jobs_created_at ON re_realestate.scrape_jobs (created_at DESC);

SELECT 'scrape_jobs table created/verified' AS result;
