import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { ExternalLink, Loader2, Search, SearchX } from "lucide-react";
import {
  safeHitHref,
  searchDocuments,
  SEARCH_VIEW_PERMISSION,
  splitMarks,
  type SearchHitDto,
} from "@/api/search";
import {
  EntityEmpty,
  EntityFilterPill,
  EntityListLoading,
  EntityPageHeader,
  EntityPager,
  EntitySearch,
  EntityStatusBadge,
  ErrorBand,
  FeatureGateView,
} from "@/components/list";
import { useFeatureGate } from "@/hooks/use-feature-gate";
import { cn } from "@/lib/cn";
import { describe } from "@/lib/list-helpers";

const PAGE_SIZE = 20;
// Short on purpose: long enough to skip a request per keystroke, short enough
// that results feel live. The spinner shows the pending state immediately.
const DEBOUNCE_MS = 200;
// The server folds text like TextNormalizer (lowercase, đ→d, strip accents) and
// keeps only [a-z0-9]; text with nothing left is a 400, so don't send it.
const fold = (s: string) =>
  s.toLowerCase().replace(/đ/g, "d").normalize("NFD").replace(/\p{M}/gu, "");
const HAS_WORD = (s: string) => /[a-z0-9]/.test(fold(s));
// Letters exist but none survive the fold (e.g. Chinese, Cyrillic): explain, don't error.
const HAS_ANY_LETTER = /[\p{L}\p{N}]/u;

export function SearchPage() {
  const gate = useFeatureGate("Search");
  const allowed = gate.has(SEARCH_VIEW_PERMISSION);

  return (
    <div className="space-y-4 sm:space-y-6">
      <EntityPageHeader
        icon={Search}
        title="Search"
        description="Find anything indexed in this workspace. Accents are optional: “hoc sinh” finds “Học sinh”."
      />
      <FeatureGateView gate={gate} allowed={allowed} feature="Search">
        <SearchBody />
      </FeatureGateView>
    </div>
  );
}

function SearchBody() {
  const [text, setText] = useState("");
  const [debounced, setDebounced] = useState("");
  const [entityType, setEntityType] = useState<string | null>(null);
  const [pageNumber, setPageNumber] = useState(1);

  useEffect(() => {
    const t = setTimeout(() => {
      setDebounced(text.trim());
      setPageNumber(1);
    }, DEBOUNCE_MS);
    return () => clearTimeout(t);
  }, [text]);

  const canSearch = HAS_WORD(debounced);
  const query = useQuery({
    queryKey: ["search", { q: debounced, entityType, pageNumber, pageSize: PAGE_SIZE }],
    queryFn: () =>
      searchDocuments({
        q: debounced,
        entityType: entityType ?? undefined,
        pageNumber,
        pageSize: PAGE_SIZE,
      }),
    enabled: canSearch,
    placeholderData: keepPreviousData,
  });

  const data = canSearch ? query.data : undefined;
  const items = data?.items ?? [];
  // Typed but not yet sent, or sent and in flight: show it right away.
  const pending = (text.trim() !== debounced && HAS_WORD(text)) || (canSearch && query.isFetching);

  // No endpoint lists the indexed types, so offer the ones on screen. While
  // filtered, keep the active type so it can be cleared.
  const types = entityType
    ? [entityType]
    : [...new Set(items.map((i) => i.entityType))].sort();
  const showFilter = entityType !== null || types.length > 1;

  const selectType = (next: string | null) => {
    setEntityType(next);
    setPageNumber(1);
  };

  return (
    <>
      <EntitySearch
        value={text}
        onChange={setText}
        placeholder="Search titles and text…"
        autoFocus
      />

      <div className="flex min-h-8 flex-wrap items-center justify-between gap-3">
        {showFilter ? (
          <EntityFilterPill<string | null>
            label="Filter by type"
            value={entityType}
            onChange={selectType}
            options={[
              { value: null, label: "All" },
              ...types.map((t) => ({ value: t, label: t })),
            ]}
          />
        ) : (
          <span />
        )}
        <p
          aria-live="polite"
          className="flex items-center gap-1.5 text-[12px] font-medium text-[var(--color-muted-foreground)]"
        >
          {pending && <Loader2 aria-hidden className="size-3.5 animate-spin" />}
          {pending
            ? "Searching…"
            : data
              ? `${data.totalCount} result${data.totalCount === 1 ? "" : "s"}`
              : ""}
        </p>
      </div>

      {query.isError && canSearch ? (
        <ErrorBand message={describe(query.error)} />
      ) : !canSearch && HAS_ANY_LETTER.test(debounced) ? (
        <EntityEmpty
          icon={SearchX}
          title="Nothing to search for"
          body="Search matches Latin letters and digits (accents are optional), so this text has no searchable word."
        />
      ) : !canSearch ? (
        <EntityEmpty
          icon={Search}
          title="Search your workspace"
          body={
            <>
              Words must all match. Use <Kbd>"exact phrase"</Kbd>, <Kbd>-exclude</Kbd> or{" "}
              <Kbd>or</Kbd>; the last word also matches as a prefix. Only records a module has
              indexed are found, and no module feeds the index yet, so it starts empty.
            </>
          }
        />
      ) : query.isLoading ? (
        <EntityListLoading rows={4} desktopColumns="grid-cols-[1fr_120px]" />
      ) : items.length === 0 ? (
        <EntityEmpty
          icon={SearchX}
          title="No matches"
          body={`Nothing indexed matches “${debounced}”${entityType ? ` in ${entityType}` : ""}. The index only holds what modules send to it.`}
        />
      ) : (
        <div>
          <ul
            aria-label="Search results"
            aria-busy={query.isFetching}
            className={cn(
              "space-y-2 transition-opacity duration-[var(--duration-spring)] ease-[var(--ease-spring)]",
              // Keep the previous page visible but quieter while the next loads.
              query.isPlaceholderData && "opacity-60",
            )}
          >
            {items.map((hit) => (
              <HitRow key={`${hit.entityType}:${hit.entityId}`} hit={hit} />
            ))}
          </ul>
          <EntityPager
            page={data?.pageNumber ?? 1}
            totalPages={data?.totalPages ?? 1}
            hasPrev={!!data?.hasPrevious}
            hasNext={!!data?.hasNext}
            onPrev={() => setPageNumber((p) => Math.max(1, p - 1))}
            onNext={() => setPageNumber((p) => p + 1)}
          />
        </div>
      )}
    </>
  );
}

function Kbd({ children }: { children: React.ReactNode }) {
  return (
    <code className="rounded bg-[var(--color-muted)] px-1 py-0.5 font-mono text-[11.5px] text-[var(--color-foreground)]">
      {children}
    </code>
  );
}

function HitRow({ hit }: { hit: SearchHitDto }) {
  const link = safeHitHref(hit.url);
  const titleClass =
    "text-[14.5px] font-semibold leading-snug tracking-[-0.005em] text-[var(--color-foreground)]";

  return (
    <li
      className={cn(
        "fsh-enter rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] px-4 py-3 shadow-xs",
        "transition-colors duration-[var(--duration-fast)]",
        link && "hover:bg-[oklch(from_var(--color-accent)_l_c_h_/_0.4)]",
      )}
    >
      <div className="flex items-start justify-between gap-3">
        {link ? (
          link.external ? (
            <a
              href={link.href}
              target="_blank"
              rel="noopener noreferrer"
              className={cn(titleClass, "inline-flex items-center gap-1.5 hover:text-[var(--color-primary)]")}
            >
              {hit.title}
              <ExternalLink aria-hidden className="size-3.5 opacity-60" />
            </a>
          ) : (
            <Link to={link.href} className={cn(titleClass, "hover:text-[var(--color-primary)]")}>
              {hit.title}
            </Link>
          )
        ) : (
          <span className={titleClass}>{hit.title}</span>
        )}
        <EntityStatusBadge className="shrink-0 font-mono normal-case tracking-normal">
          {hit.entityType}
        </EntityStatusBadge>
      </div>
      {hit.snippet && (
        <p className="mt-1 line-clamp-3 text-[13px] leading-relaxed text-[var(--color-muted-foreground)]">
          {splitMarks(hit.snippet).map((part, i) =>
            part.mark ? (
              <mark
                key={i}
                className="rounded-[3px] bg-[oklch(from_var(--color-primary)_l_c_h_/_0.14)] px-0.5 font-medium text-[var(--color-foreground)]"
              >
                {part.text}
              </mark>
            ) : (
              <span key={i}>{part.text}</span>
            ),
          )}
        </p>
      )}
    </li>
  );
}
