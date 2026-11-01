#!/bin/sh
# Dump the database (custom format) as app_readonly and copy it to the S3 remote, with a
# .sha256 file uploaded last (its presence marks a complete backup). Never deletes anything on
# the remote: retention is the bucket's lifecycle rule (see README).
set -eu
[ "$(id -u)" -ne 0 ] || exec gosu postgres "$0" "$@"
. /usr/local/lib/backup-common.sh

mkdir -p "$TMPDIR"
work=$(mktemp -d "$TMPDIR/backup.XXXXXX")
trap 'rm -rf "$work"' EXIT

name="fsh-$(date -u +%Y%m%dT%H%M%SZ).dump"
echo "backup: dumping ${PGDATABASE:-} as ${PGUSER:-} to $name"
pg_dump -Fc -f "$work/$name"
pg_restore -l "$work/$name" > /dev/null   # the archive must at least be readable
(cd "$work" && sha256sum "$name" > "$name.sha256")

echo "backup: uploading $(wc -c < "$work/$name") bytes to $REMOTE"
rclone copyto "$work/$name" "$REMOTE/$name"
rclone copyto "$work/$name.sha256" "$REMOTE/$name.sha256"

echo "backup: ok $name"
ping_ok "${BACKUP_PING_URL:-}"
