# PostgreSQL 15 + PostGIS 3.4 + pgvector
# Kombinovaný obraz pro prostorové dotazy (PostGIS) + sémantické vyhledávání (pgvector)
FROM postgis/postgis:15-3.4

# Přidej pgvector z apt (Debian Bookworm + PGDG repo)
RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-15-pgvector \
    && rm -rf /var/lib/apt/lists/*
