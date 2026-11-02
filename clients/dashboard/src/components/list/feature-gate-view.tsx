import { PlugZap, ShieldOff } from "lucide-react";
import type { FeatureGate } from "@/hooks/use-feature-gate";
import { EntityEmpty, EntityListLoading } from "./entity-shell";

/**
 * Renders `children` only once the gate resolves to open; before that a
 * skeleton, and after that a plain explanation of why the page is closed.
 */
export function FeatureGateView({
  gate,
  allowed,
  feature,
  children,
}: {
  gate: FeatureGate;
  allowed: boolean;
  feature: string;
  children: React.ReactNode;
}) {
  if (!gate.ready) return <EntityListLoading rows={3} desktopColumns="grid-cols-[1fr_160px]" />;
  if (!gate.moduleOn)
    return (
      <EntityEmpty
        icon={PlugZap}
        title={`${feature} is not enabled`}
        body="This deployment has the module switched off. Ask your operator to enable it."
      />
    );
  if (!allowed)
    return (
      <EntityEmpty
        icon={ShieldOff}
        title="You don't have access"
        body={`Ask a tenant admin for the ${feature} permission.`}
      />
    );
  return <>{children}</>;
}
