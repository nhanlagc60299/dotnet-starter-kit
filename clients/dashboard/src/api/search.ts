import { apiFetch } from "@/lib/api-client";

export type PagedResponse<T> = {
  items: T[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasPrevious: boolean;
  hasNext: boolean;
};

/**
 * One hit from GET /api/v1/search. `snippet` wraps matches in `<mark>`…`</mark>`
 * but the rest is the indexed text as-is (NOT HTML-escaped): render it by
 * splitting on the markers (see `splitMarks`), never as HTML.
 */
export type SearchHitDto = {
  entityType: string;
  entityId: string;
  title: string;
  snippet: string | null;
  url: string | null;
  rank: number;
};

export const SEARCH_VIEW_PERMISSION = "Permissions.Search.View";

export function searchDocuments(p: {
  q: string;
  entityType?: string;
  pageNumber: number;
  pageSize: number;
}) {
  const q = new URLSearchParams();
  q.set("q", p.q);
  if (p.entityType) q.set("entityType", p.entityType);
  q.set("pageNumber", String(p.pageNumber));
  q.set("pageSize", String(p.pageSize));
  return apiFetch<PagedResponse<SearchHitDto>>(`/api/v1/search?${q}`);
}

/** Split a server snippet into plain-text runs, flagging the ones inside `<mark>`. */
export function splitMarks(snippet: string): { text: string; mark: boolean }[] {
  const parts: { text: string; mark: boolean }[] = [];
  let mark = false;
  for (const piece of snippet.split(/(<mark>|<\/mark>)/)) {
    if (piece === "<mark>") mark = true;
    else if (piece === "</mark>") mark = false;
    else if (piece) parts.push({ text: piece, mark });
  }
  return parts;
}

/**
 * Where a hit may link to: an app-relative path (`/…`, not `//host`) or an
 * absolute http(s) URL. Anything else (javascript:, data:, garbage) gets no link.
 */
export function safeHitHref(url: string | null): { href: string; external: boolean } | null {
  if (!url) return null;
  if (url.startsWith("/") && !url.startsWith("//") && !url.startsWith("/\\")) {
    return { href: url, external: false };
  }
  try {
    const parsed = new URL(url);
    if (parsed.protocol === "http:" || parsed.protocol === "https:") {
      return { href: parsed.href, external: true };
    }
  } catch {
    /* not a URL */
  }
  return null;
}
