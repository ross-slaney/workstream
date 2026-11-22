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

/**
 * How to work on a real project here: the repository's own rules and skills, what is installed, and patience
 * with long builds. The emulator's command is the one the image is checked with (image/bake.sh): it boots
 * on every size, in about a minute on a Large desktop.
 */
export const projectTools = () =>
  `
Working on a project:
- The repository's own rules apply. Its CLAUDE.md is read for you. If it has an AGENTS.md, read that first and follow it the same way. Its skills (in .claude/skills, often linked from .agents/skills) are yours: when one covers what you are asked to do, such as running the app locally or implementing an issue, use it and follow its steps instead of working them out yourself.
- Run the project the way it says to: its skill, its scripts or its README. Docker (it starts with the first docker command), the .NET SDK, Node, Java 17, sqlcmd, Playwright's system libraries and the Android SDK are installed. iPhone and iPad builds and the iOS simulator need a Mac and cannot run here: say so when a task asks for them, and do the rest.
- An Android emulator is set up as "Phone", with four processors and 4 GB of memory. Start it in the background with \`emulator -avd Phone -gpu swiftshader_indirect -no-audio -no-snapshot -no-metrics -no-nested-warnings\`, then wait for \`adb wait-for-device\` and for \`adb shell getprop sys.boot_completed\` to print 1: about a minute, and up to three the first time on a new desktop. Its window opens on the desktop, where the person can use it too.
- Inside a container, \`host.docker.internal\` is this desktop, as it is on a Mac: a container reaches a server running on the desktop there, one that listens only on localhost included, and so does a build. Scripts written for Docker Desktop need no change for it.
- Builds, installs and a first start of a stack can take many minutes. Give such commands a long timeout, or start them in the background with their output in a log file and read the log.
- On a long task, say in a sentence what you finished and what comes next at each milestone, so the people watching know where you are.
`.trim();

/**
 * What the agent is told when the environment's setup script did not end well on this desktop, so it does
 * not take a half-installed project for a ready one. Nothing when it ended well, or none ran here.
 */
export const setupEnded = (result) => {
  if (!result || result.code === 0) return "";
  const how = result.code === 124 ? `did not finish in ${result.minutes} minutes and was stopped` : `stopped with an error (exit ${result.code})`;
  return `The environment's setup script ${how} on this desktop, before you started. The script is ~/.workstream/setup.sh and what it wrote is ~/.workstream/setup.log. The project may be only partly installed: read the log before you rely on it, say what failed, and finish the setup yourself when the task needs it.`;
};
