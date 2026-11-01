# Handover — "Handover preparation" (2026-11-02)

Tài liệu này để phiên Claude Code tiếp theo đọc trước khi code. Mọi quyết định ở đây đã được Nhân chốt; đừng mở lại trừ khi có lý do kỹ thuật mới.

## 1. Bối cảnh

- Mục tiêu: dựng **"phần thô" (chassis) dùng cho mọi gig** — nhận khách → hoàn thiện → giao trong thời gian ngắn nhất.
- Quyết định nền: **fork FullStackHero .NET 10 Starter Kit** (repo này), cắt bớt, rồi cấy linh kiện từ `../AICMSDotnet` (RBAC ma trận, search tiếng Việt, Import/Export, ai-service Python, MCP).
- Plan 8 tuần + bản đồ tận dụng + nguyên tắc bảo mật DB nằm trong Claude Doc:
  https://claude.ai/artifact/Q2ME984Gu8xSaSw2M4ZLjA
- Quỹ thời gian: ~10h/tuần. Mỗi tuần một deliverable chạy được.

## 2. Nguyên tắc bắt buộc (đã chốt)

1. **Mọi tính năng có công tắc, mặc định tắt, dùng gì mới mở.** Module-level qua `Modules:{Name}:Enabled`; feature-level qua `Microsoft.FeatureManagement` (đã có trong BuildingBlocks/Web/FeatureFlags); service tùy chọn qua Docker Compose profile.
2. **Không có purge/xóa cứng tự động.** Soft delete vĩnh viễn + archive schema. Xóa cứng chỉ khi khách yêu cầu bằng văn bản, chạy tay, có backup trước và audit sau.
3. **Postgres không lộ ra Internet; 3 DB role** (`app_migrate`, `app_runtime`, `app_readonly`); secrets ngoài repo; audit append-only; backup offsite + verify restore hằng tháng.
4. **Không sửa `src/BuildingBlocks`** nếu không cần — đã được Nhân cho phép cho công tắc module; lần sau hỏi lại.
5. Mọi thay đổi status đi qua workflow, lịch sử tái lập được (rule cũ của Nhân cho base framework).

## 3. Trạng thái code

Branch: `feat/module-toggles` (chưa push). 2 commit trên `main` upstream `a9675375`:

| Commit | Nội dung |
|---|---|
| `bd3b086c` chore(deps) | Bump toàn bộ pin Microsoft 10.0.12 → **10.0.14** trong `src/Directory.Packages.props`, pin thêm `System.Security.Cryptography.Pkcs` và `Microsoft.AspNetCore.SignalR.Common`. Lý do: NuGetAudit + TreatWarningsAsErrors làm upstream không restore được. |
| `2cdfec17` feat(modules) | Công tắc module. Chi tiết ở §4. |

Đã verify: `dotnet build` 0 warning; 1.340 unit test pass (13 project); DbMigrator không tạo schema của 4 module tắt; API boot Development sạch; `GET /api/v1/modules` đúng; `/api/v1/billing/*` 404; root admin 0 permission của 4 module; dashboard `tsc -b` + eslint sạch.

**Chưa chạy:** `Integration.Tests` (Testcontainers, cần Docker, ~10 phút) và Playwright. Chạy trước khi merge.

## 4. Công tắc module — cách hoạt động

- `src/BuildingBlocks/Web/Modules/ModuleLoader.cs`: đọc section `Modules`; module có `Enabled=false` → bỏ qua `ConfigureServices`, không scan validator, không `MapEndpoints`/middleware; **prune mọi ServiceDescriptor** có `ImplementationType` thuộc assembly module đó (handler Mediator đăng ký trước AddModules) để Development DI validation không fail.
- `FshModuleAttribute.Required = true` → không tắt được. Đã set cho Identity, Multitenancy.
- `IModuleRegistry` (singleton) + `GET /api/v1/modules` (RequireAuthorization) trong `src/Host/FSH.Starter.Api/Program.cs`.
- `src/Host/FSH.Starter.Api/appsettings.json`: section `Modules`, Billing/Catalog/Tickets/Chat = false. DbMigrator link cùng file này.
- `src/Tests/Integration.Tests/Infrastructure/FshWebApplicationFactory.cs`: override 4 module = true để suite upstream không đổi.
- Dashboard: `clients/dashboard/src/api/modules.ts`, `hooks/use-modules.ts` (fail-open), `pages/overview.tsx` gate 2 query billing + first-run panel. `tests/helpers/shell-mocks.ts` mock `/api/v1/modules` all-on.
- Menu admin/dashboard **không cần sửa**: module tắt không gọi `PermissionConstants.Register` → admin role không có permission → nav ẩn.

Giới hạn đã biết:
- `ModuleLoader` là static one-shot (`_modulesLoaded`) — trong một process chỉ host đầu tiên quyết định; không test được 2 cấu hình trong cùng test process. Pre-existing, chưa sửa.
- `RolePermissionSyncer` chỉ add, không remove: deploy có sẵn mà sau đó tắt module thì permission cũ còn trong DB (menu vẫn hiện, endpoint 404). Fresh deploy per khách thì không gặp. Nếu cần: thêm bước remove permission của module tắt.

## 5. Môi trường trên máy Nhân (macOS)

- .NET SDK **10.0.403** cài ở `~/.dotnet` qua dotnet-install.sh. Chưa có trong PATH mặc định → mỗi lệnh cần:
  ```bash
  export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  export TMPDIR="$(getconf DARWIN_USER_TEMP_DIR)"   # thiếu TMPDIR → NuGet-Migrations mutex crash
  ```
  Nên thêm 2 dòng trên vào `~/.zshrc`.
- Node v24 qua fnm. `clients/dashboard` đã `npm ci`; `clients/admin` **chưa**.
- Docker CLI dùng podman machine (server "fedora"). `docker run postgres:17-alpine` chạy được.
- Smoke test đã dùng: Postgres container port 5433, `DatabaseOptions__ConnectionString`, `dotnet run --project src/Host/FSH.Starter.DbMigrator -- apply --seed`, API lắng nghe `http://localhost:5030` (launchSettings ghi đè `ASPNETCORE_URLS`). Login: `POST /api/v1/identity/token/issue`, header `tenant: root`, `admin@root.com` / `123Pa$$word!` (Development).

## 6. Việc tiếp theo (theo plan)

**Trước khi merge `feat/module-toggles`:**
- [ ] Chạy `dotnet test src/Tests/Integration.Tests` (Docker) và `cd clients/dashboard && npx playwright test tests/overview`.
- [ ] Push branch, mở PR vào `main` của fork.

**Tuần 2 — Deploy:**
- [ ] `deploy/dokploy/` đã có sẵn trong repo — đọc trước, tận dụng.
- [ ] `docker-compose.prod.yml`: api + dbmigrator (one-shot) + admin + dashboard + postgres (không publish port) + valkey; profile `ai` cho ai-service/Langfuse sau này.
- [ ] Dokploy lên 1 VPS thử, HTTPS, Uptime Kuma, backup Postgres → R2, **verify restore**.
- [ ] Script tạo 3 DB role; migrator chạy bằng `app_migrate`, API bằng `app_runtime`.

**Tuần 3–4 — Cấy từ AICMSDotnet** (`/Users/nhanlagc/Documents/github/AICMSDotnet`):
- RBAC ma trận + condition filter: `backend/src/AICms.Application/Content/Authorization`
- Search tiếng Việt / slug: `AICms.Infrastructure` SQL dialect, `AICms.Domain/SlugHelper.cs`
- Import/Export: `AICms.Application/Content/Transfer`
- Mỗi thứ là một module mới `Modules.{Name}` theo skill `.agents/skills/add-module`, có công tắc riêng.

## 7. Quy ước khi code trong repo này

- Đọc `AGENTS.md` + rule file liên quan trong `.agents/rules/` trước khi sửa khu vực đó.
- Đăng ký module mới chạm **4 chỗ** (Api `Program.cs` Mediator + moduleAssemblies, và cặp tương tự trong DbMigrator).
- Build chạy `TreatWarningsAsErrors` + SonarAnalyzer: S1450 (field → local), S3267 (foreach → Select) đã dính một lần.
- Commit message: conventional commits, tiếng Anh, giải thích *vì sao* trong body.
