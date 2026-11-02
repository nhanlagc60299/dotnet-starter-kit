# Self-hosted CI runner (podman)

All GitHub Actions workflows in this repo run on `runs-on: [self-hosted, linux, podman]`. The
runner is a podman container built from `Dockerfile` here: the official
`ghcr.io/actions/actions-runner` image plus **its own podman engine**. On macOS it lives in the
podman machine VM (Linux, ARM64 on Apple silicon).

```
GitHub ──job──> runner container (--privileged, rootless in the VM)
                ├─ runner (user `runner`) ── docker CLI ──> /var/run/docker.sock
                └─ nested podman engine (root of this container) ──> job containers
                   storage: volume <name>-storage          (services:, container:, Testcontainers,
                                                             docker build/run, PublishContainer)
```

- **Nested engine, not the host's.** The entrypoint starts `podman system service` inside the
  runner container and exposes it at `/var/run/docker.sock` (`DOCKER_HOST`). Jobs see only the
  containers they start themselves; the VM's own podman, its containers and its socket are out of
  reach.
- **Only named volumes are mounted:** `<name>` → `/home/runner` (runner install, credentials,
  `_work`, .NET SDK, NuGet/npm/Playwright caches) and `<name>-storage` → `/var/lib/containers`
  (the nested engine's images and containers). No host path is bind-mounted.
- Published ports land in the runner container's own network namespace, so `localhost:5432` in a
  step reaches a service container, as on GitHub-hosted runners.
- Nested-engine quirks the image handles (see `containers.conf`, `crun-ci`, `entrypoint.sh`):
  its own subnets (the VM's engine uses 10.88/10.89), `route_localnet` for localhost port
  forwarding, a crun wrapper that drops `oomScoreAdj` (the container may not lower the 200 it
  inherits), `nofile=65536` (build steps default to 1024, which breaks Vite builds at random),
  and a 2-second healthcheck loop because there is no systemd to schedule them.
- `--restart=always`: podman restarts the runner (and its engine) if either dies.
- Labels: `self-hosted`, `Linux`, `ARM64` (or `X64`) are added by GitHub; the script adds `podman`.

## Install

Needs podman (machine running on macOS) and `gh` logged in as a repo admin.

```bash
deploy/ci-runner/register.sh
```

It builds `localhost/fsh-ci-runner:<version>`, asks GitHub for a short-lived registration token
(`gh api -X POST repos/<repo>/actions/runners/registration-token`), registers the runner on first
use and starts it. The token travels from `gh` through a pipe into `config.sh`'s environment
(`ACTIONS_RUNNER_INPUT_TOKEN`) — never argv, a file, a log or the repo; the runner masks it as
`***` in its own logs. Re-running the script rebuilds the image and restarts the runner.

Overrides: `REPO=owner/name`, `RUNNER_NAME=podman-runner-2`.

**Survive a VM reboot** (`podman machine stop/start`): `--restart=always` only covers crashes. Once:

```bash
podman machine ssh systemctl --user enable podman-restart.service
```

(This brings back *every* container with a restart policy on that VM, not just the runner.)

## Operate

| What | Command |
|---|---|
| Status on GitHub | `gh api repos/nhanlagc60299/dotnet-starter-kit/actions/runners` |
| Logs | `podman logs -f podman-runner-1` |
| Stop / start | `podman stop podman-runner-1` / `deploy/ci-runner/register.sh` |
| Remove (`config.sh remove` + delete container and both volumes) | `deploy/ci-runner/register.sh remove` |
| What the nested engine holds | `podman exec podman-runner-1 podman system df` |
| Free disk (only the runner's own engine) | `podman exec podman-runner-1 podman system prune -af` |

**Disk budget.** Expect ~10 GB for one runner: the image (~2 GB), the home volume (.NET SDK,
NuGet/npm/Playwright caches, `_work`; ~4 GB) and the nested storage volume (postgres, node,
nginx, Testcontainers and built images; ~3–4 GB). The nested engine does not share images with
the VM's engine, so everything is pulled once more. Check with `podman machine ssh df -h /var`.

**Upgrade.** The runner updates itself inside its home volume when GitHub ships a new version
(GitHub refuses runners that fall too far behind). For a new base image: bump the `FROM` tag in
`Dockerfile` and `IMAGE` in `register.sh`, then `register.sh remove` and `register.sh` (the home
volume is filled from the image only when it is empty).

**Parallel runners.** One runner runs one job at a time, so a workflow's jobs queue up. For more
throughput run `RUNNER_NAME=podman-runner-2 deploy/ci-runner/register.sh`. Each runner has its own
engine and network namespace, so ports and container names don't collide; the limit is the VM's
CPU/RAM/disk (4 CPU / 8 GiB fits one comfortably, two at a squeeze).

## Security (public repo)

A self-hosted runner executes whatever a workflow tells it to — including any npm or NuGet
package a same-repo PR pulls in.

**Why the engine is nested.** The first version mounted the VM's podman socket into the runner.
That gave every job full control of the VM's podman, and podman machine mounts the Mac's
`/Users`, `/private` and `/var/folders` read-write into the VM. One `docker run -v /Users:/u ...`
from any job — or from a malicious dependency in its install step — could read and write
`~/.ssh`, `~/.config/gh`, shell rc files and every other project on the Mac, and could start,
stop or inspect the VM's other containers. Now jobs talk to an engine that runs inside the runner
container: `-v /Users:/u` mounts an empty directory, `docker ps` lists only the job's own
containers, and no host path or host socket is mounted anywhere.

**Fork pull requests never run here.**
1. Each job a `pull_request` can start only runs when
   `github.event.pull_request.head.repo.full_name == github.repository` (the `changes` job, which
   everything else `needs`, plus CodeQL and template smoke). For a fork PR the `Backend CI` /
   `Frontend CI` gate runs on a GitHub-hosted VM, checks nothing out and **fails**, so a fork PR
   can't be merged green — once Backend CI / Frontend CI are required status checks (`main` has
   no branch protection yet).
2. A fork PR could edit the workflow and drop that guard, so the repo setting is the real gate:
   **Settings → Actions → General → "Require approval for all external contributors"** (enabled).
   Read any change under `.github/` before approving a run.
3. To test a contributor's change: review it, push it to a branch of this repo, open a PR from there.
4. Never use `pull_request_target` with a checkout of the PR head on this runner.

**What remains.**
- **Privilege.** Jobs are effectively root inside the runner container: `runner` is in the `sudo`
  group (passwordless, from the base image). The container is `--privileged` (needed for the
  nested engine): seccomp is off and `/dev/kvm` and `/dev/vsock` are open. Its root maps to the VM
  user `core` (uid 501), and `core` has passwordless sudo in the VM. So a kernel or
  container-runtime escape lands as VM root, and from there reaches the VM's podman, its other
  containers and the Mac's `/Users` mount. Keep the VM patched (`podman machine` updates); a
  dedicated VM for CI is the real fix.
- **Network reach.** The runner sits on the VM's podman bridge (10.88.0.x, gateway 10.88.0.1).
  Jobs can reach every port the VM publishes (today the aicms stack on 8000, 5080, 3000 and 3001),
  and services on the Mac's localhost through `host.containers.internal` (192.168.127.254). That
  is no worse than the first version's `--network host`, but it is real: don't run sensitive
  local services (databases with real data, admin consoles, dev servers holding credentials) while
  CI runs, or give CI its own VM.
- The runner is **persistent**: a bad job can leave files in the home volume or images in the
  nested engine that later jobs — including `main`/tag jobs with `NUGET_API_KEY` and a
  `packages: write` token — then pick up. `actions/checkout` cleans the workspace, but not
  `~/.nuget`, `~/.npm` or the engine. A job can also read `/home/runner/.credentials` (and the
  RSA key next to it) and impersonate this runner to GitHub — taking later jobs, secrets
  included, until the runner is removed. Future hardening: register with `--ephemeral` and have
  `register.sh` recreate both volumes after every job (one job per runner lifetime, credentials
  good for one job), and keep the publish jobs on a separate runner or on GitHub-hosted VMs.
