#!/bin/sh
# Create / refresh the app_migrate, app_runtime and app_readonly roles and their grants.
# Safe to run any number of times (idempotent). Needs psql and a SUPERUSER connection:
#   PGHOST PGUSER PGPASSWORD PGDATABASE (standard libpq variables)
# plus APP_MIGRATE_PASSWORD, APP_RUNTIME_PASSWORD, APP_READONLY_PASSWORD.
#
# The compose `db-roles` service runs it on every deploy, before the migrator. By hand, from the
# Dokploy Terminal of the `postgres` container (local socket as the bootstrap superuser):
#   PGUSER=fsh PGDATABASE=fsh APP_MIGRATE_PASSWORD=... APP_RUNTIME_PASSWORD=... \
#   APP_READONLY_PASSWORD=... sh /path/to/db-roles.sh
set -eu

: "${APP_MIGRATE_PASSWORD:?APP_MIGRATE_PASSWORD is required}"
: "${APP_RUNTIME_PASSWORD:?APP_RUNTIME_PASSWORD is required}"
: "${APP_READONLY_PASSWORD:?APP_READONLY_PASSWORD is required}"

here=$(cd "$(dirname "$0")" && pwd)
exec psql -X -q -v ON_ERROR_STOP=1 -f "$here/../postgres-init/01-roles.sql"
