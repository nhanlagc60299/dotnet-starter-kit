# Deploy fullstackhero lên Dokploy

`docker-compose.prod.yml` là stack production cho **một VPS** chạy [Dokploy](https://dokploy.com). Nó dẫn xuất từ `deploy/docker/docker-compose.yml` với ba khác biệt: **không service nào publish port ra host** (Traefik của Dokploy route domain tới container), `api` và `migrator` lấy từ image GHCR mà CI build, và object store tuỳ chọn nằm sau một compose profile.

> **Mỗi host Dokploy chỉ chạy một bản của stack này.** Tên service (`postgres`, `valkey`, `api`...) là tên chung và các service `api`/`admin`/`dashboard` cùng nằm trên mạng dùng chung `dokploy-network`; hai bản trên cùng host sẽ phân giải nhầm tên của nhau. Cần môi trường thứ hai (staging) thì dùng VPS/host Dokploy khác.

| Service | Image | Mạng | Là gì |
|---|---|---|---|
| `db-roles`, `db-grants` | `postgres:18-alpine` | chỉ nội bộ | One-shot, mỗi lần deploy: role, extension và grant, trước và sau migrator (xem [Database roles](#database-roles)). |
| `init` | `alpine:3.20` | chỉ nội bộ | One-shot: **từ chối deploy** nếu storage không phải S3 hoặc cấu hình S3 dở dang (provider `local` bị cấm trong production). |
| `postgres` | `postgres:18-alpine` | chỉ nội bộ | Database. Không bao giờ truy cập được từ bên ngoài project. |
| `valkey` | `valkey/valkey:9.1.0-alpine` | chỉ nội bộ | HybridCache L2, khoá Data Protection, kho idempotency (có mật khẩu + AOF). |
| `migrator` | `ghcr.io/<GHCR_OWNER>/fsh-db-migrator` | chỉ nội bộ | One-shot `apply --seed` mỗi lần deploy; thoát 0 trước khi `api` khởi động. |
| `api` | `ghcr.io/<GHCR_OWNER>/fsh-api` | nội bộ + `dokploy-network` | ASP.NET Core API, cổng **8080**. |
| `admin` | build từ `clients/admin` | nội bộ + `dokploy-network` | Console cho operator (nginx), cổng **80**. |
| `dashboard` | build từ `clients/dashboard` | nội bộ + `dokploy-network` | Dashboard cho tenant (nginx), cổng **80**. |
| `backup` | build từ `deploy/dokploy/db-backup` | chỉ nội bộ | Luôn bật: `pg_dump` hằng ngày lên bucket tương thích S3 (R2) và kiểm tra restore hằng tháng; xem [Backup và restore](#backup-và-restore). |
| `rustfs`, `rustfs-init` | `rustfs/rustfs`, `amazon/aws-cli` | profile `storage` (bật mặc định qua `COMPOSE_PROFILES`) | Object store S3 đi kèm, nơi lưu file; bỏ profile chỉ khi dùng S3/R2 bên ngoài. Xem [Lưu file](#profiles). |

`admin` và `dashboard` chưa có image trên registry, nên Dokploy build chúng trên VPS từ repository (lần deploy đầu mất vài phút).

## Deploy lần đầu, từng bước

1. **DNS.** Tạo bản ghi `A` trỏ về VPS cho các host API, admin và dashboard (ví dụ `api.`, `admin.`, `app.`). Thêm cả host S3 (ví dụ `s3.`) cho service `rustfs`, trừ khi dùng S3/R2 bên ngoài.
2. **Quyền truy cập GHCR.** VPS phải pull được package GHCR `fsh-api` và `fsh-db-migrator`: để chúng public, hoặc thêm credential registry trong Dokploy (Settings → Registry) và đăng nhập VPS bằng PAT có `read:packages`.
3. **Tạo project.** Dokploy → Projects → Create Project → Create Service → **Compose**. Type: **Docker Compose** (không phải Stack; Stack không hỗ trợ `build:`).
4. **Source.** Provider: GitHub (hoặc Git) → chọn repository và branch này. **Compose Path:** `./deploy/dokploy/docker-compose.prod.yml`.
5. **Environment.** Trong tab Environment dán nội dung `deploy/dokploy/.env.example` rồi điền mọi giá trị còn trống. Dokploy lưu nó thành `.env` cạnh file compose; compose lấy từng biến qua `${VAR}`. Sinh secret bằng `openssl rand -base64 48`. Các URL phải đúng từng ký tự với origin bạn gắn ở bước 6, nếu không CORS và link trong e-mail sẽ hỏng. **Lưu một bản của toàn bộ Environment ở ngoài VPS** (password manager): xem [Khôi phục sau khi mất VPS](#khôi-phục-sau-khi-mất-vps-dr).
6. **Domains.** Tab Domains → Add Domain, mỗi service một lần, bật **HTTPS** và certificate **Let's Encrypt**:

   | Domain | Service name | Container port |
   |---|---|---|
   | `api.example.com` | `api` | `8080` |
   | `admin.example.com` | `admin` | `80` |
   | `app.example.com` | `dashboard` | `80` |
   | `s3.example.com` | `rustfs` | `9000` |

   Dùng *Preview Compose* để xem các label Traefik mà Dokploy thêm vào trước khi deploy. `api`, `admin` và `dashboard` đã tham gia mạng external `dokploy-network` ngay trong file compose (tài liệu Dokploy yêu cầu điều này cho các service không phải service được gắn domain).
7. **Deploy.** Bấm Deploy. Thứ tự do `depends_on` đảm bảo: `postgres` + `valkey` healthy, rồi `db-roles`, `migrator`, `db-grants` chạy lần lượt và phải thoát 0, rồi tới `api`, `admin`, `dashboard`. Nếu `migrator` lỗi thì không gì khác khởi động: đọc log của nó ở tab Logs.
8. **Kiểm tra.**
   ```bash
   curl -fsS https://api.example.com/health/ready        # 200
   curl -fsS https://admin.example.com/config.json       # có FSH_API_URL
   curl -fsS https://app.example.com/config.json
   ```
9. **Đăng nhập.** `https://admin.example.com`, tenant `root`, `admin@root.com` với `SEED_ADMIN_PASSWORD`; đổi mật khẩu ở Settings → Security.
10. **Chạy thử restore ngay.** Trong Terminal của container `backup`: `backup.sh` rồi `restore-verify.sh`. Đừng chờ lịch tháng: lúc đó mới biết cấu hình R2 sai thì đã mất cả tháng backup.

Redeploy (đổi image tag, hoặc `git push` có bật auto-deploy) chạy lại `migrator` trước, nên migration được áp trước khi API mới khởi động. `api` và `migrator` có `pull_policy: always`: Dokploy chạy `up -d --build` mà không `--pull`, nên nếu thiếu dòng này, tag di động như `dev-latest` sẽ không bao giờ được kéo lại. Hiện chưa có tag `v*` nào, CI chỉ đẩy `dev-latest`, nên giữ `FSH_IMAGE_TAG=dev-latest`; khi đã có release, ghim `FSH_IMAGE_TAG` vào một phiên bản để rollback lặp lại được.

> Lưu ý: dữ liệu ghi vào bind mount từ repo sẽ mất khi redeploy (Dokploy chỉ giữ bind mount của file repo dưới `../files`, đường dẫn tương đối vào repo có thể bị mất giữa các lần deploy). Script **chỉ đọc** mount từ repo (`./postgres-init`, `./scripts`) thì ổn vì repo được clone lại trước mỗi `compose up`. Dữ liệu thì dùng named volume (file này làm vậy) và *File Mounts* của Dokploy cho file cấu hình.

## Database roles

Ba role, tạo bởi hai service one-shot cùng chạy `scripts/db-roles.sh` → `postgres-init/01-roles.sql` ở **mỗi lần deploy**: `db-roles` (trước `migrator`: tạo role, extension, grant nền) và `db-grants` (sau `migrator`, trước `api`: cấp quyền cho schema/bảng migrator vừa tạo):

| Role | Dùng bởi | Quyền |
|---|---|---|
| `app_migrate` | `migrator` | Sở hữu database và mọi schema module; DDL + DML. Không superuser. |
| `app_runtime` | `api` | `USAGE` trên schema, `SELECT/INSERT/UPDATE/DELETE` trên bảng, `USAGE/SELECT` trên sequence. Không `CREATE/DROP/ALTER` ở schema module. Sở hữu schema `hangfire` (Hangfire tự tạo/migrate bảng của nó khi API khởi động). **Append-only trên schema `audit`**: chỉ `SELECT` + `INSERT`. |
| `app_readonly` | `backup` và người vận hành (báo cáo, hỗ trợ) | `SELECT` trên mọi bảng và sequence, kể cả `hangfire`. |

`fsh` (bootstrap superuser, mật khẩu `POSTGRES_PASSWORD`) chỉ dùng cho `db-roles` và cứu hộ, không bao giờ cho app. Connection string của `migrator` và `api` được compose tự ghép từ `APP_MIGRATE_PASSWORD` / `APP_RUNTIME_PASSWORD` (không còn biến `DB_*_CONNECTION`). Mật khẩu chỉ nên là ký tự an toàn cho connection string (không `;`, không khoảng trắng): `openssl rand -hex 24`. Postgres không publish port; truy cập thủ công qua tab *Terminal* của container `postgres` (`psql -U app_readonly fsh`).

> **`app_readonly` đọc được mọi bảng, kể cả mật khẩu băm của người dùng** (`identity."Users"`). "Chỉ đọc" không có nghĩa là "không nhạy cảm": coi `APP_READONLY_PASSWORD` là secret production như các mật khẩu khác. Cùng lý do, mọi bản dump của nó chứa các hash đó.

**Vì sao service one-shot chứ không phải `/docker-entrypoint-initdb.d`.** Thư mục đó chỉ chạy lúc khởi tạo volume rỗng: không áp dụng được cho DB đã có, không đổi được mật khẩu, không re-grant được. `db-roles` idempotent nên chạy lại mỗi deploy là an toàn. Script được bind-mount từ repo (`./postgres-init`, `./scripts`): Dokploy clone lại repo ở mỗi lần deploy *trước* khi `compose up`, nên file luôn có mặt khi container one-shot khởi động (khác với dữ liệu, thứ phải nằm trong named volume). Muốn chạy tay (ví dụ sau khi thêm module): Redeploy, hoặc trong Terminal của container `postgres`:

```bash
PGUSER=fsh PGDATABASE=fsh APP_MIGRATE_PASSWORD=... APP_RUNTIME_PASSWORD=... APP_READONLY_PASSWORD=... \
  sh /duong/dan/toi/scripts/db-roles.sh
```

**Module mới không cần sửa gì.** Schema module được phát hiện động (mọi schema do `app_migrate` sở hữu), và `db-grants` chạy lại lượt cấp quyền *sau* migrator, nên schema/bảng mà migrator tạo ngay trong lần deploy này cũng có quyền trước khi `api` khởi động. `ALTER DEFAULT PRIVILEGES FOR ROLE app_migrate` lo các bảng tạo thêm sau đó. Script chỉ giả định DB mới: nếu từng chạy migrator bằng user `fsh`, các bảng thuộc `fsh` và `app_migrate` không ALTER được (đổi owner thủ công trước).

**Bảo mật script.** `01-roles.sql` chạy bằng superuser, trong khi `app_migrate` sở hữu `public` và database, tức là có thể đặt hàm/toán tử trùng tên hoặc `ALTER DATABASE ... SET search_path`. Vì vậy file đặt `SET search_path = pg_catalog, pg_temp` ngay đầu, tạo extension `SCHEMA public`, và `db-roles.sh` bỏ `PGOPTIONS`/`PSQLRC`. Khi sửa file, đừng dùng tên không qualify ngoài `pg_catalog`.

**Audit append-only (nguyên tắc 2).** `app_runtime` không có `UPDATE/DELETE/TRUNCATE` trên schema `audit`, kể cả bảng tương lai (default privileges). Lưu ý: module Auditing có `AuditRetentionJob` (Hangfire recurring job `auditing-retention`) dùng `ExecuteDeleteAsync` để xoá bản ghi cũ. Job này **tắt mặc định** (`Auditing:Retention:Enabled=false`) và ở đây vẫn tắt; nếu bật, lệnh DELETE sẽ bị Postgres từ chối (`permission denied for table AuditRecords`), nghĩa là quyền DB chặn đúng thứ nguyên tắc 2 cấm. Đừng bật `Auditing__Retention__Enabled`; nếu sau này cần lưu trữ audit cũ, làm bằng bước archive có chủ đích dưới `app_migrate`, không phải job tự động.

## Backup và restore

Service `backup` luôn bật (backup là bắt buộc) và **fail closed**: thiếu một trong `BACKUP_S3_ENDPOINT`, `BACKUP_S3_BUCKET`, `BACKUP_S3_ACCESS_KEY`, `BACKUP_S3_SECRET_KEY` thì compose từ chối deploy (`required variable ... is missing`), và container tự thoát với thông báo rõ nếu chạy mà thiếu. Nó chạy `busybox crond` (giờ UTC), log ra stdout (tab *Logs* của Dokploy):

| Việc | Lịch mặc định | Biến | Làm gì |
|---|---|---|---|
| `backup.sh` | `0 2 * * *` (02:00 hằng ngày) | `BACKUP_CRON` | `pg_dump -Fc` bằng `app_readonly` → `fsh-<UTC>.dump` → `rclone copyto` lên `<bucket>/<BACKUP_S3_PREFIX>/`, rồi file `.sha256`. File `.sha256` được tải lên **sau cùng**: có nó nghĩa là bản backup đầy đủ. Xoá file tạm local khi xong. |
| `restore-verify.sh` | `0 4 1 * *` (04:00 ngày 1 hằng tháng) | `RESTORE_VERIFY_CRON` | Tải bản mới nhất, kiểm sha256, `initdb` một cluster tạm *trong container* (chỉ unix socket, không đụng DB prod), `pg_restore`, in số bảng theo schema, số dòng `identity."Users"`, kiểm có tenant `root`. Thoát ≠ 0 nếu bất kỳ bước nào hỏng, **kể cả khi bản mới nhất cũ hơn `RESTORE_VERIFY_MAX_AGE_HOURS` (mặc định 72 giờ)**: backup hằng ngày hỏng âm thầm thì verify cũng phải đỏ. Dọn cluster tạm. |

Script **không bao giờ xoá** gì trên remote (chỉ `rclone copyto`). Thời hạn lưu giữ là việc của bucket (xem dưới), cũng là cách để nguyên tắc "không xoá cứng tự động" không bị phá bởi chính backup.

### Cấu hình Cloudflare R2

1. R2 → *Create bucket* (ví dụ `fsh-backups`). Bucket phải tạo sẵn: token chỉ cần quyền object nên service không tạo bucket.
2. R2 → *Manage API tokens* → tạo token **Object Read & Write**, giới hạn đúng bucket đó. Lấy *Access Key ID*, *Secret Access Key* và endpoint `https://<account-id>.r2.cloudflarestorage.com`.
3. Tab Environment: `BACKUP_S3_ENDPOINT`, `BACKUP_S3_BUCKET`, `BACKUP_S3_ACCESS_KEY`, `BACKUP_S3_SECRET_KEY` (xem `.env.example`). `BACKUP_S3_PROVIDER` mặc định `Cloudflare`; dùng `Other` cho S3-compatible khác (RustFS, MinIO).
4. **Retention bằng lifecycle rule**: bucket → *Settings* → *Object lifecycle rules* → *Add rule*: prefix `fsh/` (đúng `BACKUP_S3_PREFIX`), *Delete uploaded objects after* **30 days** (chỉnh theo nhu cầu; dump hằng ngày × 30 ngày là đủ cho đa số). Không tạo rule thì backup tích luỹ mãi.

Bản dump chưa mã hoá phía client và chứa dữ liệu người dùng (kể cả hash mật khẩu): giữ bucket ở chế độ private, token chỉ cấp cho bucket này. Thư mục làm việc là volume `backup_work`; cần trống khoảng 3 lần kích thước dump (dump + cluster tạm).

### Chạy tay

Trong tab *Terminal* của container `backup` (hoặc `docker compose exec backup ...`):

```bash
backup.sh           # backup ngay, in ra tên file đã tải lên
restore-verify.sh   # kiểm bản mới nhất, exit 0 = restore được
```

### Khôi phục sau khi mất VPS (DR)

Backup chỉ chứa **database**. Những thứ sau **không** nằm trong đó, và thiếu chúng thì khôi phục không trọn vẹn:

| Thứ | Vì sao quan trọng | Làm gì |
|---|---|---|
| **Giá trị tab Environment của Dokploy** (đặc biệt `BACKUP_S3_*`, `APP_*_PASSWORD`, `POSTGRES_PASSWORD`, `JWT_SIGNING_KEY`) | Không có `BACKUP_S3_*` thì không tải được backup; không có `JWT_SIGNING_KEY` thì mọi phiên đăng nhập cũ mất hiệu lực. Chúng chỉ nằm trên VPS. | Lưu một bản ở **ngoài VPS** (password manager), cập nhật mỗi khi đổi. |
| **Khoá Data Protection** (volume `valkey_data`, khoá `DataProtection-Keys`) | API lưu khoá Data Protection **chỉ trong Valkey**; chúng mã hoá secret của webhook. Valkey mới sau DR có khoá mới, nên secret webhook đã lưu trong database **không giải mã được**. | Hoặc sao lưu volume `valkey_data` (snapshot VPS hoặc tính năng sao lưu volume của Dokploy; stack này chưa tự động hoá việc đó), hoặc chấp nhận và **cấp lại secret cho từng webhook endpoint** sau DR. |
| **Volume `rustfs_data`** (RustFS đi kèm) | File người dùng tải lên. **Không được sao lưu off-site**: mất VPS là mất file. | Snapshot VPS / sao lưu volume định kỳ, hoặc dùng S3/R2 bên ngoài: [Lưu file off-site bằng R2](#lưu-file-off-site-bằng-r2). |
| Cache và kho idempotency trong Valkey | Dựng lại được. | Không cần. |

Trình tự DR trên VPS mới: cài Dokploy, tạo lại project, dán Environment từ password manager, **deploy bình thường** (tạo role, schema rỗng, seed), rồi làm đúng quy trình dưới. (Nếu `valkey_data` đã được khôi phục thì khôi phục nó *trước* lần deploy.)

### Restore thật vào prod khi có sự cố

Các nguyên tắc: **không `DROP` gì cả**; DB hiện tại chỉ được *đổi tên* và giữ nguyên cho đến khi bản restore được chứng minh tốt; mọi bước nguy hiểm dừng ngay khi lỗi. Dump giữ nguyên owner và quyền của `app_migrate` / `app_runtime`, nên các role phải tồn tại trong cluster trước (đó là lý do phải deploy trước trong trường hợp DR).

Trước khi bắt đầu, nhớ ba điều:

- **Chọn bản dump từ *trước* sự cố.** Cron backup vẫn chạy mỗi đêm: bản mới nhất có thể đã chụp dữ liệu hỏng.
- **Tránh làm quanh 02:00 UTC.** Giữa bước 3b và bước 4, `fsh` là DB rỗng; nếu cron backup chạy đúng lúc đó, nó tải lên một bản dump rỗng làm "bản mới nhất". Sau khi xong, chạy `backup.sh` thủ công và kiểm tra bản mới.
- **Chạy từng khối một, theo thứ tự**, trong cùng một phiên Terminal (biến `old`, `dump_ok` sống trong phiên đó). Khối nào in `LỖI` thì **dừng**, đừng chạy khối kế tiếp.

#### Bước 1. Dừng ghi

Dokploy → dừng service `api` (và `admin`, `dashboard` nếu muốn trang bảo trì). Nếu giao diện Dokploy không dùng được, dùng CLI trên VPS: tìm tên container rồi dừng nó (`restart: unless-stopped` không khởi động lại container đã bị dừng thủ công):

```bash
docker ps --format '{{.Names}}' | grep -- '-api-'      # tên dạng <project>-api-1
docker stop <tên-container-api>
```

#### Bước 2. Tải và kiểm bản dump

Trong Terminal của container `backup`:

```bash
. /usr/local/lib/backup-common.sh
cd /var/lib/postgresql/work
rclone lsf "$REMOTE" | sort | tail -n 15        # chọn bản TRƯỚC sự cố: fsh-<UTC>.dump
dump_name=fsh-20261101T020000Z.dump             # sửa lại cho đúng bản đã chọn
rclone copyto "$REMOTE/$dump_name" dump
rclone copyto "$REMOTE/$dump_name.sha256" dump.sha256
sed -i 's/ .*/  dump/' dump.sha256 && sha256sum -c dump.sha256   # phải in "dump: OK"
```

#### Bước 3a. Lưới an toàn: dump DB hiện tại

Dán `POSTGRES_PASSWORD` tại chỗ (đừng lưu vào đâu). Khối này dump DB hiện tại và đẩy lên bucket; nó chỉ đặt `dump_ok=1` khi cả hai việc thành công:

```bash
export PGHOST=postgres PGUSER=fsh PGPASSWORD='<POSTGRES_PASSWORD>'
old=fsh_before_restore_$(date -u +%Y%m%dT%H%M%SZ); echo "GHI LẠI TÊN NÀY: $old"
dump_ok=
rm -f before-restore.dump
pg_dump -Fc -f before-restore.dump fsh &&
  rclone copyto before-restore.dump "$REMOTE/before-restore-$old.dump" &&
  dump_ok=1
[ "$dump_ok" = 1 ] && echo "3a OK: đã có bản dump an toàn" || echo "3a LỖI: DỪNG, không chạy bước 3b"
```

Nếu 3a lỗi thì **dừng và điều tra** (DB hỏng nặng? bucket không ghi được?). Đổi tên DB thủ công không làm mất dữ liệu, nhưng đó là quyết định của người vận hành, không phải của quy trình này.

#### Bước 3b. Đổi tên DB hiện tại, tạo `fsh` mới rỗng

```bash
[ "$dump_ok" = 1 ] && [ -n "$old" ] && psql -X -v ON_ERROR_STOP=1 -d postgres -v old="$old" <<'SQL'
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'fsh' AND pid <> pg_backend_pid();
ALTER DATABASE fsh RENAME TO :"old";
CREATE DATABASE fsh OWNER app_migrate;
SQL
```

`ON_ERROR_STOP` làm psql dừng ở câu lệnh lỗi đầu tiên: nếu đổi tên thất bại thì `fsh` cũ vẫn nguyên và không có DB mới nào được tạo. (Lỗi "being accessed by other users" nghĩa là còn kết nối: chắc chắn `api` đã dừng rồi chạy lại khối này.)

#### Bước 4. Nạp dump vào `fsh` mới

```bash
pg_restore --exit-on-error -d fsh dump && echo "4 OK" || echo "4 LỖI: chạy khối rollback bên dưới"
```

Nếu in `4 LỖI`, **rollback bằng đổi tên** (không `DROP`): DB nạp dở được giữ lại dưới tên `fsh_failed_restore_<UTC>` để điều tra, DB cũ trả về tên `fsh`. Khối này tự kiểm tra DB cũ còn tồn tại trước khi đổi bất cứ thứ gì (`1/count(*)` chia cho 0 nếu `$old` rỗng hoặc không có, và `ON_ERROR_STOP` dừng ngay):

```bash
failed=fsh_failed_restore_$(date -u +%Y%m%dT%H%M%SZ)
[ -n "$old" ] && psql -X -v ON_ERROR_STOP=1 -d postgres -v old="$old" -v failed="$failed" <<'SQL'
SELECT 1/count(*) AS old_database_exists FROM pg_database WHERE datname = :'old';
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'fsh' AND pid <> pg_backend_pid();
ALTER DATABASE fsh RENAME TO :"failed";
ALTER DATABASE :"old" RENAME TO fsh;
SQL
```

Nếu chính khối rollback báo lỗi thì **dừng**: DB cũ vẫn còn nguyên dưới tên `$old` (xem `psql -d postgres -c '\l'`); đổi tên về `fsh` bằng tay khi đã hiểu tình hình.

#### Bước 5. So sánh, rồi Redeploy

Số dòng của DB mới phải hợp lý so với DB cũ:

```bash
for d in fsh "$old"; do psql -X -d "$d" -Atc "SELECT '$d', (SELECT count(*) FROM identity.\"Users\"), (SELECT count(*) FROM tenant.\"Tenants\")"; done
```

Rồi **Redeploy** trên Dokploy: `db-roles` → `migrator` (lịch sử migration đã nằm trong dump; bản dump cũ hơn schema hiện tại thì migrator áp các migration còn thiếu) → `db-grants` đặt lại quyền cấp database, mật khẩu role và quyền trên schema, rồi `api` lên. Kiểm `https://api.example.com/health/ready` và đăng nhập.

#### Bước 6. Sau khi chạy lại

- Chạy `backup.sh`, rồi `restore-verify.sh`, để bản mới nhất trong bucket là bản của DB đã khôi phục.
- Cache cũ trong Valkey (liên quan DB trước sự cố) tự hết hạn; **không** chạy `FLUSHALL` hay `FLUSHDB`: chúng xoá luôn khoá Data Protection (mất khả năng giải mã secret webhook).
- Dọn file trong volume: `rm -f dump dump.sha256 before-restore.dump`.
- DB `fsh_before_restore_<UTC>` (và `fsh_failed_restore_<UTC>` nếu có) **vẫn nằm trong Postgres** và chiếm đĩa. Hệ thống không bao giờ tự xoá chúng; chỉ khi đã chạy ổn đủ lâu bạn mới xoá tay bằng `DROP DATABASE`, hoặc giữ lại để đối chiếu dữ liệu. Bản `before-restore-*.dump` trong bucket chịu lifecycle rule như các bản khác.

Muốn thử mà không đụng prod: `restore-verify.sh` làm đúng phần nạp dump này trong cluster tạm.

### Giám sát bằng Uptime Kuma (tuỳ chọn)

Cron im lặng khi hỏng nên nên có tín hiệu "còn sống". Trong Uptime Kuma tạo hai monitor loại **Push** và dán URL của chúng vào tab Environment:

| Biến | Monitor | Heartbeat interval gợi ý |
|---|---|---|
| `BACKUP_PING_URL` | Push, ping sau mỗi backup thành công | 26 giờ |
| `RESTORE_VERIFY_PING_URL` | Push, ping sau mỗi lần verify thành công | khoảng 32 ngày (hoặc mức tối đa Kuma cho phép) |

Script chỉ ping khi **thành công**, nên thiếu heartbeat = backup/verify hỏng hoặc không chạy. Để trống thì không ping.

## Profiles

| Profile | Services | Mặc định |
|---|---|---|
| `storage` | `rustfs`, `rustfs-init` | bật (`COMPOSE_PROFILES=storage` trong `.env.example`); bỏ khi dùng S3/R2 bên ngoài |
| `ai` | tên giữ chỗ cho ai-service / Langfuse sau này; **hiện chưa định nghĩa gì** | không áp dụng |

**Lưu file. Production không bao giờ dùng provider `local`.** Provider `local` phục vụ mọi thứ dưới `wwwroot` (kể cả khoá `private/…` của module Files) cho người gọi ẩn danh qua `UseStaticFiles`, trước cả bước xác thực (xem `.agents/rules/storage.md`: chỉ dùng cho dev). Vì vậy `FSH_STORAGE_PROVIDER` mặc định là `s3`, và service `init` làm deploy thất bại nếu nó là giá trị khác. Chỉ còn hai cách, đều là S3:

| Cách | Cấu hình | Dữ liệu nằm ở | Off-site? |
|---|---|---|---|
| **Mặc định**: RustFS đi kèm (profile `storage`) | `COMPOSE_PROFILES=storage` (đã có trong `.env.example`), `FSH_S3_PUBLIC_URL`, `RUSTFS_*` | volume `rustfs_data` | **Không** |
| S3 / R2 bên ngoài | bỏ `COMPOSE_PROFILES`, đặt `FSH_S3_*`, `RUSTFS_*` | bucket bên ngoài | Có (do nhà cung cấp) |

Chọn RustFS làm mặc định vì nó đơn giản nhất mà vẫn an toàn: không cần tài khoản ngoài, và `rustfs-init` chỉ mở đọc ẩn danh cho `public/*` (còn `private/` cần chữ ký presigned). Đổi lại, **service `backup` chỉ sao lưu database**: file trong `rustfs_data` không có bản off-site, mất VPS là mất chúng trừ khi bạn tự snapshot volume, hoặc chuyển sang bucket R2 như dưới. Nếu một bản cũ từng chạy với provider `local`, file còn nằm trong volume `api_files` (compose không còn khai báo volume này nhưng không xoá nó): chép sang bucket trước khi dọn volume.

**Dùng RustFS tự host (profile `storage`):**

1. Trong tab Environment giữ `COMPOSE_PROFILES=storage`, `FSH_STORAGE_PROVIDER=s3`, đặt `FSH_S3_PUBLIC_URL=https://s3.example.com`, `RUSTFS_ACCESS_KEY`, `RUSTFS_SECRET_KEY`.
2. Thêm domain Dokploy `s3.example.com` → service `rustfs`, cổng `9000`, bật HTTPS. Traefik giữ nguyên header `Host` gốc, thứ mà chữ ký presigned URL bao phủ.
3. Redeploy. `rustfs-init` tạo bucket (`fsh`, hoặc `FSH_S3_BUCKET`) và mở quyền đọc ẩn danh chỉ cho `public/*`; `api` chờ nó xong mới khởi động.

Deploy **fail closed** với cấu hình dở dang: `storage-guard` không cho RustFS khởi động với access/secret key trống (nó sẽ quay về key mặc định trên một domain public), và `init` không cho API khởi động khi provider không phải `s3`, khi `FSH_S3_PUBLIC_URL`, `FSH_S3_SERVICE_URL` hoặc key thiếu/không phải URL http(s), hoặc khi `FSH_S3_SERVICE_URL` trỏ vào RustFS đi kèm mà `COMPOSE_PROFILES` không có `storage`. Đọc log của service lỗi để thấy thông báo.

### Lưu file off-site bằng R2

Muốn file có bản off-site: **xoá dòng `COMPOSE_PROFILES=storage`** (không chạy RustFS) và trỏ vào một bucket R2 (hoặc S3 khác). Tất cả đặt được từ tab Environment:

| Biến | Giá trị cho R2 | Ghi chú |
|---|---|---|
| `FSH_STORAGE_PROVIDER` | `s3` | |
| `FSH_S3_SERVICE_URL` | `https://<account-id>.r2.cloudflarestorage.com` | Nơi API tự gọi. Mặc định `http://rustfs:9000` (cần profile `storage`). |
| `FSH_S3_PUBLIC_URL` | cùng endpoint trên | Endpoint dùng để ký presigned URL cho trình duyệt (gốc URL, không có path). |
| `FSH_S3_BUCKET` | tên bucket **file** (mặc định `fsh`) | Khác bucket backup; bucket phải tạo sẵn. |
| `FSH_S3_PUBLIC_BASE_URL` | domain phục vụ file public, ví dụ `https://files.example.com` | Chỉ khi domain đó chỉ lộ `/public/*` (xem cảnh báo bên dưới). Bỏ trống thì mặc định `<FSH_S3_PUBLIC_URL>/<bucket>`, đúng cho RustFS dạng path-style, thường **không** đúng cho R2. |
| `RUSTFS_ACCESS_KEY`, `RUSTFS_SECRET_KEY` | access key / secret của token R2 | Tên biến giữ nguyên dù không dùng RustFS. Dùng token riêng cho bucket file. |

Trong R2 còn phải đặt CORS cho bucket file cho phép origin `FSH_ADMIN_URL` và `FSH_DASHBOARD_URL` (phương thức `PUT`, `GET`, header `Content-Type`), vì trình duyệt upload thẳng lên presigned URL. **Cảnh báo:** custom domain hoặc `r2.dev` của R2 phục vụ *cả bucket*, tức cả khoá `private/…`, cho mọi người. Chỉ gắn nó khi có Transform/WAF rule của Cloudflare giới hạn ở đường dẫn `/public/*`; nếu không, đừng bật public access cho bucket file. Lưu ý: cấu hình R2 chưa được thử với R2 thật trong repo (chỉ kiểm bằng `docker compose config` và RustFS); thử một lần upload và một lần tải file `private/` sau khi cấu hình.

## Module switches

`FSH_MODULE_BILLING`, `FSH_MODULE_CATALOG`, `FSH_MODULE_TICKETS`, `FSH_MODULE_CHAT`, `FSH_MODULE_SEARCH`, `FSH_MODULE_ACCESSCONTROL`, `FSH_MODULE_TRANSFER` ánh xạ sang `Modules__{Name}__Enabled` và đều mặc định `false`. Chúng được định nghĩa một lần trong file compose (`x-modules`) và tới `migrator` lẫn `api` giống hệt nhau; đổi trong tab Environment rồi redeploy để migrator tạo hoặc bỏ qua schema của module.

## Giám sát uptime của ứng dụng

Chạy [Uptime Kuma](https://github.com/louislam/uptime-kuma) như **một service Dokploy riêng** (Templates → Uptime Kuma), không nằm trong compose này, để nó vẫn sống khi bạn redeploy ứng dụng. Cho nó domain riêng, rồi thêm các monitor HTTP(s):

| Monitor | URL | Kỳ vọng |
|---|---|---|
| API | `https://api.example.com/health/ready` | 200 |
| Admin | `https://admin.example.com/` | 200 |
| Dashboard | `https://app.example.com/` | 200 |

Thêm một kênh thông báo (e-mail, Telegram, Slack) và gắn vào cả ba. Image API và migrator là chiseled (không có shell, không có curl), nên không có healthcheck trong container: giám sát bên ngoài ở `/health/ready` là tín hiệu sức khoẻ. Monitor Push cho backup nằm ở [Giám sát bằng Uptime Kuma](#giám-sát-bằng-uptime-kuma-tuỳ-chọn).

## Xử lý sự cố

| Triệu chứng | Nguyên nhân thường gặp |
|---|---|
| `... is required` khi deploy | Một biến bắt buộc trong tab Environment còn trống; thông báo nêu tên biến. |
| `network dokploy-network declared as external, but could not be found` | Không chạy dưới Dokploy (nó tạo mạng này), hoặc mạng của Dokploy đã bị xoá. Chạy local: `docker network create dokploy-network`. |
| `api` không bao giờ khởi động | `db-roles`, `migrator` hoặc `db-grants` thoát khác 0. Xem log của chúng: thiếu/không an toàn `APP_*_PASSWORD`, sai `POSTGRES_PASSWORD` (nếu volume có trước khi đổi mật khẩu), hoặc `JWT_SIGNING_KEY` chứa `replace-with`. Hoặc `init` lỗi vì cấu hình storage (provider không phải `s3`, thiếu `FSH_S3_PUBLIC_URL`/key, hoặc thiếu `COMPOSE_PROFILES=storage` khi dùng RustFS đi kèm): xem log của nó. |
| Redeploy mà vẫn chạy image cũ | `FSH_IMAGE_TAG` không phải tag CI đã đẩy (hiện chỉ có `dev-latest`). `api`/`migrator` đã `pull_policy: always`, nên không phải do cache. |
| `pull access denied` cho `fsh-api` | Package GHCR là private và VPS không có credential (bước 2), hoặc `GHCR_OWNER` sai. |
| 404 / 502 từ Traefik trên một domain | Tên service hoặc cổng ở tab Domains không khớp bảng trên, hoặc service không nằm trên `dokploy-network`. |
| Lỗi CORS trong trình duyệt | `FSH_ADMIN_URL` / `FSH_DASHBOARD_URL` khác với origin thực sự được phục vụ. |
| Log `Cannot load libgssapi_krb5` | Connection string thiếu `GSS Encryption Mode=Disable` (vô hại). |
| Log `backup`: `BACKUP_S3_... is not set`, `NoSuchBucket`, `AccessDenied` | Biến thiếu, bucket chưa tạo, hoặc token R2 không có quyền object read/write trên bucket đó. Chạy lại `backup.sh` trong Terminal để thấy lỗi đầy đủ. |
| `restore-verify` báo bản mới nhất quá cũ | Backup hằng ngày không còn lên bucket: xem log của `backup` (thường là token/endpoint). |

## Nguồn

- Dokploy, Docker Compose domains (network, label Traefik, `expose`): https://docs.dokploy.com/docs/core/docker-compose/domains
- Dokploy, Docker Compose (source, biến môi trường, volumes, Stack vs Compose): https://docs.dokploy.com/docs/core/docker-compose
- Stack gốc và quy ước của nó: `deploy/docker/README.md`
