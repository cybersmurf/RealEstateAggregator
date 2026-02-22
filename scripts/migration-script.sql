Build started...
Build succeeded.
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 're_realestate') THEN
            CREATE SCHEMA re_realestate;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE EXTENSION IF NOT EXISTS vector;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE TABLE re_realestate.sources (
        "Id" uuid NOT NULL,
        "Code" character varying(50) NOT NULL,
        "Name" character varying(200) NOT NULL,
        "BaseUrl" text NOT NULL,
        "IsActive" boolean NOT NULL,
        "SupportsUrlScrape" boolean NOT NULL,
        "SupportsListScrape" boolean NOT NULL,
        "ScraperType" text NOT NULL,
        "CreatedAt" timestamptz NOT NULL,
        "UpdatedAt" timestamptz NOT NULL,
        CONSTRAINT "PK_sources" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE TABLE re_realestate.listings (
        "Id" uuid NOT NULL,
        "SourceId" uuid NOT NULL,
        "SourceCode" character varying(50) NOT NULL,
        "SourceName" character varying(200) NOT NULL,
        "ExternalId" character varying(500),
        "Url" character varying(2000) NOT NULL,
        "Title" character varying(500) NOT NULL,
        "Description" text NOT NULL,
        "PropertyType" integer NOT NULL,
        "OfferType" integer NOT NULL,
        "Price" numeric(18,2),
        "PriceNote" text,
        "LocationText" text NOT NULL,
        "Region" text,
        "District" text,
        "Municipality" text,
        "AreaBuiltUp" double precision,
        "AreaLand" double precision,
        "Rooms" integer,
        "HasKitchen" boolean,
        "ConstructionType" character varying(50),
        "Condition" character varying(50),
        "CreatedAtSource" timestamptz,
        "UpdatedAtSource" timestamptz,
        "FirstSeenAt" timestamptz NOT NULL,
        "LastSeenAt" timestamptz,
        "IsActive" boolean NOT NULL,
        description_embedding vector(1536),
        CONSTRAINT "PK_listings" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_listings_sources_SourceId" FOREIGN KEY ("SourceId") REFERENCES re_realestate.sources ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE TABLE re_realestate.scrape_runs (
        "Id" uuid NOT NULL,
        "SourceId" uuid NOT NULL,
        "SourceCode" text NOT NULL,
        "StartedAt" timestamptz NOT NULL,
        "FinishedAt" timestamptz,
        "Status" character varying(20) NOT NULL,
        "TotalSeen" integer NOT NULL,
        "TotalNew" integer NOT NULL,
        "TotalUpdated" integer NOT NULL,
        "TotalInactivated" integer NOT NULL,
        "ErrorMessage" text,
        CONSTRAINT "PK_scrape_runs" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_scrape_runs_sources_SourceId" FOREIGN KEY ("SourceId") REFERENCES re_realestate.sources ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE TABLE re_realestate.analysis_jobs (
        "Id" uuid NOT NULL,
        "ListingId" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "Status" integer NOT NULL,
        "StorageProvider" character varying(50),
        "StoragePath" text,
        "StorageUrl" text,
        "RequestedAt" timestamptz NOT NULL DEFAULT (now()),
        "FinishedAt" timestamptz,
        "ErrorMessage" text,
        CONSTRAINT "PK_analysis_jobs" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_analysis_jobs_listings_ListingId" FOREIGN KEY ("ListingId") REFERENCES re_realestate.listings ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE TABLE re_realestate.listing_photos (
        "Id" uuid NOT NULL,
        "ListingId" uuid NOT NULL,
        "OriginalUrl" text NOT NULL,
        "StoredUrl" text,
        "Order" integer NOT NULL,
        "CreatedAt" timestamptz NOT NULL DEFAULT (now()),
        CONSTRAINT "PK_listing_photos" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_listing_photos_listings_ListingId" FOREIGN KEY ("ListingId") REFERENCES re_realestate.listings ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE TABLE re_realestate.user_listing_state (
        "Id" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "ListingId" uuid NOT NULL,
        "Status" text NOT NULL,
        "Notes" text,
        "LastUpdated" timestamptz NOT NULL DEFAULT (now()),
        CONSTRAINT "PK_user_listing_state" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_user_listing_state_listings_ListingId" FOREIGN KEY ("ListingId") REFERENCES re_realestate.listings ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_analysis_jobs_ListingId" ON re_realestate.analysis_jobs ("ListingId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_analysis_jobs_Status" ON re_realestate.analysis_jobs ("Status");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_analysis_jobs_Status_RequestedAt" ON re_realestate.analysis_jobs ("Status", "RequestedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_analysis_jobs_UserId" ON re_realestate.analysis_jobs ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_listing_photos_ListingId_Order" ON re_realestate.listing_photos ("ListingId", "Order");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_listings_FirstSeenAt" ON re_realestate.listings ("FirstSeenAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_listings_IsActive" ON re_realestate.listings ("IsActive") WHERE is_active = true;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_listings_IsActive_Municipality_Price" ON re_realestate.listings ("IsActive", "Municipality", "Price") WHERE is_active = true;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_listings_IsActive_Region_Price" ON re_realestate.listings ("IsActive", "Region", "Price") WHERE is_active = true;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_listings_PropertyType_OfferType" ON re_realestate.listings ("PropertyType", "OfferType") WHERE is_active = true;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_listings_SourceCode" ON re_realestate.listings ("SourceCode");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE UNIQUE INDEX "IX_listings_SourceId_ExternalId" ON re_realestate.listings ("SourceId", "ExternalId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_scrape_runs_SourceCode_StartedAt" ON re_realestate.scrape_runs ("SourceCode", "StartedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_scrape_runs_SourceId" ON re_realestate.scrape_runs ("SourceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_scrape_runs_Status" ON re_realestate.scrape_runs ("Status");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE UNIQUE INDEX "IX_sources_Code" ON re_realestate.sources ("Code");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_user_listing_state_ListingId" ON re_realestate.user_listing_state ("ListingId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE UNIQUE INDEX "IX_user_listing_state_UserId_ListingId" ON re_realestate.user_listing_state ("UserId", "ListingId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    CREATE INDEX "IX_user_listing_state_UserId_Status" ON re_realestate.user_listing_state ("UserId", "Status");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260222153038_InitialSchema') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260222153038_InitialSchema', '10.0.3');
    END IF;
END $EF$;
COMMIT;


