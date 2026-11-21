import type { Online } from "@/lib/online";
import type { AdminOverview } from "@/lib/workstream/api";

/**
 * The system administrator's page: what is running now across every team, the last day's curve, and each
 * team's month with the limits an administrator can change. English only, like the rest of what only the
 * people who run Workstream see.
 */
export type AdminScreenProps = {
  overview: Online<AdminOverview>;
  busy: boolean;
  error: string | null;
  onRetry: () => void;
  /** Back to the person's own tasks. */
  onBack: () => void;
  onSetLimits: (teamId: string, modelAllowanceUsd: number, maxRunningDesktops: number) => Promise<boolean>;
};
