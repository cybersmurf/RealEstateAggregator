using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Pgvector.EntityFrameworkCore;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Infrastructure;

public sealed class RealEstateDbContext : DbContext
{
    public RealEstateDbContext(DbContextOptions<RealEstateDbContext> options)
        : base(options)
    {
    }

    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<ListingPhoto> ListingPhotos => Set<ListingPhoto>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<UserListingState> UserListingStates => Set<UserListingState>();
    public DbSet<AnalysisJob> AnalysisJobs => Set<AnalysisJob>();
    public DbSet<ScrapeRun> ScrapeRuns => Set<ScrapeRun>();
    public DbSet<UserListingPhoto> UserListingPhotos => Set<UserListingPhoto>();
    public DbSet<SpatialArea> SpatialAreas => Set<SpatialArea>();
    public DbSet<ListingAnalysis> ListingAnalyses => Set<ListingAnalysis>();
    public DbSet<ListingCadastreData> ListingCadastreData => Set<ListingCadastreData>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 🔥 Enable pgvector extension
        modelBuilder.HasPostgresExtension("vector");

        // 🔥 Configure Npgsql to use snake_case naming
        modelBuilder.UseIdentityAlwaysColumns();

        // ============================================================================
        // Source
        // ============================================================================
        modelBuilder.Entity<Source>(entity =>
        {
            entity.ToTable("sources", "re_realestate");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Code).IsUnique();
            entity.Property(e => e.Code).HasColumnName("code").HasMaxLength(50).IsRequired();
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(e => e.BaseUrl).HasColumnName("base_url").IsRequired();
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.SupportsUrlScrape).HasColumnName("supports_url_scrape");
            entity.Property(e => e.SupportsListScrape).HasColumnName("supports_list_scrape");
            entity.Property(e => e.ScraperType).HasColumnName("scraper_type");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        // ============================================================================
        // Listing - MAIN ENTITY WITH PGVECTOR
        // ============================================================================
        modelBuilder.Entity<Listing>(entity =>
        {
            entity.ToTable("listings", "re_realestate");
            entity.HasKey(e => e.Id);

            // Column mappings - PascalCase properties to snake_case columns
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.SourceCode).HasColumnName("source_code").HasMaxLength(50).IsRequired();
            entity.Property(e => e.SourceName).HasColumnName("source_name").HasMaxLength(200).IsRequired();
            entity.Property(e => e.ExternalId).HasColumnName("external_id").HasMaxLength(500);
            entity.Property(e => e.Url).HasColumnName("url").HasMaxLength(2000).IsRequired();
            entity.Property(e => e.Title).HasColumnName("title").HasMaxLength(500).IsRequired();
            entity.Property(e => e.Description).HasColumnName("description").IsRequired();
            entity.Property(e => e.LocationText).HasColumnName("location_text").IsRequired();
            entity.Property(e => e.Region).HasColumnName("region");
            entity.Property(e => e.District).HasColumnName("district");
            entity.Property(e => e.Municipality).HasColumnName("municipality");
            entity.Property(e => e.PropertyType)
                .HasColumnName("property_type")
                .HasConversion(
                    v => v.ToString(),
                    v => v == "House" ? PropertyType.House
                       : v == "Apartment" ? PropertyType.Apartment
                       : v == "Land" ? PropertyType.Land
                       : v == "Cottage" ? PropertyType.Cottage
                       : v == "Commercial" ? PropertyType.Commercial
                       : v == "Industrial" ? PropertyType.Industrial
                       : v == "Garage" ? PropertyType.Garage
                       : PropertyType.Other);
            entity.Property(e => e.OfferType)
                .HasColumnName("offer_type")
                .HasConversion(
                    v => v.ToString(),
                    v => v == "Rent" ? OfferType.Rent
                       : v == "Auction" ? OfferType.Auction
                       : OfferType.Sale);
            entity.Property(e => e.Price).HasColumnName("price").HasColumnType("numeric(15,2)");
            entity.Property(e => e.PriceNote).HasColumnName("price_note");
            entity.Property(e => e.AreaBuiltUp).HasColumnName("area_built_up");
            entity.Property(e => e.AreaLand).HasColumnName("area_land");
            entity.Property(e => e.Rooms).HasColumnName("rooms");
            entity.Property(e => e.HasKitchen).HasColumnName("has_kitchen");
            entity.Property(e => e.ConstructionType).HasColumnName("construction_type").HasMaxLength(50);
            entity.Property(e => e.Condition).HasColumnName("condition").HasMaxLength(50);
            entity.Property(e => e.CreatedAtSource).HasColumnName("created_at_source").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAtSource).HasColumnName("updated_at_source").HasColumnType("timestamptz");
            entity.Property(e => e.FirstSeenAt).HasColumnName("first_seen_at").HasColumnType("timestamptz");
            entity.Property(e => e.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamptz");
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.DescriptionEmbedding).HasColumnName("description_embedding").HasColumnType("vector(1536)");

            // 📍 GPS souřadnice
            entity.Property(e => e.Latitude).HasColumnName("latitude");
            entity.Property(e => e.Longitude).HasColumnName("longitude");
            entity.Property(e => e.GeocodedAt).HasColumnName("geocoded_at").HasColumnType("timestamptz");
            entity.Property(e => e.GeocodeSource).HasColumnName("geocode_source").HasMaxLength(20);

            // location_point je spravován PostGIS triggerem – ignorujeme přímým EF
            entity.Ignore("LocationPoint");

            entity.Property(e => e.DriveFolderId).HasColumnName("drive_folder_id");
            entity.Property(e => e.DriveInspectionFolderId).HasColumnName("drive_inspection_folder_id");
            entity.Property(e => e.OneDriveFolderId).HasColumnName("onedrive_folder_id");
            entity.Property(e => e.OneDriveInspectionFolderId).HasColumnName("onedrive_inspection_folder_id");

            // Ollama text features
            entity.Property(e => e.SmartTags).HasColumnName("smart_tags").HasColumnType("text");
            entity.Property(e => e.SmartTagsAt).HasColumnName("smart_tags_at").HasColumnType("timestamptz");
            entity.Property(e => e.AiNormalizedData).HasColumnName("ai_normalized_data").HasColumnType("jsonb");
            entity.Property(e => e.AiNormalizedAt).HasColumnName("ai_normalized_at").HasColumnType("timestamptz");
            entity.Property(e => e.PriceSignal).HasColumnName("price_signal").HasMaxLength(10);
            entity.Property(e => e.PriceSignalReason).HasColumnName("price_signal_reason");
            entity.Property(e => e.PriceSignalAt).HasColumnName("price_signal_at").HasColumnType("timestamptz");

            // 🔍 Shadow property pro precomputed tsvector (GIN index) – fulltext search
            entity.Property<NpgsqlTsVector>("SearchTsv")
                .HasColumnName("search_tsv")
                .HasColumnType("tsvector")
                .ValueGeneratedOnAddOrUpdate();

            // Indexes
            entity.HasIndex(e => new { e.SourceId, e.ExternalId }).IsUnique();
            entity.HasIndex(e => new { e.IsActive, e.Region, e.Price })
                .HasFilter("is_active = true");
            entity.HasIndex(e => new { e.IsActive, e.Municipality, e.Price })
                .HasFilter("is_active = true");
            entity.HasIndex(e => e.FirstSeenAt);
            entity.HasIndex(e => new { e.PropertyType, e.OfferType })
                .HasFilter("is_active = true");
            entity.HasIndex(e => e.SourceCode);
            entity.HasIndex(e => e.IsActive)
                .HasFilter("is_active = true");

            // Foreign key to Source
            entity.HasOne(e => e.Source)
                .WithMany(s => s.Listings)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Restrict);

            // Navigation to Photos
            entity.HasMany(e => e.Photos)
                .WithOne(p => p.Listing)
                .HasForeignKey(p => p.ListingId)
                .OnDelete(DeleteBehavior.Cascade);

            // Navigation to UserStates
            entity.HasMany(e => e.UserStates)
                .WithOne(u => u.Listing)
                .HasForeignKey(u => u.ListingId)
                .OnDelete(DeleteBehavior.Cascade);

            // Navigation to AnalysisJobs
            entity.HasMany(e => e.AnalysisJobs)
                .WithOne(a => a.Listing)
                .HasForeignKey(a => a.ListingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ============================================================================
        // ListingPhoto
        // ============================================================================
        modelBuilder.Entity<ListingPhoto>(entity =>
        {
            entity.ToTable("listing_photos", "re_realestate");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ListingId).HasColumnName("listing_id");
            entity.Property(e => e.OriginalUrl).HasColumnName("original_url");
            entity.Property(e => e.StoredUrl).HasColumnName("stored_url");
            entity.Property(e => e.Order).HasColumnName("order_index");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
            entity.Property(e => e.AiDescription).HasColumnName("ai_description");
            entity.Property(e => e.PhotoCategory).HasColumnName("photo_category");
            entity.Property(e => e.PhotoDescription).HasColumnName("photo_description");
            entity.Property(e => e.PhotoLabels).HasColumnName("photo_labels").HasColumnType("text");
            entity.Property(e => e.DamageDetected).HasColumnName("damage_detected").HasDefaultValue(false);
            entity.Property(e => e.ClassificationConfidence).HasColumnName("classification_confidence").HasColumnType("numeric(3,2)");
            entity.Property(e => e.ClassifiedAt).HasColumnName("classified_at").HasColumnType("timestamptz");
            entity.Property(e => e.ClassificationFeedback).HasColumnName("classification_feedback");
            entity.Property(e => e.AltText).HasColumnName("alt_text");
            entity.HasIndex(e => new { e.ListingId, e.Order });
        });

        // ============================================================================
        // UserListingState
        // ============================================================================
        modelBuilder.Entity<UserListingState>(entity =>
        {
            entity.ToTable("user_listing_state", "re_realestate");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.ListingId).HasColumnName("listing_id");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.LastUpdated).HasColumnName("last_updated").HasColumnType("timestamptz").HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.UserId, e.ListingId }).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.Status });
        });

        // ============================================================================
        // AnalysisJob
        // ============================================================================
        modelBuilder.Entity<AnalysisJob>(entity =>
        {
            entity.ToTable("analysis_jobs", "re_realestate");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ListingId).HasColumnName("listing_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.RequestedAt).HasColumnName("requested_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
            entity.Property(e => e.FinishedAt).HasColumnName("finished_at").HasColumnType("timestamptz");
            entity.Property(e => e.StorageProvider).HasColumnName("storage_provider").HasMaxLength(50);
            entity.Property(e => e.StoragePath).HasColumnName("storage_path");
            entity.Property(e => e.StorageUrl).HasColumnName("storage_url");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ListingId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.Status, e.RequestedAt });
        });

        // ============================================================================
        // ScrapeRun
        // ============================================================================
        modelBuilder.Entity<ScrapeRun>(entity =>
        {
            entity.ToTable("scrape_runs", "re_realestate");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.SourceCode).HasColumnName("source_code");
            entity.Property(e => e.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            entity.Property(e => e.FinishedAt).HasColumnName("finished_at").HasColumnType("timestamptz");
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.TotalSeen).HasColumnName("total_seen");
            entity.Property(e => e.TotalNew).HasColumnName("total_new");
            entity.Property(e => e.TotalUpdated).HasColumnName("total_updated");
            entity.Property(e => e.TotalInactivated).HasColumnName("total_inactivated");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
            entity.HasIndex(e => new { e.SourceCode, e.StartedAt });
            entity.HasIndex(e => e.Status);

            // Foreign key to Source
            entity.HasOne(e => e.Source)
                .WithMany(s => s.ScrapeRuns)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ============================================================================
        // UserListingPhoto - User's own photos from property visits
        // ============================================================================
        modelBuilder.Entity<UserListingPhoto>(entity =>
        {
            entity.ToTable("user_listing_photos", "re_realestate");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ListingId).HasColumnName("listing_id");
            entity.Property(e => e.StoredUrl).HasColumnName("stored_url").HasMaxLength(500).IsRequired();
            entity.Property(e => e.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(255).IsRequired();
            entity.Property(e => e.FileSizeBytes).HasColumnName("file_size_bytes");
            entity.Property(e => e.TakenAt).HasColumnName("taken_at").HasColumnType("timestamptz");
            entity.Property(e => e.UploadedAt).HasColumnName("uploaded_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
            entity.Property(e => e.Notes).HasColumnName("notes").HasMaxLength(1000);
            entity.Property(e => e.AiDescription).HasColumnName("ai_description");

            // AI Classification columns
            entity.Property(e => e.PhotoCategory).HasColumnName("photo_category");
            entity.Property(e => e.PhotoLabels).HasColumnName("photo_labels");
            entity.Property(e => e.DamageDetected).HasColumnName("damage_detected").HasDefaultValue(false);
            entity.Property(e => e.ClassificationConfidence).HasColumnName("classification_confidence").HasColumnType("numeric(3,2)");
            entity.Property(e => e.ClassifiedAt).HasColumnName("classified_at").HasColumnType("timestamptz");

            // Foreign key to Listing (cascade delete - if listing deleted, photos deleted too)
            entity.HasOne(e => e.Listing)
                .WithMany()
                .HasForeignKey(e => e.ListingId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            entity.HasIndex(e => e.ListingId);
            entity.HasIndex(e => e.UploadedAt);
        });

        // =====================================================================
        // ListingAnalysis – RAG analýzy s pgvector embeddingy
        // =====================================================================
        modelBuilder.Entity<ListingAnalysis>(entity =>
        {
            entity.ToTable("listing_analyses", "re_realestate");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ListingId).HasColumnName("listing_id");
            entity.Property(e => e.Content).HasColumnName("content").IsRequired();
            entity.Property(e => e.Embedding)
                .HasColumnName("embedding")
                .HasColumnType("vector(768)");  // nomic-embed-text (Ollama). OpenAI: vector(1536)
            entity.Property(e => e.Source)
                .HasColumnName("source")
                .HasMaxLength(50)
                .HasDefaultValue("manual");
            entity.Property(e => e.Title).HasColumnName("title").HasMaxLength(500);
            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne(e => e.Listing)
                .WithMany(l => l.Analyses)
                .HasForeignKey(e => e.ListingId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ListingId);
            entity.HasIndex(e => e.CreatedAt);
        });

        // =====================================================================
        // ListingCadastreData – ČÚZK/RUIAN katastrální data
        // =====================================================================
        modelBuilder.Entity<ListingCadastreData>(entity =>
        {
            entity.ToTable("listing_cadastre_data", "re_realestate");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ListingId).HasColumnName("listing_id");
            entity.Property(e => e.RuianKod).HasColumnName("ruian_kod");
            entity.Property(e => e.ParcelNumber).HasColumnName("parcel_number").HasMaxLength(50);
            entity.Property(e => e.LvNumber).HasColumnName("lv_number").HasMaxLength(50);
            entity.Property(e => e.LandAreaM2).HasColumnName("land_area_m2");
            entity.Property(e => e.LandType).HasColumnName("land_type").HasMaxLength(100);
            entity.Property(e => e.OwnerType).HasColumnName("owner_type").HasMaxLength(50);
            entity.Property(e => e.EncumbrancesJson).HasColumnName("encumbrances").HasColumnType("jsonb");
            entity.Property(e => e.AddressSearched).HasColumnName("address_searched").IsRequired();
            entity.Property(e => e.CadastreUrl).HasColumnName("cadastre_url").HasMaxLength(500);
            entity.Property(e => e.FetchStatus).HasColumnName("fetch_status").HasMaxLength(20)
                .HasDefaultValue("pending");
            entity.Property(e => e.FetchError).HasColumnName("fetch_error");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at").HasColumnType("timestamptz");
            entity.Property(e => e.RawRuianJson).HasColumnName("raw_ruian").HasColumnType("jsonb");

            entity.HasOne(e => e.Listing)
                .WithMany()
                .HasForeignKey(e => e.ListingId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ListingId).IsUnique();
            entity.HasIndex(e => e.FetchStatus);
        });

        // =====================================================================
        // SpatialArea – pojmenované prostorové oblasti (koridory, polygony)
        // =====================================================================
        modelBuilder.Entity<SpatialArea>(entity =>
        {
            entity.ToTable("spatial_areas", "re_realestate");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.AreaType).HasColumnName("area_type").HasMaxLength(30);

            // geom je PostGIS geometrie – čteme jako WKT text přes raw SQL, EF mapuje na string
            entity.Property(e => e.GeomWkt).HasColumnName("geom").HasColumnType("geometry");

            entity.Property(e => e.StartCity).HasColumnName("start_city").HasMaxLength(100);
            entity.Property(e => e.EndCity).HasColumnName("end_city").HasMaxLength(100);
            entity.Property(e => e.BufferMeters).HasColumnName("buffer_m");
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasIndex(e => e.IsActive);
        });
    }
}
