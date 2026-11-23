-- Initializes least-privilege runtime vs migration PostgreSQL roles (M0.2 / M0.8).
-- Passwords here are for local dev/demo containers only; override via secrets in staging/pilot.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexus_migrator') THEN
        CREATE ROLE nexus_migrator LOGIN PASSWORD 'nexus_migrator_dev_only';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexus_runtime') THEN
        CREATE ROLE nexus_runtime LOGIN PASSWORD 'nexus_runtime_dev_only';
    END IF;
END
$$;

CREATE EXTENSION IF NOT EXISTS unaccent;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO nexus_migrator;
GRANT USAGE ON SCHEMA public TO nexus_runtime;

ALTER DEFAULT PRIVILEGES FOR ROLE nexus_migrator IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nexus_runtime;

ALTER DEFAULT PRIVILEGES FOR ROLE nexus_migrator IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO nexus_runtime;

ALTER DEFAULT PRIVILEGES FOR ROLE nexus_migrator IN SCHEMA public
    GRANT EXECUTE ON FUNCTIONS TO nexus_runtime;
