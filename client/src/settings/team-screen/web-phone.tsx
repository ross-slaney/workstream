import { TeamPage } from "./parts";
import type { TeamScreenProps } from "./props";

/** A phone's browser: the same column, full width; a person's actions wrap under their name. */
export function TeamScreen(p: TeamScreenProps) {
  return <TeamPage {...p} />;
}
