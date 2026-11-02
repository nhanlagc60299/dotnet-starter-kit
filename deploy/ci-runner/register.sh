#!/usr/bin/env bash
# Self-hosted GitHub Actions runner in podman, with its own nested podman engine. See README.md.
#
#   ./register.sh          build the image, register the runner (first time only), (re)start it
#   ./register.sh remove   unregister it from GitHub, delete its container and volumes
#
# Env: REPO, RUNNER_NAME (defaults below).
#
# The registration/removal token is short-lived. It goes from `gh` through a pipe into the
# environment of config.sh only: never argv, a file, a log line or the container's config.
set -euo pipefail

REPO="${REPO:-nhanlagc60299/dotnet-starter-kit}"
NAME="${RUNNER_NAME:-podman-runner-1}"
IMAGE="localhost/fsh-ci-runner:2.338.2" # keep the tag in step with the Dockerfile's FROM

# Two named volumes, nothing else from the host: the runner's home (install, credentials, _work,
# caches; filled from the image on first use) and the nested engine's image/container storage.
VOLS=(-v "$NAME:/home/runner" -v "$NAME-storage:/var/lib/containers")

config() { # $1 = token endpoint (registration-token | remove-token), rest = config.sh args
  local endpoint="$1"; shift
  gh api -X POST "repos/$REPO/actions/runners/$endpoint" --jq .token |
    podman run -i --rm --user runner --entrypoint /bin/bash -w /home/runner "${VOLS[@]}" \
      -e ACTIONS_RUNNER_PRINT_LOG_TO_STDOUT= "$IMAGE" \
      -c 'read -r t; ACTIONS_RUNNER_INPUT_TOKEN="$t" exec ./config.sh "$@"' config.sh "$@"
}

podman build -q -t "$IMAGE" "$(dirname "$0")" >/dev/null

if [[ "${1:-}" == "remove" ]]; then
  podman stop -t 60 "$NAME" >/dev/null 2>&1 || true # graceful: the runner deletes its session
  podman rm -f "$NAME" >/dev/null 2>&1 || true
  config remove-token remove
  podman volume rm "$NAME" "$NAME-storage" >/dev/null
  echo "Runner $NAME removed."
  exit 0
fi

if ! podman run --rm --user runner --entrypoint test -w /home/runner "${VOLS[@]}" "$IMAGE" -f .runner; then
  # GitHub adds the self-hosted, Linux and CPU-arch (X64/ARM64) labels itself.
  config registration-token --unattended --replace --url "https://github.com/$REPO" \
    --name "$NAME" --labels podman --work _work
fi

podman stop -t 60 "$NAME" >/dev/null 2>&1 || true # graceful: the runner deletes its session
podman rm -f "$NAME" >/dev/null 2>&1 || true
# --privileged lets the nested engine run containers. It stays inside this VM user's rootless
# user namespace and gets no host paths and no host socket.
# DOCKER_BUILDKIT=0: podman's Docker-compatible API has no BuildKit endpoint.
podman run -d --name "$NAME" --restart=always --privileged "${VOLS[@]}" \
  -e DOCKER_HOST=unix:///var/run/docker.sock \
  -e DOCKER_BUILDKIT=0 \
  -e TESTCONTAINERS_HOST_OVERRIDE=localhost \
  -e DOTNET_INSTALL_DIR=/home/runner/.dotnet \
  "$IMAGE" >/dev/null

echo "Runner $NAME started. Logs: podman logs -f $NAME"
