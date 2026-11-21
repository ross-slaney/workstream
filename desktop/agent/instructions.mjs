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
 * with long builds.
 */
export const projectTools = () =>
  `
Working on a project:
- The repository's own rules apply. Its CLAUDE.md is read for you. If it has an AGENTS.md, read that first and follow it the same way. Its skills (in .claude/skills, often linked from .agents/skills) are yours: when one covers what you are asked to do, such as running the app locally or implementing an issue, use it and follow its steps instead of working them out yourself.
- Run the project the way it says to: its skill, its scripts or its README. Docker (it starts with the first docker command), the .NET SDK, Node, Java 17, sqlcmd, Playwright's system libraries and the Android SDK are installed. An Android emulator is set up as "Phone": start it in the background with \`emulator -avd Phone -gpu swiftshader_indirect -no-audio -no-snapshot\`. iPhone and iPad builds and the iOS simulator need a Mac and cannot run here: say so when a task asks for them, and do the rest.
- Builds, installs and a first start of a stack can take many minutes. Give such commands a long timeout, or start them in the background with their output in a log file and read the log.
- On a long task, say in a sentence what you finished and what comes next at each milestone, so the people watching know where you are.
`.trim();
