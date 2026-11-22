import type { Online } from "@/lib/online";
import type { TeamUsage } from "@/lib/workstream/api";

/**
 * What a team has used this month, in the units a bill would be made from: what its agents spent on models
 * beside the allowance, desktop hours for each size it used, and storage in GB-months for each kind it kept.
 * No prices: desktops and storage are shown as amounts only.
 */
export type UsageScreenProps = {
  usage: Online<TeamUsage>;
  onRetry: () => void;
};
