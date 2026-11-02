import { apiFetch } from "@/lib/api-client";

export const TRANSFER_EXPORT_PERMISSION = "Permissions.Transfer.Export";
export const TRANSFER_IMPORT_PERMISSION = "Permissions.Transfer.Import";

/** CreateOnly skips rows that already exist; Upsert updates them. Nothing is ever deleted. */
export type TransferMode = "CreateOnly" | "Upsert";
/** For an import, Pending means "uploaded, not applied yet". */
export type TransferJobStatus = "Pending" | "Running" | "Succeeded" | "Failed";

export type TransferRowError = { entityType: string; row: number; message: string };

export type TransferSummary = {
  /** Rows per entity type in the archive. */
  rows: Record<string, number>;
  created: number;
  updated: number;
  skipped: number;
  failed: number;
  /** The first 100 row errors. */
  errors: TransferRowError[];
};

export type TransferJobDto = {
  id: string;
  kind: "Export" | "Import";
  status: TransferJobStatus;
  entityTypes: string[];
  mode: TransferMode | null;
  fileName: string | null;
  sizeBytes: number;
  sha256: string | null;
  summary: TransferSummary | null;
  error: string | null;
  createdBy: string;
  createdAtUtc: string;
  completedAtUtc: string | null;
};

export type TransferEntityTypeDto = { entityType: string; canExport: boolean; canImport: boolean };

const BASE = "/api/v1/transfer";

/**
 * Mirrors the default of TransferOptions.MaxUploadBytes (200 MiB) in
 * src/Modules/Transfer/Modules.Transfer/TransferOptions.cs. No endpoint exposes
 * the configured value, so update this if a deployment changes it; the API
 * still enforces the real limit.
 */
export const MAX_UPLOAD_BYTES = 200 * 1024 * 1024;
// Uploads are validated in full before the response; give big archives time.
const UPLOAD_TIMEOUT_MS = 10 * 60_000;

export function getTransferEntityTypes() {
  return apiFetch<TransferEntityTypeDto[]>(`${BASE}/entity-types`);
}

export function createExport(entityTypes: string[]) {
  return apiFetch<TransferJobDto>(`${BASE}/exports`, {
    method: "POST",
    body: JSON.stringify({ entityTypes }),
  });
}

export function getTransferJob(id: string) {
  return apiFetch<TransferJobDto>(`${BASE}/jobs/${encodeURIComponent(id)}`);
}

/** Fetch a succeeded export's zip with the session's auth and hand it to the browser as a download. */
export async function downloadExport(id: string) {
  let response: Response | undefined;
  await apiFetch<void>(`${BASE}/exports/${encodeURIComponent(id)}/download`, {
    timeoutMs: UPLOAD_TIMEOUT_MS,
    onResponse: (r) => {
      response = r;
    },
  });
  if (!response) throw new Error("The download returned no file.");
  const blob = await response.blob();
  // Readable cross-origin only if the API's CORS exposes it (today it exposes only
  // ETag), so fall back to a name of the same shape.
  const disposition = response.headers.get("content-disposition") ?? "";
  const name = /filename="?([A-Za-z0-9._-]+)"?/.exec(disposition)?.[1] ?? `transfer-export-${id}.zip`;
  const href = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = href;
  a.download = name;
  a.click();
  // Let the click start the download before releasing the blob.
  setTimeout(() => URL.revokeObjectURL(href), 1_000);
  return name;
}

/** Upload an archive: the API validates and stores it; nothing is imported yet. */
export function uploadImport(file: File) {
  const form = new FormData();
  form.append("file", file);
  return apiFetch<TransferJobDto>(`${BASE}/imports`, {
    method: "POST",
    body: form,
    timeoutMs: UPLOAD_TIMEOUT_MS,
  });
}

/** What applying the archive with `mode` would do. Writes nothing. */
export function previewImport(id: string, mode: TransferMode) {
  return apiFetch<TransferSummary>(`${BASE}/imports/${encodeURIComponent(id)}/preview`, {
    method: "POST",
    body: JSON.stringify({ mode }),
  });
}

export function applyImport(id: string, mode: TransferMode) {
  return apiFetch<TransferJobDto>(`${BASE}/imports/${encodeURIComponent(id)}/apply`, {
    method: "POST",
    body: JSON.stringify({ mode }),
  });
}
