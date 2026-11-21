// Parts of what the agent is told (harness.mjs) that depend on the desktop's setup, kept apart so they can be
// read and tested without starting the agent.

/**
 * How project work ends, when the desktop has a repository: on a branch of its own and a pull request, never
 * on the default branch. git and gh sign in to GitHub by themselves (github.mjs, shims/gh).
 */
export const projectWork = (repository) =>
  repository
    ? `
The project is the repository ${repository.url}${repository.branch ? `, on its branch ${repository.branch}` : ""}, cloned into your working folder. Finish project work as a branch and a pull request:

- Make a new branch for the work before you commit, named for what it does.
- Commit there, push the branch, and open a pull request with \`gh pr create\`, saying what changed and why. Give the person its address when you finish.
- Never push to the default branch or to the branch you started from, and never force-push a branch you did not make.
- git and gh sign in to GitHub by themselves with a token for this repository. If pushing is refused, say so and leave the work committed on its branch; do not look for another way in.
`.trim()
    : "";
