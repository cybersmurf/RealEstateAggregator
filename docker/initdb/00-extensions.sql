-- ============================================================================
-- Bootstrap čisté databáze – běží POUZE při prvním startu (prázdný volume).
-- ============================================================================
-- Záměrně obsahuje jen extensions a schéma. Tabulky vytváří aplikace
-- (EnsureCreatedAsync + DbInitializer), aby existoval jediný zdroj pravdy.
--
-- POZOR: docker-entrypoint-initdb.d pouští VŠECHNY .sql abecedně s ON_ERROR_STOP=1.
-- Dřív se sem mountoval celý ./scripts, takže backfill_condition.sql běžel jako první,
-- spadl na neexistující tabulce a init se přerušil dřív, než došlo na init-db.sql.
-- Do tohoto adresáře proto nepatří nic, co předpokládá existující schéma.
-- ============================================================================

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS vector;    -- pgvector – sémantické vyhledávání (768 dim)
CREATE EXTENSION IF NOT EXISTS postgis;   -- PostGIS – ST_Buffer, ST_Intersects, koridor podél trasy

CREATE SCHEMA IF NOT EXISTS re_realestate;
