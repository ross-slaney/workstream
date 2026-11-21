import type { Online } from "@/lib/online";
import type { TeamUsage } from "@/lib/workstream/api";

/** What a team has used this month beside what it may use: the numbers a bill would be made from. */
export type UsageScreenProps = {
  usage: Online<TeamUsage>;
  onRetry: () => void;
};
