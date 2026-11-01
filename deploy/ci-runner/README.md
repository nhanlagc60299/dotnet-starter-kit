# Self-hosted CI runner (podman)

All GitHub Actions workflows in this repo run on `runs-on: [self-hosted, linux, podman]`, served by
the official runner image (`ghcr.io/actions/actions-runner`) in a podman container. On macOS that
container lives in the podman machine VM (Linux, ARM64 on Apple silicon).

```
GitHub ──job──> runner container ──/var/run/docker.sock──> podman (the VM) ──> sibling containers
                (docker CLI)                                                  (services:, Testcontainers,
                                                                               docker build/run)
```

- **docker CLI → podman.** The VM's podman API socket is mounted at `/var/run/docker.sock`
  (`DOCKER_HOST`), so `services:`, `container:` jobs, Testcontainers, `docker build` and
  `dotnet publish /t:PublishContainer` all start containers next to the runner.
- **Host network.** Ports those containers publish land on the VM; the runner shares the VM's
  network, so `localhost:5432` in a step reaches the service container.
- **One volume per runner** (`podman-runner-1`) holds the runner install, its credentials and the
  `_work` dir. It is mounted at its own VM path so `container:` jobs, which bind-mount the work dir
  by host path, see the same files.
- `--restart=always`: podman restarts the runner if it dies.
- Labels: `self-hosted`, `Linux`, `ARM64` (or `X64`) are added by GitHub; the script adds `podman`.

## Install

Needs podman (machine running on macOS) and `gh` logged in as a repo admin.

```bash
deploy/ci-runner/register.sh
```

It asks GitHub for a short-lived registration token (`gh api -X POST
repos/<repo>/actions/runners/registration-token`), registers the runner on first use, then starts
it. The token goes from `gh` straight into `config.sh` over a pipe: it is never put in an env var,
a file, a log or the repo. Re-running the script just restarts the runner.

Overrides: `REPO=owner/name`, `RUNNER_NAME=podman-runner-2`, `RUNNER_IMAGE=ghcr.io/actions/actions-runner:<ver>`.

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
| Remove (`config.sh remove` + delete container and volume) | `deploy/ci-runner/register.sh remove` |
| Free disk | `podman image prune -f` (CI builds images; the workflows delete their own tags, not layers) |

**Upgrade.** The runner updates itself in its volume when GitHub ships a new version (GitHub
refuses runners that fall too far behind). To move to a new image (newer docker CLI, OS
packages): `register.sh remove`, bump `RUNNER_IMAGE` in `register.sh`, run `register.sh` again.

**Parallel runners.** One runner runs one job at a time, so a workflow's jobs queue up. For more
throughput run `RUNNER_NAME=podman-runner-2 deploy/ci-runner/register.sh` (each gets its own
volume). Caveat: all runners share the VM's network and container names, and the
`DbMigrator Container Smoke` job (service on port 5432) and the frontend `Container` jobs (port
8080, container name `smoke`) assume they are alone — two of those at once on the same VM
collide. Keep it to one runner unless the VM has spare CPU/RAM and you accept the odd retry; 4
CPU / 8 GiB fits one comfortably.

## Security (public repo)

A self-hosted runner executes whatever the workflow tells it to, on this machine, with access to
the podman socket (i.e. any container on the VM). Code from strangers must never get there.

1. **Guard in every workflow.** Each job that a `pull_request` can start only runs when
   `github.event.pull_request.head.repo.full_name == github.repository` (the `changes` job, which
   everything else `needs`, plus CodeQL and template smoke jobs). For a fork PR the required
   `Backend CI` / `Frontend CI` gate runs on a GitHub-hosted VM, checks nothing out and **fails**,
   so a fork PR can't be merged green without review.
2. **Turn on approval — required, the guard alone is not enough.** A fork PR can edit the
   workflow files themselves and delete the guard. Only GitHub stops that: **Settings → Actions →
   General → "Fork pull request workflows from outside collaborators" → "Require approval for all
   external contributors"**. Before approving a run, read any change under `.github/`.
3. To test a contributor's change: review it, push it to a branch of this repo, open a PR from
   there.
4. Never use `pull_request_target` with a checkout of the PR head on this runner.
