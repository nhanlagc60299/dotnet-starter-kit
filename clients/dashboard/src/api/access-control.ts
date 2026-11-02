import { apiFetch } from "@/lib/api-client";

/**
 * One grant of a role: it may do `action` on `subject` (a subject `name:*`
 * covers every `name:…`). `condition` is a filter limiting which rows the
 * grant reaches; null means every row.
 */
export type AccessGrantDto = {
  subject: string;
  action: string;
  condition: Record<string, unknown> | null;
};

export const ACCESS_CONTROL_VIEW_PERMISSION = "Permissions.AccessControl.View";
export const ACCESS_CONTROL_MANAGE_PERMISSION = "Permissions.AccessControl.Manage";

// Grants are keyed by role NAME (what the JWT carries), not the role id.
export function getRoleGrants(role: string) {
  return apiFetch<AccessGrantDto[]>(
    `/api/v1/access-control/roles/${encodeURIComponent(role)}/grants`,
  );
}

/** Replaces the role's whole grant set. */
export function putRoleGrants(role: string, grants: AccessGrantDto[]) {
  return apiFetch<void>(`/api/v1/access-control/roles/${encodeURIComponent(role)}/grants`, {
    method: "PUT",
    body: JSON.stringify(grants),
  });
}
