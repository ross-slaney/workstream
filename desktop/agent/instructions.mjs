// Parts of what the agent is told (harness.mjs) that depend on the desktop's setup, kept apart so they can be
// read and tested without starting the agent.

import { phoneInstructions } from "./phones.mjs";

/**
 * How project work ends, on any repository: on a branch of its own and a pull request, never on the default
 * branch. git and gh sign in to GitHub by themselves (github.mjs, shims/gh), with or without a repository in
 * the environment, so a task about another repository can clone it.
 */
export const projectWork = (repository) =>
  `
${
  repository
    ? `The project is the repository ${repository.url}${repository.branch ? `, on its branch ${repository.branch}` : ""}, cloned into your working folder.`
    : "No repository is cloned on this desktop. git and gh reach GitHub as the person who started this task: when the task is about a repository, clone it into your working folder with `gh repo clone owner/name`."
} Finish work on any repository as a branch and a pull request:

- Make a new branch for the work before you commit, named for what it does.
- Commit there, push the branch, and open a pull request with \`gh pr create\`, saying what changed and why. Give the person its address when you finish.
- Never push to the default branch or to the branch you started from, and never force-push a branch you did not make.
- git and gh sign in to GitHub by themselves, as the person who started this task. If they cannot, they print a line starting with "Workstream:" that says why: tell the person that line. If pushing is refused, say so and leave the work committed on its branch; do not look for another way in.
`.trim();

/**
 * How to work on a real project here: the repository's own rules and skills, what is installed, the phones
 * this desktop has (phones.mjs), and patience with long builds.
 */
export const projectTools = (apps = []) =>
  `
Working on a project:
- The repository's own rules apply. Its CLAUDE.md is read for you. If it has an AGENTS.md, read that first and follow it the same way. Its skills (in .claude/skills, often linked from .agents/skills) are yours: when one covers what you are asked to do, such as running the app locally or implementing an issue, use it and follow its steps instead of working them out yourself.
- Run the project the way it says to: its skill, its scripts or its README. Docker (it starts with the first docker command), the .NET SDK, Node, Java 17, sqlcmd, Playwright's system libraries and the Android SDK are installed.
${phoneInstructions(apps)}
- Inside a container, \`host.docker.internal\` is this desktop, as it is on a Mac: a container reaches a server running on the desktop there, one that listens only on localhost included, and so does a build. Scripts written for Docker Desktop need no change for it.
- Builds, installs and a first start of a stack can take many minutes. A command you run is stopped after ten minutes unless you give it a longer timeout, which can be up to an hour: give such commands one, or start them in the background with their output in a log file and read the log.
- On a long task, say in a sentence what you finished and what comes next at each milestone, so the people watching know where you are.
`.trim();

/**
 * What the agent is told when the environment has a repository and no setup script (no workstream.yml, and
 * nothing typed in the form): nothing of the project is installed, so it installs what the task needs first.
 */
export const noSetup = (setup) =>
  setup?.repository && !setup.setupScript?.trim()
    ? "No setup script ran on this desktop, so the project's packages and tools are not installed. Install what the task needs, the way the repository's README, AGENTS.md or CI workflow does it, before you build or test."
    : "";

/**
 * What the agent is told when the environment's setup script did not end well on this desktop, so it does
 * not take a half-installed project for a ready one. Nothing when it ended well, or none ran here.
 */
export const setupEnded = (result) => {
  if (!result || result.code === 0) return "";
  const how = result.code === 124 ? `did not finish in ${result.minutes} minutes and was stopped` : `stopped with an error (exit ${result.code})`;
  return `The environment's setup script ${how} on this desktop, before you started. The script is ~/.workstream/setup.sh and what it wrote is ~/.workstream/setup.log. The project may be only partly installed: read the log before you rely on it, say what failed, and finish the setup yourself when the task needs it.`;
};
