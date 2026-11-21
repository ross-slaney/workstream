import { GitHubPage } from "./parts";
import type { GitHubScreenProps } from "./props";

/** A phone's browser: the same message, full width. */
export function GitHubScreen(p: GitHubScreenProps) {
  return <GitHubPage {...p} />;
}
