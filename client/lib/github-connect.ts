const KEY = "workstream.github-connect";

/** A connection under way on this browser: the team, and the state the API signed for the person who set off. */
export type PendingGitHubConnect = { teamId: string; state: string };

/**
 * Leave for GitHub to install Workstream's app, in this tab. What was set off with is kept on this browser, so
 * the page GitHub sends the person back to knows which team it was for.
 */
export function goToGitHub(teamId: string, installUrl: string) {
  const state = new URL(installUrl).searchParams.get("state") ?? "";
  try {
    window.localStorage.setItem(KEY, JSON.stringify({ teamId, state } satisfies PendingGitHubConnect));
  } catch {
    // Not kept: GitHub hands the state back itself.
  }
  window.location.assign(installUrl);
}

/** The connection set off from this browser, if there is one. */
export function pendingGitHubConnect(): PendingGitHubConnect | null {
  try {
    const value = JSON.parse(window.localStorage.getItem(KEY) ?? "null") as Partial<PendingGitHubConnect> | null;
    return value?.teamId ? { teamId: value.teamId, state: value.state ?? "" } : null;
  } catch {
    return null;
  }
}

export function forgetGitHubConnect() {
  try {
    window.localStorage.removeItem(KEY);
  } catch {
    // Nothing to forget.
  }
}
