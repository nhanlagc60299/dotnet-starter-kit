import { useState } from "react";
import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseQueryResult,
} from "@tanstack/react-query";
import {
  ArrowDownUp,
  Check,
  Download,
  FileArchive,
  Loader2,
  Upload,
} from "lucide-react";
import { toast } from "sonner";
import {
  applyImport,
  createExport,
  downloadExport,
  getTransferEntityTypes,
  getTransferJob,
  MAX_UPLOAD_BYTES,
  previewImport,
  TRANSFER_EXPORT_PERMISSION,
  TRANSFER_IMPORT_PERMISSION,
  uploadImport,
  type TransferEntityTypeDto,
  type TransferJobDto,
  type TransferJobStatus,
  type TransferMode,
  type TransferSummary,
} from "@/api/transfer";
import { formatBytes } from "@/hooks/use-file-upload";
import {
  EntityFilterPill,
  EntityListLoading,
  EntityPageHeader,
  EntityStatusBadge,
  ErrorBand,
  FeatureGateView,
  type EntityStatusTone,
} from "@/components/list";
import { Button } from "@/components/ui/button";
import { useFeatureGate } from "@/hooks/use-feature-gate";
import { cn } from "@/lib/cn";
import { describe } from "@/lib/list-helpers";

const POLL_MS = 1_000;

const STATUS_TONE: Record<TransferJobStatus, EntityStatusTone> = {
  Pending: "default",
  Running: "info",
  Succeeded: "success",
  Failed: "danger",
};

const MODES: { value: TransferMode; label: string; hint: string }[] = [
  { value: "CreateOnly", label: "Create only", hint: "Add rows that don't exist yet; leave existing ones untouched." },
  { value: "Upsert", label: "Upsert", hint: "Add new rows and update the ones that already exist." },
];

// ───────────────────────────────────────────────────────────────────────
//  Page
// ───────────────────────────────────────────────────────────────────────

export function TransferPage() {
  const gate = useFeatureGate("Transfer");
  const canExport = gate.has(TRANSFER_EXPORT_PERMISSION);
  const canImport = gate.has(TRANSFER_IMPORT_PERMISSION);

  return (
    <div className="space-y-4 sm:space-y-6">
      <EntityPageHeader
        icon={ArrowDownUp}
        title="Import / export"
        description="Move workspace data in and out as zip archives. Imports create or update rows; they never delete."
      />
      <FeatureGateView gate={gate} allowed={canExport || canImport} feature="Import / export">
        <TransferBody canExport={canExport} canImport={canImport} />
      </FeatureGateView>
    </div>
  );
}

function TransferBody({ canExport, canImport }: { canExport: boolean; canImport: boolean }) {
  const types = useQuery({ queryKey: ["transfer", "entity-types"], queryFn: getTransferEntityTypes });

  if (types.isError) return <ErrorBand message={describe(types.error)} />;
  if (types.isLoading) return <EntityListLoading rows={3} desktopColumns="grid-cols-[1fr_120px]" />;

  const list = types.data ?? [];
  return (
    <div className={cn("grid gap-4", canExport && canImport && "lg:grid-cols-2 lg:items-start")}>
      {canExport && <ExportPanel types={list.filter((t) => t.canExport)} />}
      {canImport && <ImportPanel types={list.filter((t) => t.canImport)} />}
    </div>
  );
}

/** A job, polled while the server is still working on it. */
function useTransferJob(id: string | null, busy: (status: TransferJobStatus) => boolean) {
  return useQuery({
    queryKey: ["transfer", "jobs", id],
    queryFn: () => getTransferJob(id!),
    enabled: id !== null,
    // Stop on an error (shown below the panel) instead of hammering a 403/404 every second.
    refetchInterval: (q) =>
      q.state.data && !q.state.error && busy(q.state.data.status) ? POLL_MS : false,
  });
}

// ───────────────────────────────────────────────────────────────────────
//  Export
// ───────────────────────────────────────────────────────────────────────

function ExportPanel({ types }: { types: TransferEntityTypeDto[] }) {
  const qc = useQueryClient();
  const [selected, setSelected] = useState<string[]>([]);
  const [jobId, setJobId] = useState<string | null>(null);
  // An export is queued (Pending) then Running; both mean "keep polling".
  const job = useTransferJob(jobId, (s) => s === "Pending" || s === "Running");

  const start = useMutation({
    mutationFn: (entityTypes: string[]) => createExport(entityTypes),
    onSuccess: (created) => {
      qc.setQueryData(["transfer", "jobs", created.id], created);
      setJobId(created.id);
    },
    onError: (e) => toast.error(describe(e)),
  });
  const download = useMutation({
    mutationFn: (id: string) => downloadExport(id),
    onError: (e) => toast.error(describe(e)),
  });

  const toggle = (type: string) =>
    setSelected((s) => (s.includes(type) ? s.filter((x) => x !== type) : [...s, type]));
  const current = job.data;
  const running = current ? current.status === "Pending" || current.status === "Running" : false;

  return (
    <Panel title="Export" description="Pick what to export. The archive is built in the background.">
      {types.length === 0 ? (
        <p className="text-[13px] text-[var(--color-muted-foreground)]">
          Nothing you can export yet: no module offers an exportable type you have access to.
        </p>
      ) : (
        <fieldset className="space-y-1.5">
          <legend className="sr-only">Entity types to export</legend>
          {types.map((t) => (
            <label
              key={t.entityType}
              className="flex cursor-pointer items-center gap-3 rounded-lg px-2 py-1.5 transition-colors duration-[var(--duration-fast)] hover:bg-[var(--color-accent)]"
            >
              <input
                type="checkbox"
                checked={selected.includes(t.entityType)}
                onChange={() => toggle(t.entityType)}
                className="size-4 accent-[var(--color-primary)]"
              />
              <span className="font-mono text-[13px]">{t.entityType}</span>
            </label>
          ))}
        </fieldset>
      )}

      <div className="flex flex-wrap items-center gap-2">
        <Button
          size="sm"
          disabled={selected.length === 0 || start.isPending || running}
          onClick={() => start.mutate([...selected].sort())}
        >
          {start.isPending ? <Loader2 className="animate-spin" /> : <FileArchive />}
          Start export
        </Button>
      </div>

      {current && (
        <JobCard job={current} poll={job}>
          {current.status === "Succeeded" && (
            <>
              <p className="text-[12.5px] text-[var(--color-muted-foreground)]">
                {formatBytes(current.sizeBytes)}
                {current.sha256 && (
                  <>
                    {" · SHA-256 "}
                    <code className="font-mono" title={current.sha256}>
                      {current.sha256.slice(0, 12)}…
                    </code>
                  </>
                )}
              </p>
              {current.summary && <RowsPerType rows={current.summary.rows} />}
              <Button
                size="sm"
                variant="outline"
                disabled={download.isPending}
                onClick={() => download.mutate(current.id)}
              >
                {download.isPending ? <Loader2 className="animate-spin" /> : <Download />}
                Download archive
              </Button>
            </>
          )}
        </JobCard>
      )}
      {job.isError && !current && <ErrorBand message={describe(job.error)} />}
    </Panel>
  );
}

// ───────────────────────────────────────────────────────────────────────
//  Import: upload → mode → preview → apply → status
// ───────────────────────────────────────────────────────────────────────

type PreviewState = { jobId: string; mode: TransferMode; summary: TransferSummary };

function ImportPanel({ types }: { types: TransferEntityTypeDto[] }) {
  const qc = useQueryClient();
  const [jobId, setJobId] = useState<string | null>(null);
  const [mode, setMode] = useState<TransferMode>("CreateOnly");
  const [preview, setPreview] = useState<PreviewState | null>(null);
  const [tooBig, setTooBig] = useState<string | null>(null);
  // For an import, Pending is "uploaded, waiting for apply": only Running polls.
  const job = useTransferJob(jobId, (s) => s === "Running");
  const current = job.data;

  const upload = useMutation({
    mutationFn: (file: File) => uploadImport(file),
    onSuccess: (created) => {
      qc.setQueryData(["transfer", "jobs", created.id], created);
      setJobId(created.id);
      setPreview(null);
    },
  });
  const runPreview = useMutation({
    mutationFn: (v: { jobId: string; mode: TransferMode }) => previewImport(v.jobId, v.mode),
    onSuccess: (summary, v) => setPreview({ ...v, summary }),
  });
  const apply = useMutation({
    mutationFn: (v: { jobId: string; mode: TransferMode }) => applyImport(v.jobId, v.mode),
    onSuccess: (started) => qc.setQueryData(["transfer", "jobs", started.id], started),
  });

  const reset = () => {
    setJobId(null);
    setPreview(null);
    upload.reset();
    runPreview.reset();
    apply.reset();
  };

  const applied = current !== undefined && current.status !== "Pending";
  // A failed import can be applied again (the API allows Pending or Failed).
  const canApply = current !== undefined && (current.status === "Pending" || current.status === "Failed");
  const previewHere = preview && preview.jobId === jobId && preview.mode === mode ? preview : null;
  const step = !current ? 1 : !previewHere && !applied ? 2 : !applied ? 3 : 4;

  return (
    <Panel title="Import" description="Upload an archive, preview what it would change, then apply it.">
      <ol className="space-y-5">
        <Step n={1} title="Upload archive" active={step === 1} done={step > 1}>
          {current ? (
            <p className="flex flex-wrap items-center gap-2 text-[13px]">
              <FileArchive aria-hidden className="size-4 text-[var(--color-muted-foreground)]" />
              <span className="font-medium">{current.fileName ?? "archive.zip"}</span>
              <span className="text-[var(--color-muted-foreground)]">
                {formatBytes(current.sizeBytes)} · {current.entityTypes.join(", ")}
              </span>
              <Button variant="link" size="xs" onClick={reset}>
                Start over
              </Button>
            </p>
          ) : (
            <>
              <label
                className={cn(
                  "flex cursor-pointer flex-col items-center gap-2 rounded-xl border border-dashed border-[var(--color-border-strong)] px-4 py-6 text-center",
                  "transition-colors duration-[var(--duration-fast)] hover:bg-[var(--color-accent)]",
                  "focus-within:ring-[3px] focus-within:ring-[oklch(from_var(--color-ring)_l_c_h_/_0.4)]",
                  upload.isPending && "pointer-events-none opacity-70",
                )}
              >
                {upload.isPending ? (
                  <Loader2 aria-hidden className="size-5 animate-spin text-[var(--color-muted-foreground)]" />
                ) : (
                  <Upload aria-hidden className="size-5 text-[var(--color-muted-foreground)]" />
                )}
                <span className="text-[13px] font-medium">
                  {upload.isPending ? "Checking archive…" : "Choose a .zip archive"}
                </span>
                <span className="text-[11.5px] text-[var(--color-muted-foreground)]">
                  Up to {formatBytes(MAX_UPLOAD_BYTES)} ·{" "}
                  {types.length > 0
                    ? `importable here: ${types.map((t) => t.entityType).join(", ")}`
                    : "no type is importable for you right now."}
                </span>
                <input
                  type="file"
                  accept=".zip,application/zip"
                  aria-label="Archive to import"
                  className="sr-only"
                  disabled={upload.isPending}
                  onChange={(e) => {
                    const file = e.target.files?.[0];
                    e.target.value = "";
                    if (!file) return;
                    if (file.size > MAX_UPLOAD_BYTES) {
                      upload.reset();
                      setTooBig(
                        `${file.name} is ${formatBytes(file.size)}; the limit is ${formatBytes(MAX_UPLOAD_BYTES)}.`,
                      );
                      return;
                    }
                    setTooBig(null);
                    upload.mutate(file);
                  }}
                />
              </label>
              {tooBig && <ErrorBand message={tooBig} />}
              {upload.isError && <ErrorBand message={describe(upload.error)} />}
            </>
          )}
        </Step>

        <Step n={2} title="Choose a mode and preview" active={step === 2} done={step > 2}>
          {current && !applied && (
            <div className="space-y-3">
              <EntityFilterPill<TransferMode>
                label="Import mode"
                value={mode}
                onChange={setMode}
                options={MODES}
              />
              <p className="text-[12.5px] text-[var(--color-muted-foreground)]">
                {MODES.find((m) => m.value === mode)?.hint}
              </p>
              <Button
                size="sm"
                variant="outline"
                disabled={runPreview.isPending}
                onClick={() => runPreview.mutate({ jobId: current.id, mode })}
              >
                {runPreview.isPending && <Loader2 className="animate-spin" />}
                Preview
              </Button>
              {runPreview.isError && <ErrorBand message={describe(runPreview.error)} />}
            </div>
          )}
        </Step>

        <Step n={3} title="Review and apply" active={step === 3} done={step > 3}>
          {previewHere && !applied && (
            <div className="space-y-3">
              <SummaryView summary={previewHere.summary} caption="Preview: nothing has been written yet" />
              <Button
                size="sm"
                disabled={!canApply || apply.isPending}
                onClick={() => apply.mutate({ jobId: previewHere.jobId, mode: previewHere.mode })}
              >
                {apply.isPending && <Loader2 className="animate-spin" />}
                Apply import
              </Button>
              {apply.isError && <ErrorBand message={describe(apply.error)} />}
            </div>
          )}
        </Step>

        <Step n={4} title="Status" active={step === 4} done={current?.status === "Succeeded"}>
          {current && applied && (
            <JobCard job={current} poll={job}>
              {current.summary && current.status !== "Running" && (
                <SummaryView summary={current.summary} caption={`Applied with ${current.mode ?? mode}`} />
              )}
              {current.status === "Failed" && (
                <Button
                  size="sm"
                  variant="outline"
                  disabled={apply.isPending}
                  onClick={() => apply.mutate({ jobId: current.id, mode: current.mode ?? mode })}
                >
                  Retry import
                </Button>
              )}
            </JobCard>
          )}
        </Step>
      </ol>
      {job.isError && !current && <ErrorBand message={describe(job.error)} />}
    </Panel>
  );
}

// ───────────────────────────────────────────────────────────────────────
//  Pieces
// ───────────────────────────────────────────────────────────────────────

function Panel({
  title,
  description,
  children,
}: {
  title: string;
  description: string;
  children: React.ReactNode;
}) {
  return (
    <section
      aria-label={title}
      className="space-y-4 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5 shadow-xs"
    >
      <header>
        <h2 className="font-display text-[16px] font-semibold tracking-tight">{title}</h2>
        <p className="mt-0.5 text-[12.5px] text-[var(--color-muted-foreground)]">{description}</p>
      </header>
      {children}
    </section>
  );
}

function Step({
  n,
  title,
  active,
  done,
  children,
}: {
  n: number;
  title: string;
  active: boolean;
  done: boolean;
  children: React.ReactNode;
}) {
  return (
    <li className="flex gap-3" aria-current={active ? "step" : undefined}>
      <span
        aria-hidden
        className={cn(
          "grid size-6 shrink-0 place-items-center rounded-full text-[11px] font-semibold",
          "transition-[background-color,color,transform] duration-[var(--duration-spring)] ease-[var(--ease-spring)]",
          done
            ? "bg-[var(--color-primary)] text-[var(--color-primary-foreground)]"
            : active
              ? "scale-110 bg-[var(--color-primary-soft)] text-[var(--color-primary)]"
              : "bg-[var(--color-muted)] text-[var(--color-muted-foreground)]",
        )}
      >
        {done ? <Check className="size-3.5" /> : n}
      </span>
      <div className="min-w-0 flex-1 space-y-2 pt-0.5">
        <h3
          className={cn(
            "text-[13.5px] font-semibold transition-colors duration-[var(--duration-default)]",
            active || done ? "text-[var(--color-foreground)]" : "text-[var(--color-muted-foreground)]",
          )}
        >
          {title}
        </h3>
        {children}
      </div>
    </li>
  );
}

function JobCard({
  job,
  poll,
  children,
}: {
  job: TransferJobDto;
  /** The polling query, so a failed refresh can be shown and retried here. */
  poll: UseQueryResult<TransferJobDto>;
  children?: React.ReactNode;
}) {
  const busy = job.status === "Running" || (job.kind === "Export" && job.status === "Pending");
  // Polling stops after a failed fetch; Retry refetches, and a success resumes it.
  const pollFailed = poll.isError && !poll.isFetching;
  return (
    <div className="fsh-enter space-y-2.5 rounded-lg border border-[var(--color-border)] bg-[oklch(from_var(--color-muted)_l_c_h_/_0.35)] p-3.5">
      <div className="flex flex-wrap items-center gap-2">
        <span aria-live="polite">
          <EntityStatusBadge tone={STATUS_TONE[job.status]} withDot>
            {job.status}
          </EntityStatusBadge>
        </span>
        {busy && !pollFailed && (
          <Loader2 aria-hidden className="size-3.5 animate-spin text-[var(--color-muted-foreground)]" />
        )}
        <span className="text-[12px] text-[var(--color-muted-foreground)]">
          {pollFailed
            ? "Status updates paused."
            : busy
              ? "Working… this page updates by itself."
              : job.entityTypes.join(", ")}
        </span>
      </div>
      {pollFailed && (
        <div role="alert" className="flex flex-wrap items-center gap-2 text-[12.5px] text-[var(--color-destructive)]">
          <span>Couldn't refresh the status: {describe(poll.error)}</span>
          <Button size="xs" variant="outline" onClick={() => void poll.refetch()}>
            Retry
          </Button>
        </div>
      )}
      {job.status === "Failed" && job.error && (
        <p className="text-[12.5px] text-[var(--color-destructive)]">{job.error}</p>
      )}
      {children}
    </div>
  );
}

function RowsPerType({ rows }: { rows: Record<string, number> }) {
  const entries = Object.entries(rows);
  if (entries.length === 0) return null;
  return (
    <ul className="flex flex-wrap gap-1.5">
      {entries.map(([type, count]) => (
        <li
          key={type}
          className="rounded-md bg-[var(--color-card)] px-2 py-0.5 font-mono text-[11.5px] ring-1 ring-inset ring-[var(--color-border)]"
        >
          {type} · {count} row{count === 1 ? "" : "s"}
        </li>
      ))}
    </ul>
  );
}

const TILES: { key: "created" | "updated" | "skipped" | "failed"; label: string; tone: string }[] = [
  { key: "created", label: "Create", tone: "text-[var(--color-success)]" },
  { key: "updated", label: "Update", tone: "text-[var(--color-info)]" },
  { key: "skipped", label: "Skip", tone: "text-[var(--color-muted-foreground)]" },
  { key: "failed", label: "Error", tone: "text-[var(--color-destructive)]" },
];

function SummaryView({ summary, caption }: { summary: TransferSummary; caption: string }) {
  return (
    <div className="fsh-enter space-y-3">
      <table className="w-full table-fixed border-separate border-spacing-1.5 text-center" aria-label={caption}>
        <caption className="pb-1 text-left text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]">
          {caption}
        </caption>
        <thead>
          <tr>
            {TILES.map((t) => (
              <th key={t.key} scope="col" className="text-[11px] font-medium text-[var(--color-muted-foreground)]">
                {t.label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          <tr>
            {TILES.map((t) => (
              <td
                key={t.key}
                className={cn(
                  "rounded-lg bg-[var(--color-card)] py-2 font-display text-[20px] font-semibold tabular-nums tracking-tight ring-1 ring-inset ring-[var(--color-border)]",
                  t.tone,
                )}
              >
                {summary[t.key]}
              </td>
            ))}
          </tr>
        </tbody>
      </table>
      <RowsPerType rows={summary.rows} />
      {summary.errors.length > 0 && (
        <div className="max-h-56 overflow-auto rounded-lg border border-[oklch(from_var(--color-destructive)_l_c_h_/_0.3)]">
          <table className="w-full text-left text-[12.5px]" aria-label="Row errors">
            <thead className="sticky top-0 bg-[var(--color-card)] text-[11px] uppercase tracking-wider text-[var(--color-muted-foreground)]">
              <tr>
                <th scope="col" className="px-3 py-1.5 font-semibold">Type</th>
                <th scope="col" className="px-3 py-1.5 font-semibold">Row</th>
                <th scope="col" className="px-3 py-1.5 font-semibold">Error</th>
              </tr>
            </thead>
            <tbody>
              {summary.errors.map((e, i) => (
                <tr key={i} className="border-t border-[oklch(from_var(--color-border)_l_c_h_/_0.5)]">
                  <td className="px-3 py-1.5 font-mono">{e.entityType}</td>
                  <td className="px-3 py-1.5 tabular-nums">{e.row}</td>
                  <td className="px-3 py-1.5 text-[var(--color-destructive)]">{e.message}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {summary.failed > summary.errors.length && (
            <p className="px-3 py-1.5 text-[11.5px] text-[var(--color-muted-foreground)]">
              Showing the first {summary.errors.length} of {summary.failed} errors.
            </p>
          )}
        </div>
      )}
    </div>
  );
}
