import { expect, test, type Page, type Route } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

const ROLES = [
  { id: "r-2", name: "Editor", description: null },
  { id: "r-1", name: "Admin", description: null },
];

const ADMIN_GRANTS = [
  { subject: "tickets", action: "read", condition: { createdBy: { $eq: "$CURRENT_USER" } } },
];

/** Serve GET grants per role and capture PUT bodies; `putReply` decides the PUT response. */
async function mockGrants(page: Page, putReply: (route: Route) => Promise<void>) {
  const puts: { role: string; body: unknown }[] = [];
  await page.route("**/api/v1/access-control/roles/*/grants", async (route) => {
    const role = decodeURIComponent(new URL(route.request().url()).pathname.split("/").at(-2)!);
    if (route.request().method() === "PUT") {
      puts.push({ role, body: route.request().postDataJSON() });
      await putReply(route);
      return;
    }
    await route.fulfill({ json: role === "Admin" ? ADMIN_GRANTS : [] });
  });
  return puts;
}

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
  await mockJsonResponse(page, "**/api/v1/identity/permissions", [
    "Permissions.AccessControl.View",
    "Permissions.AccessControl.Manage",
  ]);
  await mockJsonResponse(page, "**/api/v1/identity/roles", ROLES);
});

test("edits a role's grants and saves the whole set", async ({ page }) => {
  const puts = await mockGrants(page, (r) => r.fulfill({ status: 204 }));

  await page.goto("/identity/access");
  await expect(page.getByRole("link", { name: "Access rules" })).toBeVisible();
  const editor = page.getByRole("region", { name: "Grants of Admin" });
  await expect(editor.getByLabel("Subject of grant 1")).toHaveValue("tickets");
  await expect(editor.getByLabel("Condition (JSON)")).toHaveValue(/\$CURRENT_USER/);

  await editor.getByRole("button", { name: "Add grant" }).click();
  // A blank new row disables Save; the reason is spelled out.
  await expect(editor.getByRole("button", { name: "Save rules" })).toBeDisabled();
  await expect(editor.getByText("Fill in subject and action")).toBeVisible();
  await editor.getByLabel("Subject of grant 2").fill("catalog:brands");
  await editor.getByLabel("Action of grant 2").fill("update");
  await editor.getByRole("switch", { name: "Limit grant 2 with a condition" }).click();

  const condition = editor.getByRole("listitem", { name: "Grant 2" }).getByLabel("Condition (JSON)");
  await condition.fill('{"isActive": tru');
  await expect(editor.getByText(/Not valid JSON/)).toBeVisible();
  await expect(editor.getByRole("button", { name: "Save rules" })).toBeDisabled();

  await condition.fill('{"isActive": true}');
  await editor.getByRole("button", { name: "Save rules" }).click();
  await expect(page.getByText("Saved access rules for Admin")).toBeVisible();

  expect(puts).toEqual([
    {
      role: "Admin",
      body: [
        ...ADMIN_GRANTS,
        { subject: "catalog:brands", action: "update", condition: { isActive: true } },
      ],
    },
  ]);
  await expect(editor.getByText("Unsaved")).toHaveCount(0);
});

test("shows the API's validation errors on the grant they belong to", async ({ page }) => {
  await mockGrants(page, (r) =>
    r.fulfill({
      status: 400,
      contentType: "application/problem+json",
      body: JSON.stringify({
        title: "Validation error",
        status: 400,
        errors: { "Grants[1].Condition": ["Unknown operator '$like'."] },
      }),
    }),
  );

  await page.goto("/identity/access");
  await page.getByRole("button", { name: "Editor" }).click();
  const editor = page.getByRole("region", { name: "Grants of Editor" });
  await editor.getByRole("button", { name: "Add grant" }).click();
  await editor.getByLabel("Subject of grant 1").fill("tickets");
  await editor.getByLabel("Action of grant 1").fill("read");
  await editor.getByRole("button", { name: "Add grant" }).click();
  await editor.getByLabel("Subject of grant 2").fill("tickets");
  await editor.getByLabel("Action of grant 2").fill("update");
  await editor.getByRole("switch", { name: "Limit grant 2 with a condition" }).click();
  await editor
    .getByRole("listitem", { name: "Grant 2" })
    .getByLabel("Condition (JSON)")
    .fill('{"title": {"$like": "x"}}');
  await editor.getByRole("button", { name: "Save rules" }).click();

  await expect(editor.getByRole("alert", { name: "Errors on grant 2" })).toHaveText("Unknown operator '$like'.");
  await expect(editor.getByRole("alert", { name: "Errors on grant 1" })).toHaveCount(0);
  // The draft survives the failed save.
  await expect(editor.getByText("Unsaved")).toBeVisible();
});
