# Deploy fullstackhero on Dokploy

`docker-compose.prod.yml` is the production stack for a single VPS running [Dokploy](https://dokploy.com). It is derived from `deploy/docker/docker-compose.yml`, with three differences: **no service publishes a host port** (Dokploy's Traefik routes domains to containers), `api` and `migrator` come from the GHCR images CI builds, and the optional object store sits behind a compose profile.

| Service | Image | Network | What it is |
|---|---|---|---|
| `postgres` | `postgres:18-alpine` | internal only | Database. Never reachable from outside the project. |
| `valkey` | `valkey/valkey:9.1.0-alpine` | internal only | HybridCache L2, Data Protection keys, idempotency store (password + AOF). |
| `migrator` | `ghcr.io/<GHCR_OWNER>/fsh-db-migrator` | internal only | One-shot `apply --seed` on every deploy; exits 0 before `api` starts. |
| `api` | `ghcr.io/<GHCR_OWNER>/fsh-api` | internal + `dokploy-network` | ASP.NET Core API, port **8080**. |
| `admin` | built from `clients/admin` | internal + `dokploy-network` | Operator console (nginx), port **80**. |
| `dashboard` | built from `clients/dashboard` | internal + `dokploy-network` | Tenant dashboard (nginx), port **80**. |
| `rustfs`, `rustfs-init` | `rustfs/rustfs`, `amazon/aws-cli` | profile `storage` | Optional S3-compatible object store; see [Profiles](#profiles). |

`admin` and `dashboard` have no registry image yet, so Dokploy builds them on the VPS from the repository (first deploy: several minutes).

## First deploy, step by step

1. **DNS.** Create `A` records pointing at the VPS for the API, admin and dashboard hosts (e.g. `api.`, `admin.`, `app.`). Add the S3 host too if you will use the `storage` profile.
2. **GHCR access.** The GHCR packages `fsh-api` and `fsh-db-migrator` must be pullable by the VPS: make them public, or add a registry credential in Dokploy (Settings → Registry) and log the VPS in with a PAT that has `read:packages`.
3. **Create the project.** Dokploy → Projects → Create Project → Create Service → **Compose**. Type: **Docker Compose** (not Stack; Stack does not support `build:`).
4. **Source.** Provider: GitHub (or Git) → pick this repository and branch. **Compose Path:** `./deploy/dokploy/docker-compose.prod.yml`.
5. **Environment.** In the Environment tab paste `deploy/dokploy/.env.example`, then fill every empty value. Dokploy saves it to a `.env` next to the compose file; the compose file picks each variable up through `${VAR}`. Generate secrets with `openssl rand -base64 48`. The URLs must be the exact origins you attach in step 6, or CORS and e-mail links break.
6. **Domains.** Domains tab → Add Domain, once per service, with **HTTPS** on and certificate **Let's Encrypt**:

   | Domain | Service name | Container port |
   |---|---|---|
   | `api.example.com` | `api` | `8080` |
   | `admin.example.com` | `admin` | `80` |
   | `app.example.com` | `dashboard` | `80` |

   Use *Preview Compose* to inspect the Traefik labels Dokploy adds before deploying. `api`, `admin` and `dashboard` already join the external `dokploy-network` in the compose file (the Dokploy docs require this for services other than the one a domain is added to).
7. **Deploy.** Click Deploy. Order is enforced by `depends_on`: `postgres` + `valkey` healthy, then `migrator` runs and must exit 0, then `api`, `admin`, `dashboard`. If `migrator` fails, nothing else starts: read its log in the Logs tab.
8. **Verify.**
   ```bash
   curl -fsS https://api.example.com/health/ready        # 200
   curl -fsS https://admin.example.com/config.json       # shows FSH_API_URL
   curl -fsS https://app.example.com/config.json
   ```
9. **Sign in.** `https://admin.example.com`, tenant `root`, `admin@root.com` with `SEED_ADMIN_PASSWORD`; rotate it from Settings → Security.

Redeploys (new image tag or `git push` with auto-deploy) re-run `migrator` first, so migrations are applied before the new API starts. Pin `FSH_IMAGE_TAG` to a release version instead of `latest` for repeatable rollbacks.

> Pitfall: Dokploy keeps bind mounts of repository files only under `../files`; relative paths into the repo can be lost between deploys. Use named volumes for data (this file does) and Dokploy *File Mounts* for config files.

## Database roles

`DB_MIGRATE_CONNECTION` (DDL role `app_migrate`, used only by `migrator`) and `DB_RUNTIME_CONNECTION` (DML role `app_runtime`, used by `api`) are separate on purpose. The roles and their init scripts arrive in the next change (hardening task); until then `.env.example` points both at the bootstrap user `fsh`. Postgres publishes no port in either case: for ad-hoc access use Dokploy's *Terminal* tab on the `postgres` container.

## Profiles

| Profile | Services | Default |
|---|---|---|
| `storage` | `rustfs`, `rustfs-init` | off |
| `ai` | reserved name for a future ai-service / Langfuse; **nothing is defined yet** | n/a |

**Object storage.** With `storage` off, the API runs `Storage__Provider=local` (files go to the container's `wwwroot`, which is lost on redeploy and does not enforce `private/`: fine for a trial, not for real data). To use S3:

1. Set `COMPOSE_PROFILES=storage`, `FSH_STORAGE_PROVIDER=s3`, `FSH_S3_PUBLIC_URL=https://s3.example.com`, `RUSTFS_ACCESS_KEY`, `RUSTFS_SECRET_KEY` in the Environment tab.
2. Add a Dokploy domain `s3.example.com` → service `rustfs`, port `9000`, HTTPS on. Traefik keeps the original `Host` header, which the presigned signature covers.
3. Redeploy. `rustfs-init` creates the bucket `fsh` and opens anonymous read on `public/*` only.

Alternatively point `Storage__S3__*` at an external provider (Cloudflare R2, AWS S3) and skip the profile.

## Module switches

`FSH_MODULE_BILLING`, `FSH_MODULE_CATALOG`, `FSH_MODULE_TICKETS`, `FSH_MODULE_CHAT` map to `Modules__{Name}__Enabled` and are all `false` by default. They are defined once in the compose file (`x-modules`) and reach `migrator` and `api` identically; change them in the Environment tab and redeploy so the migrator creates or skips the module's schema.

## Uptime monitoring (Uptime Kuma)

Run [Uptime Kuma](https://github.com/louislam/uptime-kuma) as its **own** Dokploy service (Templates → Uptime Kuma), not inside this compose, so it stays up while you redeploy the app. Give it its own domain, then add HTTP(s) monitors:

| Monitor | URL | Expect |
|---|---|---|
| API | `https://api.example.com/health/ready` | 200 |
| Admin | `https://admin.example.com/` | 200 |
| Dashboard | `https://app.example.com/` | 200 |

Add a notification channel (e-mail, Telegram, Slack) and attach it to all three. The API and migrator images are chiseled (no shell, no curl), so there is no in-container healthcheck: external monitoring on `/health/ready` is the health signal.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `... is required` when deploying | An empty required variable in the Environment tab; the message names it. |
| `network dokploy-network declared as external, but could not be found` | Not running under Dokploy (it creates the network), or Dokploy's network was removed. Locally: `docker network create dokploy-network`. |
| `api` never starts | `migrator` exited non-zero. Check its log: bad `DB_MIGRATE_CONNECTION`, or `JWT_SIGNING_KEY` containing `replace-with`. |
| `pull access denied` for `fsh-api` | The GHCR package is private and the VPS has no credential (step 2), or `GHCR_OWNER` is wrong. |
| 404 / 502 from Traefik on a domain | Service name or port in the Domains tab doesn't match the table above, or the service isn't on `dokploy-network`. |
| CORS error in the browser | `FSH_ADMIN_URL` / `FSH_DASHBOARD_URL` differ from the origins actually served. |
| Cannot load `libgssapi_krb5` in the log | Connection string lacks `GSS Encryption Mode=Disable` (harmless). |

## Sources

- Dokploy, Docker Compose domains (network, Traefik labels, `expose`): https://docs.dokploy.com/docs/core/docker-compose/domains
- Dokploy, Docker Compose (source, environment variables, volumes, Stack vs Compose): https://docs.dokploy.com/docs/core/docker-compose
- Base stack and its conventions: `deploy/docker/README.md`
