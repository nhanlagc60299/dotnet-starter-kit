import { useAuth } from "@/auth/use-auth";
import { useModules } from "@/hooks/use-modules";

/**
 * Gate for a page backed by an optional module (Modules:{Name}:Enabled) and a
 * permission. `ready` stays false until both the module list and the user's
 * permissions have loaded, so a page neither fires requests the API would
 * 404/403 nor flashes a "no access" state on first paint.
 */
export function useFeatureGate(module: string) {
  const { user, permissionsHydrated } = useAuth();
  const modules = useModules();
  const perms = user?.permissions ?? [];
  return {
    ready: modules.isLoaded && permissionsHydrated,
    moduleOn: modules.isLoaded && modules.isEnabled(module),
    has: (permission: string) => perms.includes(permission),
  };
}

export type FeatureGate = ReturnType<typeof useFeatureGate>;
