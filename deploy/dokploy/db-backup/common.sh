# Sourced by backup.sh, restore-verify.sh and entrypoint.sh. Fails closed: a missing setting
# stops the caller with a message instead of letting a backup "succeed" into nowhere.

for _v in BACKUP_S3_ENDPOINT BACKUP_S3_BUCKET BACKUP_S3_ACCESS_KEY BACKUP_S3_SECRET_KEY; do
    eval "_val=\${$_v:-}"
    if [ -z "$_val" ]; then
        echo "backup: $_v is not set. Backups are mandatory: set BACKUP_S3_ENDPOINT, BACKUP_S3_BUCKET," \
            "BACKUP_S3_ACCESS_KEY and BACKUP_S3_SECRET_KEY (see deploy/dokploy/.env.example)." >&2
        exit 1
    fi
done

# The S3 remote is built from the environment only: no rclone.conf, nothing on disk.
# Provider "Cloudflare" is R2; BACKUP_S3_PROVIDER=Other for any other S3-compatible store.
export RCLONE_S3_PROVIDER="${BACKUP_S3_PROVIDER:-Cloudflare}"
export RCLONE_S3_ENDPOINT="$BACKUP_S3_ENDPOINT"
export RCLONE_S3_ACCESS_KEY_ID="$BACKUP_S3_ACCESS_KEY"
export RCLONE_S3_SECRET_ACCESS_KEY="$BACKUP_S3_SECRET_KEY"
# An object-scoped R2 token cannot create buckets; the bucket must already exist.
export RCLONE_S3_NO_CHECK_BUCKET=true
export RCLONE_CONFIG=/dev/null

_prefix=$(printf '%s' "${BACKUP_S3_PREFIX:-fsh}" | sed 's|^/*||; s|/*$||')
REMOTE=":s3:$BACKUP_S3_BUCKET${_prefix:+/$_prefix}"
export REMOTE

# Scratch space: a named volume in compose, so a big dump never fills the container layer.
export TMPDIR="${BACKUP_TMPDIR:-/var/lib/postgresql/work}"

# Optional push monitor (Uptime Kuma): `ping_ok <url>` after a run succeeded, so a missing
# heartbeat is the alarm for a failed or never-started run. An empty URL is a no-op.
ping_ok() {
    [ -z "${1:-}" ] || wget -q -T 15 -O /dev/null "$1" || echo "warning: heartbeat ping failed" >&2
}
