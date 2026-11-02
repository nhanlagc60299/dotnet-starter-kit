import { expect, test, type Page } from "@playwright/test";
import { mockJsonResponse, mockProblemDetails } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

const TYPES = [{ entityType: "catalog.brands", canExport: true, canImport: true }];

function job(over: Record<string, unknown>) {
  return {
    id: "11111111-1111-1111-1111-111111111111",
    kind: "Export",
    status: "Pending",
    entityTypes: ["catalog.brands"],
    mode: null,
    fileName: null,
    sizeBytes: 0,
    sha256: null,
    summary: null,
    error: null,
    createdBy: "u-test-1",
    createdAtUtc: "2026-11-02T10:00:00Z",
    completedAtUtc: null,
    ...over,
  };
}

const SUMMARY = {
  rows: { "catalog.brands": 4 },
  created: 2,
  updated: 1,
  skipped: 0,
  failed: 1,
  errors: [{ entityType: "catalog.brands", row: 3, message: "Name is required." }],
};

/** Answer GET /jobs/{id} with each body in turn, then keep repeating the last. */
async function mockJobPolls(page: Page, bodies: unknown[]) {
  let i = 0;
  await page.route("**/api/v1/transfer/jobs/*", (route) =>
    route.fulfill({ json: bodies[Math.min(i++, bodies.length - 1)] }),
  );
}

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
  await mockJsonResponse(page, "**/api/v1/identity/permissions", [
    "Permissions.Transfer.Export",
    "Permissions.Transfer.Import",
  ]);
  await mockJsonResponse(page, "**/api/v1/transfer/entity-types", TYPES);
});

test("exports, follows the job and downloads the archive", async ({ page }) => {
  let exportBody: unknown;
  await page.route("**/api/v1/transfer/exports", async (route) => {
    exportBody = route.request().postDataJSON();
    await route.fulfill({ status: 202, json: job({}) });
  });
  await mockJobPolls(page, [
    job({ status: "Running" }),
    job({ status: "Succeeded", sizeBytes: 2048, sha256: "ab".repeat(32), summary: { ...SUMMARY, created: 0, updated: 0, failed: 0, errors: [] } }),
  ]);
  await page.route("**/api/v1/transfer/exports/*/download", (route) =>
    route.fulfill({
      status: 200,
      headers: {
        "Content-Type": "application/zip",
        "Content-Disposition": "attachment; filename=transfer-export-20261102-100000.zip",
        // The dev API is cross-origin; without this the browser hides the header.
        "Access-Control-Expose-Headers": "Content-Disposition",
      },
      body: Buffer.from("PK\u0005\u0006" + "\0".repeat(18)),
    }),
  );

  await page.goto("/system/transfer");
  await expect(page.getByRole("link", { name: "Import / export" })).toBeVisible();
  const panel = page.getByRole("region", { name: "Export" });
  await panel.getByLabel("catalog.brands").check();
  await panel.getByRole("button", { name: "Start export" }).click();

  await expect(panel.getByText("Succeeded")).toBeVisible({ timeout: 5_000 });
  expect(exportBody).toEqual({ entityTypes: ["catalog.brands"] });
  await expect(panel.getByText("catalog.brands · 4 rows")).toBeVisible();
  const download = page.waitForEvent("download");
  await panel.getByRole("button", { name: "Download archive" }).click();
  expect((await download).suggestedFilename()).toBe("transfer-export-20261102-100000.zip");
});

test("uploads, previews with the chosen mode and applies an import", async ({ page }) => {
  const imported = job({ kind: "Import", fileName: "brands.zip", sizeBytes: 900 });
  await mockJsonResponse(page, "**/api/v1/transfer/imports", imported, { method: "POST" });
  let previewBody: unknown;
  await page.route("**/api/v1/transfer/imports/*/preview", async (route) => {
    previewBody = route.request().postDataJSON();
    await route.fulfill({ json: SUMMARY });
  });
  let applyBody: unknown;
  await page.route("**/api/v1/transfer/imports/*/apply", async (route) => {
    applyBody = route.request().postDataJSON();
    await route.fulfill({ status: 202, json: { ...imported, status: "Running", mode: "Upsert" } });
  });
  await mockJobPolls(page, [
    { ...imported, status: "Succeeded", mode: "Upsert", summary: SUMMARY },
  ]);

  await page.goto("/system/transfer");
  const panel = page.getByRole("region", { name: "Import" });
  await panel.getByLabel("Archive to import").setInputFiles({
    name: "brands.zip",
    mimeType: "application/zip",
    buffer: Buffer.from("PK"),
  });
  await expect(panel.getByText("brands.zip")).toBeVisible();

  await panel.getByRole("group", { name: "Import mode" }).getByRole("button", { name: "Upsert" }).click();
  await panel.getByRole("button", { name: "Preview" }).click();
  const preview = panel.getByRole("table", { name: /Preview/ });
  await expect(preview.getByRole("cell", { name: "2" })).toBeVisible();
  expect(previewBody).toEqual({ mode: "Upsert" });
  await expect(panel.getByRole("table", { name: "Row errors" })).toContainText("Name is required.");

  await panel.getByRole("button", { name: "Apply import" }).click();
  await expect(panel.getByText("Succeeded")).toBeVisible();
  expect(applyBody).toEqual({ mode: "Upsert" });
  await expect(panel.getByRole("table", { name: "Applied with Upsert" })).toBeVisible();
});

test("shows why an uploaded archive was refused", async ({ page }) => {
  await mockProblemDetails(page, "**/api/v1/transfer/imports", 400, {
    title: "Bad Request",
    detail: "The archive has no manifest.json.",
  });

  await page.goto("/system/transfer");
  await page.getByLabel("Archive to import").setInputFiles({
    name: "bad.zip",
    mimeType: "application/zip",
    buffer: Buffer.from("nope"),
  });
  await expect(page.getByText("The archive has no manifest.json.")).toBeVisible();
});

test("stops polling after a failed status fetch and resumes on Retry", async ({ page }) => {
  await page.route("**/api/v1/transfer/exports", (route) => route.fulfill({ status: 202, json: job({}) }));
  let failing = true;
  let polls = 0;
  await page.route("**/api/v1/transfer/jobs/*", (route) => {
    polls++;
    return failing
      ? route.fulfill({ status: 500, contentType: "application/problem+json", body: JSON.stringify({ title: "Server error", status: 500 }) })
      : route.fulfill({ json: job({ status: "Succeeded", sizeBytes: 10 }) });
  });

  await page.goto("/system/transfer");
  const panel = page.getByRole("region", { name: "Export" });
  await panel.getByLabel("catalog.brands").check();
  await panel.getByRole("button", { name: "Start export" }).click();

  // The query's own retries run first, then the failure is shown and polling stops.
  await expect(panel.getByText("Couldn't refresh the status")).toBeVisible({ timeout: 10_000 });
  await expect(panel.getByText("Working…")).toHaveCount(0);
  const settled = polls;
  await page.waitForTimeout(2_500);
  expect(polls).toBe(settled);

  failing = false;
  await panel.getByRole("button", { name: "Retry" }).click();
  await expect(panel.getByText("Succeeded")).toBeVisible();
  await expect(panel.getByText("Couldn't refresh the status")).toHaveCount(0);
});

test("refuses an archive over the upload limit without sending it", async ({ page }) => {
  let uploads = 0;
  await page.route("**/api/v1/transfer/imports", (route) => {
    uploads++;
    return route.fulfill({ status: 500 });
  });

  await page.goto("/system/transfer");
  await expect(page.getByText(/Up to 200\.0 MB/)).toBeVisible();
  // Playwright caps setInputFiles buffers at 50 MB, so fake the size on a tiny File.
  await page.getByLabel("Archive to import").evaluate((input: HTMLInputElement) => {
    const file = new File(["PK"], "huge.zip", { type: "application/zip" });
    Object.defineProperty(file, "size", { value: 300 * 1024 * 1024 });
    Object.defineProperty(input, "files", { value: [file], configurable: true });
    input.dispatchEvent(new Event("change", { bubbles: true }));
  });
  await expect(page.getByText("huge.zip is 300.0 MB; the limit is 200.0 MB.")).toBeVisible();
  expect(uploads).toBe(0);
});
