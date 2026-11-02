#!/usr/bin/env bash
# Starts the nested podman engine as this container's root, exposes it as the Docker socket, then
# runs the runner as the unprivileged `runner` user. If either process exits, the container exits
# and `--restart=always` brings both back.
set -euo pipefail

# Published ports are DNAT-ed from localhost; that needs route_localnet in this network namespace.
sysctl -qw net.ipv4.conf.all.route_localnet=1

rm -f /var/run/docker.sock
podman system service --time=0 unix:///var/run/docker.sock &
for _ in $(seq 100); do [[ -S /var/run/docker.sock ]] && break; sleep 0.1; done
chgrp docker /var/run/docker.sock
chmod 660 /var/run/docker.sock

# Podman schedules healthchecks with systemd timers; there is no systemd here, so `services:` with
# --health-cmd would stay "starting" forever. Run every container's healthcheck ourselves.
# ponytail: fixed 2s cadence for all containers, ignores --health-interval; fine for CI services.
while sleep 2; do
  for c in $(podman ps -q --filter health=starting --filter health=healthy --filter health=unhealthy); do
    podman healthcheck run "$c" >/dev/null 2>&1 || true
  done
done &

cd /home/runner
setpriv --reuid=runner --regid=runner --init-groups env HOME=/home/runner USER=runner "$@" &

trap 'kill -TERM $(jobs -p) 2>/dev/null' TERM INT
wait -n
