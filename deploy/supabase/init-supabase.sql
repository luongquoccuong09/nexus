-- Project Nexus — One-time Supabase PostgreSQL initialization script
-- Run this once in Supabase Dashboard -> SQL Editor (as postgres) BEFORE running `dotnet run -- --migrate`.
--
-- IMPORTANT:
-- 1. Replace BOTH strong placeholder passwords below before running!
-- 2. Always connect Nexus via Supabase's **Session Pooler on port 5432** (NEVER Transaction Pooler on port 6543).
-- 3. In Supabase Session Pooler connection strings, usernames take the format:
--      nexus_migrator.<your-project-ref>
--      nexus_runtime.<your-project-ref>

-- 1. Install Vietnamese search extensions explicitly into schema `public`
--    (required by M1b `public.immutable_unaccent(text)` and GIN `pg_trgm` indexes).
CREATE EXTENSION IF NOT EXISTS unaccent WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;

-- 2. Create separated migrator (DDL) and runtime (DML-only) roles
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexus_migrator') THEN
        CREATE ROLE nexus_migrator LOGIN PASSWORD 'REPLACE_WITH_STRONG_MIGRATOR_PASSWORD';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexus_runtime') THEN
        CREATE ROLE nexus_runtime LOGIN PASSWORD 'REPLACE_WITH_STRONG_RUNTIME_PASSWORD';
    END IF;
END
$$;

-- 3. Allow managed `postgres` role on Supabase (PostgreSQL 15+) to configure default privileges for nexus_migrator
GRANT nexus_migrator TO postgres;

-- 4. Grant schema DDL privileges strictly to nexus_migrator
GRANT USAGE, CREATE ON SCHEMA public TO nexus_migrator;

-- 5. Grant least-privilege runtime access to nexus_runtime (no CREATE / DROP / ALTER)
GRANT USAGE ON SCHEMA public TO nexus_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO nexus_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO nexus_runtime;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO nexus_runtime;

-- Ensure future tables/sequences/functions created by nexus_migrator are usable by nexus_runtime
ALTER DEFAULT PRIVILEGES FOR ROLE nexus_migrator IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nexus_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE nexus_migrator IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO nexus_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE nexus_migrator IN SCHEMA public
    GRANT EXECUTE ON FUNCTIONS TO nexus_runtime;
