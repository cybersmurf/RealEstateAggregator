# SQL Query Optimization

## Query Analysis Tools
- Use `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` to profile slow queries.
- Use `pg_stat_statements` to identify hot queries in production.

## Indexing Strategy
- **B-Tree indexes** for equality/range on high-cardinality columns (`price`, `created_at`).
- **GIN indexes** for full-text search (`search_tsv tsvector`), jsonb, and array columns.
- **GIST indexes** for spatial data (PostGIS geometry columns).
- **Composite indexes:** most selective column first.
- Avoid over-indexing – indexes slow down writes.

## Pagination
- **Offset pagination** (small datasets): `OFFSET (page-1)*size LIMIT size` + deterministic `ORDER BY id`.
- **Cursor pagination** (large datasets): `WHERE id > :last_id ORDER BY id LIMIT size`.
- Always include a tiebreaker: `ORDER BY created_at DESC, id ASC`.

## JOIN Optimization
- Prefer `INNER JOIN` over `LEFT JOIN` when nulls are not needed.
- Filter early: apply `WHERE` before joining when possible.
- Avoid `SELECT *` – project only needed columns.

## Aggregation
- Use conditional aggregation instead of multiple queries:
```sql
SELECT
  COUNT(*) FILTER (WHERE offer_type = 'Sale') AS sale_count,
  COUNT(*) FILTER (WHERE offer_type = 'Rent') AS rent_count
FROM re_realestate.listings;
```

## PostgreSQL Specifics
- Use `ILIKE` only for small datasets; prefer `@@` tsvector for production full-text.
- Use `pg_trgm` GIN index for `ILIKE '%...%'` patterns if necessary.
- Prefer `UPSERT` (`INSERT ... ON CONFLICT DO UPDATE`) over separate SELECT+INSERT.
- Use connection pooling (pgBouncer or Npgsql default pooling).
