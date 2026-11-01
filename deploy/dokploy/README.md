# Deploy fullstackhero on Dokploy

`docker-compose.prod.yml` is the production stack for a single VPS running [Dokploy](https://dokploy.com). It is derived from `deploy/docker/docker-compose.yml`, with three differences: **no service publishes a host port** (Dokploy's Traefik routes domains to containers), `api` and `migrator` come from the GHCR images CI builds, and the optional object store sits behind a compose profile.

| Service | Image | Network | What it is |
|---|---|---|---|
| `db-roles`, `db-grants` | `postgres:18-alpine` | internal only | One-shot, every deploy: roles, extensions and grants, before and after the migrator (see [Database roles](#database-roles)). |
| `init` | `alpine:3.20` | internal only | One-shot: validates the storage settings and fixes ownership of the `api_files` volume. |
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
7. **Deploy.** Click Deploy. Order is enforced by `depends_on`: `postgres` + `valkey` healthy, then `db-roles`, `migrator`, `db-grants` run in that order and must exit 0, then `api`, `admin`, `dashboard`. If `migrator` fails, nothing else starts: read its log in the Logs tab.
8. **Verify.**
   ```bash
   curl -fsS https://api.example.com/health/ready        # 200
   curl -fsS https://admin.example.com/config.json       # shows FSH_API_URL
   curl -fsS https://app.example.com/config.json
   ```
9. **Sign in.** `https://admin.example.com`, tenant `root`, `admin@root.com` with `SEED_ADMIN_PASSWORD`; rotate it from Settings → Security.

Redeploys (new image tag or `git push` with auto-deploy) re-run `migrator` first, so migrations are applied before the new API starts. Pin `FSH_IMAGE_TAG` to a release version instead of `latest` for repeatable rollbacks. Until the first `v*` tag is published CI pushes only `dev-latest`, so set `FSH_IMAGE_TAG=dev-latest` for the first deploy.

> Pitfall: Dokploy keeps bind mounts of repository files only under `../files`; relative paths into the repo can be lost between deploys. Use named volumes for data (this file does) and Dokploy *File Mounts* for config files.

## Database roles

Ba role, tạo bởi hai service one-shot cùng chạy `scripts/db-roles.sh` → `postgres-init/01-roles.sql` ở **mỗi lần deploy**: `db-roles` (trước `migrator`: tạo role, extension, grant nền) và `db-grants` (sau `migrator`, trước `api`: cấp quyền cho schema/bảng migrator vừa tạo):

| Role | Dùng bởi | Quyền |
|---|---|---|
| `app_migrate` | `migrator` | Sở hữu database và mọi schema module; DDL + DML. Không superuser. |
| `app_runtime` | `api` | `USAGE` trên schema, `SELECT/INSERT/UPDATE/DELETE` trên bảng, `USAGE/SELECT` trên sequence. Không `CREATE/DROP/ALTER` ở schema module. Sở hữu schema `hangfire` (Hangfire tự tạo/migrate bảng của nó khi API khởi động). **Append-only trên schema `audit`**: chỉ `SELECT` + `INSERT`. |
| `app_readonly` | người vận hành (báo cáo, hỗ trợ) | `SELECT` trên mọi bảng, kể cả `hangfire`. |

`fsh` (bootstrap superuser, mật khẩu `POSTGRES_PASSWORD`) chỉ dùng cho `db-roles` và cứu hộ, không bao giờ cho app. Connection string của `migrator` và `api` được compose tự ghép từ `APP_MIGRATE_PASSWORD` / `APP_RUNTIME_PASSWORD` (không còn biến `DB_*_CONNECTION`). Mật khẩu chỉ nên là ký tự an toàn cho connection string (không `;`, không khoảng trắng): `openssl rand -hex 24`. Postgres không publish port; truy cập thủ công qua tab *Terminal* của container `postgres` (`psql -U app_readonly fsh`).

**Vì sao service one-shot chứ không phải `/docker-entrypoint-initdb.d`.** Thư mục đó chỉ chạy lúc khởi tạo volume rỗng: không áp dụng được cho DB đã có, không đổi được mật khẩu, không re-grant được. `db-roles` idempotent nên chạy lại mỗi deploy là an toàn. Script được bind-mount từ repo (`./postgres-init`, `./scripts`): Dokploy clone lại repo ở mỗi lần deploy *trước* khi `compose up`, nên file luôn có mặt khi container one-shot khởi động (khác với dữ liệu, thứ phải nằm trong named volume). Muốn chạy tay (ví dụ sau khi thêm module): Redeploy, hoặc trong Terminal của container `postgres`:

```bash
PGUSER=fsh PGDATABASE=fsh APP_MIGRATE_PASSWORD=... APP_RUNTIME_PASSWORD=... APP_READONLY_PASSWORD=... \
  sh /duong/dan/toi/scripts/db-roles.sh
```

**Module mới không cần sửa gì.** Schema module được phát hiện động (mọi schema do `app_migrate` sở hữu), và `db-grants` chạy lại lượt cấp quyền *sau* migrator, nên schema/bảng mà migrator tạo ngay trong lần deploy này cũng có quyền trước khi `api` khởi động. `ALTER DEFAULT PRIVILEGES FOR ROLE app_migrate` lo các bảng tạo thêm sau đó. Script chỉ giả định DB mới: nếu từng chạy migrator bằng user `fsh`, các bảng thuộc `fsh` và `app_migrate` không ALTER được (đổi owner thủ công trước).

**Bảo mật script.** `01-roles.sql` chạy bằng superuser, trong khi `app_migrate` sở hữu `public` và database, tức là có thể đặt hàm/toán tử trùng tên hoặc `ALTER DATABASE ... SET search_path`. Vì vậy file đặt `SET search_path = pg_catalog, pg_temp` ngay đầu, tạo extension `SCHEMA public`, và `db-roles.sh` bỏ `PGOPTIONS`/`PSQLRC`. Khi sửa file, đừng dùng tên không qualify ngoài `pg_catalog`.

**Audit append-only (nguyên tắc 2).** `app_runtime` không có `UPDATE/DELETE/TRUNCATE` trên schema `audit`, kể cả bảng tương lai (default privileges). Lưu ý: module Auditing có `AuditRetentionJob` (Hangfire recurring job `auditing-retention`) dùng `ExecuteDeleteAsync` để xoá bản ghi cũ. Job này **tắt mặc định** (`Auditing:Retention:Enabled=false`) và ở đây vẫn tắt; nếu bật, lệnh DELETE sẽ bị Postgres từ chối (`permission denied for table AuditRecords`), nghĩa là quyền DB chặn đúng thứ nguyên tắc 2 cấm. Đừng bật `Auditing__Retention__Enabled`; nếu sau này cần lưu trữ audit cũ, làm bằng bước archive có chủ đích dưới `app_migrate`, không phải job tự động.

## Profiles

| Profile | Services | Default |
|---|---|---|
| `storage` | `rustfs`, `rustfs-init` | off |
| `ai` | reserved name for a future ai-service / Langfuse; **nothing is defined yet** | n/a |

**Object storage.** With `storage` off, the API runs `Storage__Provider=local`: files are written under `/app/wwwroot` of the api container, which is mounted from the named volume `api_files` so they survive redeploys (the one-shot `init` service makes the volume writable for the chiseled API user, UID 1654). Back that volume up with the database. The local provider serves everything from `wwwroot` and does not enforce `private/`, so prefer S3 for real data. To use S3:

1. Set `COMPOSE_PROFILES=storage`, `FSH_STORAGE_PROVIDER=s3`, `FSH_S3_PUBLIC_URL=https://s3.example.com`, `RUSTFS_ACCESS_KEY`, `RUSTFS_SECRET_KEY` in the Environment tab.
2. Add a Dokploy domain `s3.example.com` → service `rustfs`, port `9000`, HTTPS on. Traefik keeps the original `Host` header, which the presigned signature covers.
3. Redeploy. `rustfs-init` creates the bucket `fsh` and opens anonymous read on `public/*` only.

The deploy fails closed on a half-made configuration: `storage-guard` stops RustFS from starting with a blank access or secret key (it would fall back to its default credentials on a public domain), and `init` stops the API from starting with `FSH_STORAGE_PROVIDER=s3` when `FSH_S3_PUBLIC_URL` or a key is missing. Read the failing service's log for the message.

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
| `api` never starts | `db-roles`, `migrator` or `db-grants` exited non-zero. Check their logs: missing/unsafe `APP_*_PASSWORD`, wrong `POSTGRES_PASSWORD` (if the volume predates a password change), or `JWT_SIGNING_KEY` containing `replace-with`. |
| `pull access denied` for `fsh-api` | The GHCR package is private and the VPS has no credential (step 2), or `GHCR_OWNER` is wrong. |
| 404 / 502 from Traefik on a domain | Service name or port in the Domains tab doesn't match the table above, or the service isn't on `dokploy-network`. |
| CORS error in the browser | `FSH_ADMIN_URL` / `FSH_DASHBOARD_URL` differ from the origins actually served. |
| Cannot load `libgssapi_krb5` in the log | Connection string lacks `GSS Encryption Mode=Disable` (harmless). |

## Sources

- Dokploy, Docker Compose domains (network, Traefik labels, `expose`): https://docs.dokploy.com/docs/core/docker-compose/domains
- Dokploy, Docker Compose (source, environment variables, volumes, Stack vs Compose): https://docs.dokploy.com/docs/core/docker-compose
- Base stack and its conventions: `deploy/docker/README.md`
