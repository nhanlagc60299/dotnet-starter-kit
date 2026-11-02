#!/bin/sh
# Container entry: validate the configuration, install the crontab, run crond in the foreground.
set -eu
. /usr/local/lib/backup-common.sh

BACKUP_CRON=${BACKUP_CRON:-0 2 * * *}
RESTORE_VERIFY_CRON=${RESTORE_VERIFY_CRON:-0 4 1 * *}
set -f   # the schedules contain `*`
for _s in "$BACKUP_CRON" "$RESTORE_VERIFY_CRON"; do
    set -- $_s
    [ $# -eq 5 ] || { echo "backup: bad cron schedule '$_s' (need 5 fields)" >&2; exit 1; }
done
set +f

# Leftovers of a run that was killed with the container.
rm -rf "$TMPDIR"/*
mkdir -p "$TMPDIR"
chown postgres:postgres "$TMPDIR"

# Job output goes to the container log (PID 1's stdout) instead of crond's mail fallback.
cat > /etc/crontabs/root <<CRON
$BACKUP_CRON /usr/local/bin/backup.sh > /proc/1/fd/1 2>&1
$RESTORE_VERIFY_CRON /usr/local/bin/restore-verify.sh > /proc/1/fd/1 2>&1
CRON
echo "backup: scheduled backup '$BACKUP_CRON', restore-verify '$RESTORE_VERIFY_CRON' (UTC) to $REMOTE"
exec crond -f -L /dev/stdout
