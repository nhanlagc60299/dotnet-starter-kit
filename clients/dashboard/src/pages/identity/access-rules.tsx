import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { KeyRound, Plus, ShieldCheck, Trash2 } from "lucide-react";
import { toast } from "sonner";
import {
  ACCESS_CONTROL_MANAGE_PERMISSION,
  ACCESS_CONTROL_VIEW_PERMISSION,
  getRoleGrants,
  putRoleGrants,
  type AccessGrantDto,
} from "@/api/access-control";
import { listRoles } from "@/api/identity";
import {
  EntityEmpty,
  EntityListLoading,
  EntityPageHeader,
  EntityStatusBadge,
  ErrorBand,
  FeatureGateView,
} from "@/components/list";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { useFeatureGate } from "@/hooks/use-feature-gate";
import { ApiRequestError } from "@/lib/api-client";
import { cn } from "@/lib/cn";
import { describe } from "@/lib/list-helpers";

// Mirrors UpdateRoleGrantsCommandValidator so mistakes show while typing; the
// server stays the authority (it also checks operators and variables).
const SUBJECT_RE = /^[a-z][a-z0-9-]*(:[a-z0-9*-]+)?$/;
const ACTION_RE = /^[a-z][a-zA-Z]*$/;
const MAX_CONDITION_LENGTH = 8_000;
const CONDITION_EXAMPLE = '{ "createdBy": { "$eq": "$CURRENT_USER" } }';

/** Editable copy of a grant. `id` only keys React rows and maps server errors back. */
type Row = {
  id: string;
  subject: string;
  action: string;
  conditionOn: boolean;
  conditionText: string;
};

type RowErrors = Partial<Record<"subject" | "action" | "condition", string>>;

/** Server errors from the last failed save of a role, mapped onto row ids. */
type SaveErrors = { role: string; rows: Record<string, string[]>; general: string[] };

function toRow(g: AccessGrantDto): Row {
  const on = g.condition !== null && Object.keys(g.condition).length > 0;
  return {
    id: crypto.randomUUID(),
    subject: g.subject,
    action: g.action,
    conditionOn: on,
    conditionText: on ? JSON.stringify(g.condition, null, 2) : "",
  };
}

function parseCondition(text: string): { value?: Record<string, unknown>; error?: string } {
  if (!text.trim()) return { error: `Enter a JSON object, e.g. ${CONDITION_EXAMPLE}` };
  if (text.length > MAX_CONDITION_LENGTH) return { error: `At most ${MAX_CONDITION_LENGTH} characters.` };
  try {
    const value: unknown = JSON.parse(text);
    if (value === null || typeof value !== "object" || Array.isArray(value)) {
      return { error: "The condition must be a JSON object." };
    }
    return { value: value as Record<string, unknown> };
  } catch (e) {
    return { error: `Not valid JSON: ${e instanceof Error ? e.message : String(e)}` };
  }
}

function toDto(r: Row): AccessGrantDto {
  return {
    subject: r.subject.trim(),
    action: r.action.trim(),
    condition: r.conditionOn ? (parseCondition(r.conditionText).value ?? null) : null,
  };
}

function validateRows(rows: Row[]): Record<string, RowErrors> {
  const out: Record<string, RowErrors> = {};
  const seen = new Set<string>();
  for (const r of rows) {
    const e: RowErrors = {};
    const subject = r.subject.trim();
    const action = r.action.trim();
    if (!subject) e.subject = "Required.";
    else if (!SUBJECT_RE.test(subject))
      e.subject = "Lowercase letters, digits and '-', optionally ':name' or ':*'.";
    if (!action) e.action = "Required.";
    else if (!ACTION_RE.test(action)) e.action = "A camelCase word, e.g. read or readDraft.";
    if (r.conditionOn) {
      const c = parseCondition(r.conditionText);
      if (c.error) e.condition = c.error;
    }
    const key = `${subject}\u0000${action}`;
    if (subject && action && seen.has(key)) e.action = "This subject already has this action.";
    seen.add(key);
    if (Object.keys(e).length > 0) out[r.id] = e;
  }
  return out;
}

/** Map FluentValidation keys like "Grants[2].Condition" back to the rows that were sent. */
function mapSaveErrors(err: unknown, role: string, sent: Row[]): SaveErrors {
  const result: SaveErrors = { role, rows: {}, general: [] };
  const errors = err instanceof ApiRequestError ? err.problem?.errors : undefined;
  if (errors && typeof errors === "object" && !Array.isArray(errors)) {
    for (const [key, value] of Object.entries(errors as Record<string, unknown>)) {
      const messages = (Array.isArray(value) ? value : [value]).filter(
        (m): m is string => typeof m === "string",
      );
      const match = /^grants\[(\d+)\]/i.exec(key);
      const row = match ? sent[Number(match[1])] : undefined;
      if (row) result.rows[row.id] = [...(result.rows[row.id] ?? []), ...messages];
      else result.general.push(...messages);
    }
  }
  if (Object.keys(result.rows).length === 0 && result.general.length === 0) {
    result.general.push(describe(err));
  }
  return result;
}

const canonical = (rows: Row[]) => JSON.stringify(rows.map(toDto));

function without<T>(map: Record<string, T>, key: string): Record<string, T> {
  const next = { ...map };
  delete next[key];
  return next;
}

// ───────────────────────────────────────────────────────────────────────
//  Page
// ───────────────────────────────────────────────────────────────────────

export function AccessRulesPage() {
  const gate = useFeatureGate("AccessControl");
  return (
    <div className="space-y-4 sm:space-y-6">
      <EntityPageHeader
        icon={KeyRound}
        title="Access rules"
        description="What each role may do on a subject, optionally limited to the rows a condition matches."
      />
      <FeatureGateView
        gate={gate}
        allowed={gate.has(ACCESS_CONTROL_VIEW_PERMISSION)}
        feature="Access control"
      >
        <AccessRulesBody canManage={gate.has(ACCESS_CONTROL_MANAGE_PERMISSION)} />
      </FeatureGateView>
    </div>
  );
}

function AccessRulesBody({ canManage }: { canManage: boolean }) {
  const rolesQuery = useQuery({ queryKey: ["identity", "roles"], queryFn: listRoles });
  const roles = useMemo(
    () => [...(rolesQuery.data ?? [])].sort((a, b) => a.name.localeCompare(b.name)),
    [rolesQuery.data],
  );
  const [picked, setPicked] = useState<string | null>(null);
  const role = picked ?? roles[0]?.name ?? null;

  if (rolesQuery.isError) return <ErrorBand message={describe(rolesQuery.error)} />;
  if (rolesQuery.isLoading) return <EntityListLoading rows={4} desktopColumns="grid-cols-[1fr_120px]" />;
  if (!role)
    return <EntityEmpty icon={ShieldCheck} title="No roles yet" body="Create a role first, then give it access rules." />;

  return (
    <div className="grid gap-4 md:grid-cols-[220px_1fr] md:items-start">
      <nav
        aria-label="Roles"
        className="flex gap-1 overflow-x-auto rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-1.5 shadow-xs md:flex-col md:overflow-visible"
      >
        {roles.map((r) => {
          const active = r.name === role;
          return (
            <button
              key={r.id}
              type="button"
              aria-current={active ? "true" : undefined}
              onClick={() => setPicked(r.name)}
              className={cn(
                "shrink-0 cursor-pointer rounded-lg px-3 py-2 text-left text-[13px] font-medium",
                "transition-colors duration-[var(--duration-fast)] active:scale-[0.98]",
                active
                  ? "bg-[var(--color-primary-soft)] text-[var(--color-primary)]"
                  : "text-[var(--color-muted-foreground)] hover:bg-[var(--color-accent)] hover:text-[var(--color-foreground)]",
              )}
            >
              {r.name}
            </button>
          );
        })}
      </nav>
      <GrantsEditor role={role} canManage={canManage} />
    </div>
  );
}

// ───────────────────────────────────────────────────────────────────────
//  Grants editor
// ───────────────────────────────────────────────────────────────────────

function GrantsEditor({ role, canManage }: { role: string; canManage: boolean }) {
  const qc = useQueryClient();
  const grantsQuery = useQuery({
    queryKey: ["access-control", "grants", role],
    queryFn: () => getRoleGrants(role),
  });
  const serverRows = useMemo(() => (grantsQuery.data ?? []).map(toRow), [grantsQuery.data]);

  // Unsaved edits per role, so switching roles never throws work away.
  const [drafts, setDrafts] = useState<Record<string, Row[]>>({});
  const [saveErrors, setSaveErrors] = useState<SaveErrors | null>(null);

  const rows = drafts[role] ?? serverRows;
  const dirty = drafts[role] !== undefined && canonical(drafts[role]) !== canonical(serverRows);
  const clientErrors = validateRows(rows);
  const hasClientErrors = Object.keys(clientErrors).length > 0;
  const errorsHere = saveErrors?.role === role ? saveErrors : null;

  const edit = (next: Row[], touchedId?: string) => {
    setDrafts((d) => ({ ...d, [role]: next }));
    // A field being fixed drops its stale server message right away.
    if (touchedId && errorsHere?.rows[touchedId]) {
      setSaveErrors({ ...errorsHere, rows: without(errorsHere.rows, touchedId) });
    }
  };
  const patch = (id: string, change: Partial<Row>) =>
    edit(rows.map((r) => (r.id === id ? { ...r, ...change } : r)), id);

  const save = useMutation({
    mutationFn: (v: { role: string; rows: Row[] }) => putRoleGrants(v.role, v.rows.map(toDto)),
    onSuccess: (_void, v) => {
      toast.success(`Saved access rules for ${v.role}`);
      qc.setQueryData(["access-control", "grants", v.role], v.rows.map(toDto));
      setDrafts((d) => without(d, v.role));
      setSaveErrors((e) => (e?.role === v.role ? null : e));
      void qc.invalidateQueries({ queryKey: ["access-control", "grants", v.role] });
    },
    onError: (err, v) => setSaveErrors(mapSaveErrors(err, v.role, v.rows)),
  });

  if (grantsQuery.isError) return <ErrorBand message={describe(grantsQuery.error)} />;
  if (grantsQuery.isLoading) return <EntityListLoading rows={3} desktopColumns="grid-cols-[1fr_1fr_120px]" />;

  return (
    <section
      aria-label={`Grants of ${role}`}
      className="rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] shadow-xs"
    >
      <header className="flex flex-wrap items-center justify-between gap-3 border-b border-[var(--color-border)] px-5 py-3.5">
        <div className="flex min-w-0 items-baseline gap-2">
          <h2 className="truncate font-display text-[16px] font-semibold tracking-tight">{role}</h2>
          <span className="font-mono text-[11px] text-[var(--color-muted-foreground)]">
            {rows.length} grant{rows.length === 1 ? "" : "s"}
          </span>
          {dirty && <EntityStatusBadge tone="warning" withDot>Unsaved</EntityStatusBadge>}
        </div>
        {canManage && (
          <div className="flex gap-2">
            <Button
              variant="ghost"
              size="sm"
              disabled={!dirty || save.isPending}
              onClick={() => {
                setDrafts((d) => without(d, role));
                setSaveErrors(null);
              }}
            >
              Discard
            </Button>
            <Button
              size="sm"
              disabled={!dirty || hasClientErrors || save.isPending}
              onClick={() => save.mutate({ role, rows })}
            >
              {save.isPending ? "Saving…" : "Save rules"}
            </Button>
          </div>
        )}
      </header>

      {errorsHere && errorsHere.general.length > 0 && (
        <div className="px-5 pt-4" role="alert">
          <ErrorBand message={errorsHere.general.join(" ")} />
        </div>
      )}

      {rows.length === 0 ? (
        <EntityEmpty
          icon={ShieldCheck}
          title="No grants"
          body="Members of this role reach no rows guarded by access rules."
          action={
            canManage && (
              <Button variant="outline" size="sm" onClick={() => edit([newRow()])}>
                <Plus /> Add grant
              </Button>
            )
          }
        />
      ) : (
        <ul className="divide-y divide-[oklch(from_var(--color-border)_l_c_h_/_0.5)]">
          {rows.map((r, i) => (
            <GrantRow
              key={r.id}
              index={i}
              row={r}
              readOnly={!canManage}
              errors={clientErrors[r.id]}
              serverErrors={errorsHere?.rows[r.id]}
              onChange={(change) => patch(r.id, change)}
              onRemove={() => edit(rows.filter((x) => x.id !== r.id), r.id)}
            />
          ))}
        </ul>
      )}

      {canManage && rows.length > 0 && (
        <div className="border-t border-[var(--color-border)] px-5 py-3">
          <Button variant="ghost" size="sm" onClick={() => edit([...rows, newRow()])}>
            <Plus /> Add grant
          </Button>
        </div>
      )}
    </section>
  );
}

function newRow(): Row {
  return { id: crypto.randomUUID(), subject: "", action: "", conditionOn: false, conditionText: "" };
}

function GrantRow({
  index,
  row,
  readOnly,
  errors,
  serverErrors,
  onChange,
  onRemove,
}: {
  index: number;
  row: Row;
  readOnly: boolean;
  errors?: RowErrors;
  serverErrors?: string[];
  onChange: (change: Partial<Row>) => void;
  onRemove: () => void;
}) {
  const n = index + 1;
  // Empty fields of a fresh row are not "errors" yet; say so only once typed.
  const show = (field: "subject" | "action") => (row[field].trim() ? errors?.[field] : undefined);
  const conditionId = `grant-${row.id}-condition`;

  return (
    <li className="px-5 py-4" aria-label={`Grant ${n}`}>
      <div className="grid gap-3 sm:grid-cols-[1fr_1fr_auto_auto] sm:items-start">
        <FieldBox label="Subject" error={show("subject")}>
          <Input
            aria-label={`Subject of grant ${n}`}
            value={row.subject}
            onChange={(e) => onChange({ subject: e.target.value })}
            placeholder="tickets"
            spellCheck={false}
            disabled={readOnly}
            aria-invalid={!!show("subject")}
            className="font-mono text-[13px]"
          />
        </FieldBox>
        <FieldBox label="Action" error={show("action")}>
          <Input
            aria-label={`Action of grant ${n}`}
            value={row.action}
            onChange={(e) => onChange({ action: e.target.value })}
            placeholder="read"
            spellCheck={false}
            disabled={readOnly}
            aria-invalid={!!show("action")}
            className="font-mono text-[13px]"
          />
        </FieldBox>
        <div className="flex h-9 items-center gap-2 sm:mt-[22px]">
          <Switch
            checked={row.conditionOn}
            onCheckedChange={(on) => onChange({ conditionOn: on })}
            disabled={readOnly}
            aria-label={`Limit grant ${n} with a condition`}
          />
          <span className="text-[12.5px] text-[var(--color-muted-foreground)]">
            {row.conditionOn ? "Only where…" : "All rows"}
          </span>
        </div>
        {!readOnly && (
          <Button
            variant="ghost"
            size="icon-sm"
            className="sm:mt-[22px]"
            aria-label={`Remove grant ${n}`}
            onClick={onRemove}
          >
            <Trash2 />
          </Button>
        )}
      </div>

      {/* Grid-rows 0fr→1fr on the spring curve: a CSS transition, so toggling
          mid-flight reverses from where it is instead of jumping. */}
      <div
        className={cn(
          "grid transition-[grid-template-rows,opacity] duration-[var(--duration-spring)] ease-[var(--ease-spring)]",
          row.conditionOn ? "grid-rows-[1fr] opacity-100" : "grid-rows-[0fr] opacity-0",
        )}
        inert={!row.conditionOn}
      >
        <div className="min-h-0 overflow-hidden">
          <div className="space-y-1.5 pt-3">
            <label
              htmlFor={conditionId}
              className="text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]"
            >
              Condition (JSON)
            </label>
            <textarea
              id={conditionId}
              value={row.conditionText}
              onChange={(e) => onChange({ conditionText: e.target.value })}
              placeholder={CONDITION_EXAMPLE}
              rows={4}
              spellCheck={false}
              disabled={readOnly}
              aria-invalid={!!errors?.condition}
              aria-describedby={`${conditionId}-help`}
              className={cn(
                "block w-full rounded-lg border border-[var(--color-input)] bg-transparent px-3 py-2 font-mono text-[12.5px] leading-relaxed shadow-xs",
                "placeholder:text-[oklch(from_var(--color-muted-foreground)_l_c_h_/_0.6)]",
                "focus-visible:border-[var(--color-ring)] focus-visible:outline-none focus-visible:ring-[3px] focus-visible:ring-[oklch(from_var(--color-ring)_l_c_h_/_0.5)]",
                "aria-invalid:border-[var(--color-destructive)]",
                "disabled:opacity-60",
              )}
            />
            <p id={`${conditionId}-help`} className="text-[11.5px] leading-relaxed text-[var(--color-muted-foreground)]">
              {errors?.condition ? (
                <span className="text-[var(--color-destructive)]">{errors.condition}</span>
              ) : (
                <>
                  <code className="font-mono">$eq $ne $in $nin $gt $gte $lt $lte $null $and $or $not</code> ·
                  variables <code className="font-mono">$CURRENT_USER</code>,{" "}
                  <code className="font-mono">$CURRENT_ROLES</code>
                </>
              )}
            </p>
          </div>
        </div>
      </div>

      {serverErrors && serverErrors.length > 0 && (
        <ul
          role="alert"
          aria-label={`Errors on grant ${n}`}
          className="mt-3 space-y-0.5 rounded-lg border border-[oklch(from_var(--color-destructive)_l_c_h_/_0.3)] bg-[oklch(from_var(--color-destructive)_l_c_h_/_0.06)] px-3 py-2 text-[12.5px] text-[var(--color-destructive)]"
        >
          {serverErrors.map((m, i) => (
            <li key={i}>{m}</li>
          ))}
        </ul>
      )}
    </li>
  );
}

function FieldBox({
  label,
  error,
  children,
}: {
  label: string;
  error?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="min-w-0 space-y-1">
      <span aria-hidden className="block text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]">
        {label}
      </span>
      {children}
      {error && <p className="text-[11.5px] text-[var(--color-destructive)]">{error}</p>}
    </div>
  );
}
