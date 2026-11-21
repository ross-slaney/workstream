const KEY = "workstream.last-team";

/** The team the person was last in, on this browser: where signing in lands them. */
export function lastTeam(): string | null {
  try {
    return window.localStorage.getItem(KEY);
  } catch {
    return null;
  }
}

export function rememberTeam(teamId: string) {
  try {
    window.localStorage.setItem(KEY, teamId);
  } catch {
    // Not remembered; the first team is used next time.
  }
}
