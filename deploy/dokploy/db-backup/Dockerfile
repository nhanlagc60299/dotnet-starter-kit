# Postgres backup / restore-verify sidecar. Same major as the database, so pg_dump never talks
# to a newer server than itself. busybox crond (already in Alpine) is the scheduler.
FROM postgres:18-alpine

RUN apk add --no-cache rclone

COPY common.sh /usr/local/lib/backup-common.sh
COPY backup.sh restore-verify.sh entrypoint.sh /usr/local/bin/
RUN chmod 0755 /usr/local/bin/backup.sh /usr/local/bin/restore-verify.sh /usr/local/bin/entrypoint.sh

# Root only for crond; both scripts drop to the `postgres` user themselves (initdb refuses root).
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
