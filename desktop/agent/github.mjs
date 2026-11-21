// GitHub on the desktop, from the environment's variables: a GITHUB_TOKEN (or GH_TOKEN) signs in both git and
// gh, and commits are made as the token's own account. gh reads GH_TOKEN first and git reads neither, so the
// agent writes both names and tells git the token (agent.mjs).

/** The token the environment gives for GitHub, if any. */
export const gitHubToken = (variables) => variables.GH_TOKEN || variables.GITHUB_TOKEN || null;

/** The variables with the token under both names, so every program that looks for one finds it. */
export function withGitHub(variables) {
  const token = gitHubToken(variables);
  return token ? { ...variables, GITHUB_TOKEN: variables.GITHUB_TOKEN || token, GH_TOKEN: variables.GH_TOKEN || token } : { ...variables };
}

/**
 * Who commits: the token's own GitHub account, with the private address GitHub gives every account, unless
 * the environment names someone (GIT_AUTHOR_NAME and GIT_AUTHOR_EMAIL, which git reads itself). Null when
 * GitHub does not say.
 */
export async function gitIdentity(variables, { fetch = globalThis.fetch } = {}) {
  const token = gitHubToken(variables);
  if (!token || (variables.GIT_AUTHOR_NAME && variables.GIT_AUTHOR_EMAIL)) return null;
  try {
    const response = await fetch("https://api.github.com/user", {
      headers: { authorization: `Bearer ${token}`, accept: "application/vnd.github+json", "user-agent": "workstream-desktop" },
      signal: AbortSignal.timeout(10_000),
    });
    if (!response.ok) return null;
    const user = await response.json();
    if (!user?.login || !user?.id) return null;
    return { name: user.name || user.login, email: `${user.id}+${user.login}@users.noreply.github.com` };
  } catch {
    return null;
  }
}
