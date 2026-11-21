/**
 * Where GitHub sends an administrator back after they installed Workstream's app: the connection is being
 * finished, GitHub is waiting for the account's owners to approve the request, or it did not work, in the
 * API's words.
 */
export type GitHubScreenProps = {
  phase: "connecting" | "requested" | "failed";
  error: string | null;
  /** Back to the team's page. */
  onBack: () => void;
};
