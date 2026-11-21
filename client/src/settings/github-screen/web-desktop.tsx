import { GitHubPage } from "./parts";
import type { GitHubScreenProps } from "./props";

/** The desktop: the message in the middle of the window, with no sidebar, since the team is not known yet. */
export function GitHubScreen(p: GitHubScreenProps) {
  return <GitHubPage {...p} />;
}
