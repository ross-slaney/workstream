import { TeamPage } from "./parts";
import type { TeamScreenProps } from "./props";

/** The desktop: one readable column under the sidebar; each person is a row with their actions at its end. */
export function TeamScreen(p: TeamScreenProps) {
  return <TeamPage {...p} />;
}
