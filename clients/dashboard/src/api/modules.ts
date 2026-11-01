import { apiFetch } from "@/lib/api-client";

/** One row per backend module, as reported by GET /api/v1/modules. */
export type ModuleInfoDto = {
  /** Module name without the `Module` suffix — "Billing", "Catalog", … */
  name: string;
  /** False when the deployment switched it off via Modules:{Name}:Enabled. */
  enabled: boolean;
  /** Required modules (Identity, Multitenancy) can't be switched off. */
  required: boolean;
};

export function getModules() {
  return apiFetch<ModuleInfoDto[]>("/api/v1/modules");
}
