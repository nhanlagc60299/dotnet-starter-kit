#!/bin/sh
# Prove the newest backup restores: download it, check its sha256, restore it into a throwaway
# cluster (own data dir, unix socket only: never touches the production database) and run
# sanity queries. Exits non-zero on any failure.
set -eu
[ "$(id -u)" -ne 0 ] || exec gosu postgres "$0" "$@"
. /usr/local/lib/backup-common.sh

mkdir -p "$TMPDIR"
work=$(mktemp -d "$TMPDIR/verify.XXXXXX")
reported=
cleanup() {
    rc=$?
    pg_ctl -D "$work/pgdata" -m immediate stop > /dev/null 2>&1 || true
    rm -rf "$work"
    # An unexpected failure (set -e) still gets a FAILED line.
    [ "$rc" -eq 0 ] || [ -n "$reported" ] || echo "restore-verify: FAILED (exit $rc)" >&2
}
trap cleanup EXIT
fail() { reported=1; echo "restore-verify: FAILED: $*" >&2; exit 1; }

# The .sha256 is uploaded last, so listing it finds only complete backups. UTC timestamps sort.
# (captured first: sh has no pipefail, a failed listing must not look like "no backups")
listing=$(rclone lsf "$REMOTE" --include 'fsh-*.dump.sha256') || fail "cannot list $REMOTE"
latest=$(printf '%s\n' "$listing" | sort | tail -n 1)
[ -n "$latest" ] || fail "no backup found under $REMOTE"
dump=${latest%.sha256}

# A restore test of a stale backup proves nothing about today: if the daily backups have been
# failing silently, the newest dump is old and this must fail too.
max_age=${RESTORE_VERIFY_MAX_AGE_HOURS:-72}
case $max_age in ''|*[!0-9]*) fail "RESTORE_VERIFY_MAX_AGE_HOURS must be a whole number, got '$max_age'" ;; esac
iso=$(printf '%s' "$dump" | sed -En 's/^fsh-([0-9]{4})([0-9]{2})([0-9]{2})T([0-9]{2})([0-9]{2})([0-9]{2})Z\.dump$/\1-\2-\3 \4:\5:\6/p')
[ -n "$iso" ] || fail "cannot read a UTC timestamp from the name '$dump'"
made=$(date -u -d "$iso" +%s) || fail "cannot parse timestamp '$iso' of $dump"
age=$(( ($(date -u +%s) - made) / 3600 ))
echo "restore-verify: newest backup is $dump ($age h old, limit $max_age h)"
[ "$age" -le "$max_age" ] || fail "newest backup $dump is $age h old (limit $max_age h): daily backups are not landing"

rclone copyto "$REMOTE/$dump" "$work/$dump"
rclone copyto "$REMOTE/$latest" "$work/$latest"
(cd "$work" && sha256sum -c "$latest") || fail "sha256 mismatch for $dump"

# fsync=off: this cluster is disposable. No TCP at all: socket in $work.
initdb -D "$work/pgdata" -U postgres -A trust > /dev/null
pg_ctl -D "$work/pgdata" -w -l "$work/pg.log" \
    -o "-c listen_addresses= -c unix_socket_directories=$work -c fsync=off -c port=55432" start > /dev/null
# Never inherit the production connection settings from the container environment.
unset PGHOST PGPASSWORD PGUSER PGDATABASE
export PGHOST="$work" PGPORT=55432 PGUSER=postgres
createdb verify

# Roles of the source cluster do not exist here, hence no owners/ACLs. Stop at the first error.
pg_restore --no-owner --no-privileges --exit-on-error -d verify "$work/$dump" ||
    fail "pg_restore of $dump"

q() { psql -X -At -v ON_ERROR_STOP=1 -d verify -c "$1"; }
echo "restore-verify: tables per schema"
q "SELECT n.nspname || ': ' || count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
   WHERE c.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema')
   GROUP BY n.nspname ORDER BY n.nspname"
users=$(q 'SELECT count(*) FROM identity."Users"') || fail "cannot read identity.Users"
root=$(q "SELECT count(*) FROM tenant.\"Tenants\" WHERE \"Identifier\" = 'root'") || fail "cannot read tenant.Tenants"
echo "restore-verify: identity.Users rows: $users"
echo "restore-verify: tenant 'root' present: $root"
[ "$users" -gt 0 ] || fail "identity.Users is empty"
[ "$root" -eq 1 ] || fail "tenant 'root' is missing"

echo "restore-verify: ok $dump"
ping_ok "${RESTORE_VERIFY_PING_URL:-}"
