import { expect, test, type Route } from "@playwright/test";
import { mockJsonResponse, mockProblemDetails } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks, paged } from "../helpers/shell-mocks";

const HITS = [
  {
    entityType: "catalog.brands",
    entityId: "b-1",
    title: "Học sinh giỏi",
    // The API does not HTML-escape the snippet: only <mark> is meaningful.
    snippet: '<mark>Học</mark> sinh <b>bold</b> <img src=x onerror="window.__xss=1">',
    url: "/catalog/brands",
    rank: 0.9,
  },
  {
    entityType: "tickets",
    entityId: "t-7",
    title: "Hostile link",
    snippet: null,
    url: "javascript:alert(1)",
    rank: 0.5,
  },
];

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
  await mockJsonResponse(page, "**/api/v1/identity/permissions", ["Permissions.Search.View"]);
});

test("searches, highlights matches as text and filters by type", async ({ page }) => {
  const seen: URL[] = [];
  await page.route("**/api/v1/search?**", async (route: Route) => {
    const url = new URL(route.request().url());
    seen.push(url);
    const type = url.searchParams.get("entityType");
    const items = type ? HITS.filter((h) => h.entityType === type) : HITS;
    await route.fulfill({ json: paged(items) });
  });

  await page.goto("/search");
  await expect(page.getByRole("link", { name: "Search", exact: true })).toBeVisible();
  await page.getByPlaceholder("Search titles and text…").fill("hoc");

  const results = page.getByRole("list", { name: "Search results" });
  await expect(results.getByRole("link", { name: "Học sinh giỏi" })).toHaveAttribute("href", "/catalog/brands");
  await expect(results.locator("mark")).toHaveText("Học");
  // Raw markup in the snippet stays text; nothing executes or renders as HTML.
  await expect(results.getByText("<b>bold</b>", { exact: false })).toBeVisible();
  await expect(results.locator("b, img")).toHaveCount(0);
  expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined();
  // A javascript: URL is not rendered as a link.
  await expect(results.getByText("Hostile link")).toBeVisible();
  await expect(results.getByRole("link", { name: "Hostile link" })).toHaveCount(0);
  expect(seen.at(-1)?.searchParams.get("q")).toBe("hoc");

  await page.getByRole("group", { name: "Filter by type" }).getByRole("button", { name: "tickets" }).click();
  await expect(results.getByText("Học sinh giỏi")).toHaveCount(0);
  expect(seen.at(-1)?.searchParams.get("entityType")).toBe("tickets");
});

test("links only same-origin paths and http(s) URLs", async ({ page }) => {
  const hostile = ["/\t/evil.com", "/\n/evil.com", "//evil.com", "/\\evil.com", "javascript:alert(1)", "evil.com",
    // Same-origin on parse, but the normalised path starts with "//".
    "/..//evil.com", "/.//evil.com", "/a/..//evil.com", "/%2e%2e//evil.com"];
  const hit = (title: string, url: string) => ({ entityType: "t", entityId: title, title, snippet: null, url, rank: 1 });
  await mockJsonResponse(page, "**/api/v1/search?**", paged([
    ...hostile.map((url, i) => hit(`Hostile ${i}`, url)),
    hit("Inside", "/catalog/brands?page=2#top"),
    hit("Outside", "https://example.com/docs"),
  ]));

  await page.goto("/search");
  await page.getByPlaceholder("Search titles and text…").fill("x");
  const results = page.getByRole("list", { name: "Search results" });
  await expect(results.getByRole("link", { name: "Inside" })).toHaveAttribute("href", "/catalog/brands?page=2#top");
  const outside = results.getByRole("link", { name: "Outside" });
  await expect(outside).toHaveAttribute("href", "https://example.com/docs");
  await expect(outside).toHaveAttribute("target", "_blank");
  await expect(outside).toHaveAttribute("rel", "noopener noreferrer");
  for (let i = 0; i < hostile.length; i++) {
    await expect(results.getByText(`Hostile ${i}`)).toBeVisible();
    await expect(results.getByRole("link", { name: `Hostile ${i}` })).toHaveCount(0);
  }
  await expect(results.getByRole("link")).toHaveCount(2);
});

test("shows the API's validation message", async ({ page }) => {
  await mockProblemDetails(page, "**/api/v1/search?**", 400, {
    title: "Validation failed",
    errors: { Q: ["Search text must contain at least one word to match."] },
  });

  await page.goto("/search");
  await page.getByPlaceholder("Search titles and text…").fill("-x");
  await expect(page.getByText("Search text must contain at least one word to match.")).toBeVisible();
});

test("explains a switched-off module and hides the nav entry", async ({ page }) => {
  await mockJsonResponse(page, "**/api/v1/modules", [
    { name: "Identity", enabled: true, required: true },
    { name: "Search", enabled: false, required: false },
  ]);

  await page.goto("/search");
  await expect(page.getByRole("heading", { name: "Search is not enabled" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Search", exact: true })).toHaveCount(0);
});
