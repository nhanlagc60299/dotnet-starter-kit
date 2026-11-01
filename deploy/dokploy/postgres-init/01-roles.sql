-- Roles, schemas, extensions and grants for the production database.
--
-- Idempotent. Runs twice per deploy (see scripts/db-roles.sh): `db-roles` before the migrator
-- (creates roles, so the migrator can log in) and `db-grants` after it (grants on the schemas and
-- tables the migrator just created). Run as a superuser connected to the application database.
-- Passwords come from the environment (APP_MIGRATE_PASSWORD, APP_RUNTIME_PASSWORD,
-- APP_READONLY_PASSWORD), never from this file.
--
--   app_migrate   owns the database and every module schema, runs the EF migrations. DDL.
--   app_runtime   used by the API: DML only. Owns schema `hangfire` (Hangfire.PostgreSql migrates
--                 its own schema at startup). Append-only on schema `audit`.
--   app_readonly  SELECT everywhere (tables and sequences), for reporting / support and pg_dump.
--
-- Module schemas are discovered, not listed: every schema owned by app_migrate. A new module needs
-- no change here.

\set ON_ERROR_STOP on

-- SECURITY: this file runs as a superuser, but app_migrate owns `public` and the database and can
-- plant same-named functions/operators there (or ALTER DATABASE ... SET search_path). Pin the
-- search path before anything is resolved so only pg_catalog is trusted. Keep every later
-- statement schema-safe: no unqualified names outside pg_catalog.
SET search_path = pg_catalog, pg_temp;
-- Re-runs are expected: do not print "already exists, skipping" for every object.
SET client_min_messages = warning;

\getenv migrate_pw APP_MIGRATE_PASSWORD
\getenv runtime_pw APP_RUNTIME_PASSWORD
\getenv readonly_pw APP_READONLY_PASSWORD

SELECT current_database() AS db \gset

-- ── Extensions (superuser only; app_migrate cannot create untrusted ones) ──
CREATE EXTENSION IF NOT EXISTS pgcrypto SCHEMA public;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp" SCHEMA public;
CREATE EXTENSION IF NOT EXISTS pg_trgm SCHEMA public;
CREATE EXTENSION IF NOT EXISTS unaccent SCHEMA public;

-- ── Roles ──
SELECT format('CREATE ROLE %I', r) FROM pg_catalog.unnest(ARRAY['app_migrate', 'app_runtime', 'app_readonly']) AS r
WHERE NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = r) \gexec

ALTER ROLE app_migrate  LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'migrate_pw';
ALTER ROLE app_runtime  LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'runtime_pw';
ALTER ROLE app_readonly LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'readonly_pw';

-- ── Database ──
ALTER DATABASE :"db" OWNER TO app_migrate;
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE :"db" TO app_runtime, app_readonly;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO app_runtime, app_readonly;

-- ── Fixed schemas ──
-- audit exists up front so its append-only defaults are in place before the first migration.
CREATE SCHEMA IF NOT EXISTS audit AUTHORIZATION app_migrate;
-- hangfire: owned by app_runtime because Hangfire creates and migrates its tables itself.
CREATE SCHEMA IF NOT EXISTS hangfire AUTHORIZATION app_runtime;
ALTER SCHEMA hangfire OWNER TO app_runtime;
GRANT USAGE ON SCHEMA hangfire TO app_readonly;

-- ── Module schemas: whatever app_migrate owns right now ──
CREATE TEMP TABLE _module_schemas AS
SELECT nspname::text AS name
FROM pg_catalog.pg_namespace
WHERE nspowner = 'app_migrate'::regrole
  AND nspname !~ '^pg_'
  AND nspname NOT IN ('information_schema', 'public', 'hangfire');

SELECT format('GRANT USAGE ON SCHEMA %I TO app_runtime, app_readonly', name) FROM pg_temp._module_schemas \gexec

-- ── Grants on what exists now ──
-- Everything except audit: full DML for app_runtime.
SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO app_runtime', name)
FROM pg_temp._module_schemas WHERE name <> 'audit' \gexec
SELECT format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO app_runtime', name)
FROM pg_temp._module_schemas \gexec
SELECT format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO app_readonly', name) FROM pg_temp._module_schemas \gexec
-- SELECT on sequences too: pg_dump reads every sequence's value, and the backup runs as app_readonly.
SELECT format('GRANT SELECT ON ALL SEQUENCES IN SCHEMA %I TO app_readonly', name) FROM pg_temp._module_schemas \gexec
GRANT SELECT ON ALL TABLES IN SCHEMA hangfire TO app_readonly;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA hangfire TO app_readonly;

-- EF keeps one shared __EFMigrationsHistory in `public`; the API's readiness check reads it.
GRANT SELECT ON ALL TABLES IN SCHEMA public TO app_runtime, app_readonly;

-- Audit is append-only for the runtime role: insert and read, never change or remove.
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA audit TO app_runtime;
REVOKE UPDATE, DELETE, TRUNCATE ON ALL TABLES IN SCHEMA audit FROM app_runtime;

-- ── Defaults for tables the migrator creates later ──
SELECT format(
    'ALTER DEFAULT PRIVILEGES FOR ROLE app_migrate IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_runtime',
    name)
FROM pg_temp._module_schemas WHERE name <> 'audit' \gexec
SELECT format(
    'ALTER DEFAULT PRIVILEGES FOR ROLE app_migrate IN SCHEMA %I GRANT USAGE, SELECT ON SEQUENCES TO app_runtime',
    name)
FROM pg_temp._module_schemas \gexec
SELECT format(
    'ALTER DEFAULT PRIVILEGES FOR ROLE app_migrate IN SCHEMA %I GRANT SELECT ON TABLES TO app_readonly',
    name)
FROM pg_temp._module_schemas \gexec
SELECT format(
    'ALTER DEFAULT PRIVILEGES FOR ROLE app_migrate IN SCHEMA %I GRANT SELECT ON SEQUENCES TO app_readonly',
    name)
FROM pg_temp._module_schemas \gexec

ALTER DEFAULT PRIVILEGES FOR ROLE app_migrate IN SCHEMA audit GRANT SELECT, INSERT ON TABLES TO app_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE app_migrate IN SCHEMA audit REVOKE UPDATE, DELETE, TRUNCATE ON TABLES FROM app_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE app_migrate IN SCHEMA public GRANT SELECT ON TABLES TO app_runtime, app_readonly;

-- Hangfire tables are created by app_runtime.
ALTER DEFAULT PRIVILEGES FOR ROLE app_runtime IN SCHEMA hangfire GRANT SELECT ON TABLES TO app_readonly;
ALTER DEFAULT PRIVILEGES FOR ROLE app_runtime IN SCHEMA hangfire GRANT SELECT ON SEQUENCES TO app_readonly;
