import { useQuery } from "@tanstack/react-query";
import { getModules, type ModuleInfoDto } from "@/api/modules";

const EMPTY: ModuleInfoDto[] = [];

/**
 * Which backend modules this deployment runs. Pages that depend on an optional
 * module (Billing, Catalog, Tickets, Chat) gate their queries on `isEnabled(...)`
 * so a switched-off module reads as "not installed" instead of a wall of 404s.
 *
 * Until the list has loaded every module reads as enabled, so existing pages
 * keep their behaviour on the very first paint; callers that must not fire a
 * request before knowing should also check `isLoaded`.
 */
export function useModules() {
  const query = useQuery({
    queryKey: ["system", "modules"],
    queryFn: () => getModules(),
    staleTime: 5 * 60_000,
  });

  const modules = query.data ?? EMPTY;
  // Fail open: if the list can't be fetched (older backend, network error)
  // behave exactly like before the switches existed — everything enabled.
  const isLoaded = query.isSuccess || query.isError;

  const isEnabled = (name: string): boolean => {
    if (!query.isSuccess) return true;
    const found = modules.find((m) => m.name.toLowerCase() === name.toLowerCase());
    return found ? found.enabled : false;
  };

  return { modules, isLoaded, isEnabled, isError: query.isError };
}
