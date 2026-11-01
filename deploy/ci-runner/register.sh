#!/usr/bin/env bash
# Self-hosted GitHub Actions runner in podman. See README.md in this folder.
#
#   ./register.sh          register the runner (first time only) and (re)start it
#   ./register.sh remove   unregister it from GitHub, delete its container and volume
#
# Env: REPO, RUNNER_NAME, RUNNER_IMAGE (defaults below).
#
# The registration/removal token is short-lived. It only ever lives in a pipe from `gh` to
# config.sh's stdin wrapper: never an env var, file or log line.
set -euo pipefail

REPO="${REPO:-nhanlagc60299/dotnet-starter-kit}"
NAME="${RUNNER_NAME:-podman-runner-1}"
IMAGE="${RUNNER_IMAGE:-ghcr.io/actions/actions-runner:2.338.2}"

# The runner drives the podman of the machine it runs on (the podman VM on macOS) through that
# machine's API socket, so `services:`, Testcontainers and `docker build` start sibling containers.
SOCK="$(podman info --format '{{.Host.RemoteSocket.Path}}')"
SOCK="${SOCK#unix://}"

podman volume exists "$NAME" || podman volume create "$NAME" >/dev/null
# The volume (runner install + work dir) is mounted at its own host path. Job containers
# (`container:` jobs, docker actions) get the work dir and the runner's externals bind-mounted
# by *host* path, so the runner must see them under the same path the podman host does.
DIR="$(podman volume inspect -f '{{.Mountpoint}}' "$NAME")"
# keep-id maps the host user that owns the podman socket and the volume to the image's `runner` user.
RUN=(--userns=keep-id:uid=1001,gid=1001 --security-opt label=disable -v "$NAME:$DIR" -w "$DIR")

config() { # $1 = token endpoint (registration-token | remove-token), rest = config.sh args
  local endpoint="$1"; shift
  gh api -X POST "repos/$REPO/actions/runners/$endpoint" --jq .token |
    podman run -i --rm "${RUN[@]}" -e ACTIONS_RUNNER_PRINT_LOG_TO_STDOUT= "$IMAGE" \
      bash -c 'read -r t; exec ./config.sh "$@" --token "$t"' config.sh "$@"
}

if [[ "${1:-}" == "remove" ]]; then
  podman rm -f "$NAME" >/dev/null 2>&1 || true
  config remove-token remove
  podman volume rm "$NAME" >/dev/null
  echo "Runner $NAME removed."
  exit 0
fi

podman pull -q "$IMAGE" >/dev/null
if ! podman run --rm "${RUN[@]}" "$IMAGE" test -f .runner; then
  podman run --rm "${RUN[@]}" "$IMAGE" cp -a /home/runner/. "$DIR/"
  # GitHub adds the self-hosted, Linux and CPU-arch (X64/ARM64) labels itself.
  config registration-token --unattended --replace --url "https://github.com/$REPO" \
    --name "$NAME" --labels podman --work _work
fi

podman rm -f "$NAME" >/dev/null 2>&1 || true
# --network host: ports published by service/Testcontainers/smoke containers land on the podman
# host, and `localhost` inside a job step must reach them.
# DOCKER_BUILDKIT=0: podman's Docker-compatible API has no BuildKit endpoint.
podman run -d --name "$NAME" --restart=always --network host "${RUN[@]}" \
  -v "$SOCK:/var/run/docker.sock" \
  -e DOCKER_HOST=unix:///var/run/docker.sock \
  -e DOCKER_BUILDKIT=0 \
  -e TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE="$SOCK" \
  -e TESTCONTAINERS_HOST_OVERRIDE=localhost \
  -e DOTNET_INSTALL_DIR="$DIR/_dotnet" \
  "$IMAGE" ./run.sh >/dev/null

echo "Runner $NAME started. Logs: podman logs -f $NAME"
