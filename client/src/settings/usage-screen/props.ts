import type { Online } from "@/lib/online";
import type { ModelReport, TeamUsage } from "@/lib/workstream/api";

/**
 * What a team has used this month, in the units a bill would be made from: what its agents spent on models
 * beside the allowance, desktop hours for each size it used, and storage in GB-months for each kind it kept.
 * No prices: desktops and storage are shown as amounts only.
 *
 * `report` is who and what used the models: day by day, by person, by tool, by model and by task. It is the
 * whole team's for a team administrator and the reader's own for anyone else, and its cards are drawn once it
 * has been read.
 */
export type UsageScreenProps = {
  usage: Online<TeamUsage>;
  report: Online<ModelReport>;
  onRetry: () => void;
};
