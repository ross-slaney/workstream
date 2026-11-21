---
name: start-workstream-task
description: Hand a GitHub issue to Workstream from this computer. Starts a Workstream task (a cloud desktop with an agent) whose prompt is the issue's title and body plus "work on a branch and open a pull request", waits until its desktop is ready, and reports the task's link. Use when asked to start, send, hand off or run an issue in Workstream, or to "give #123 to Workstream".
---

# Start a Workstream task for a GitHub issue

The Workstream command line (`cli/workstream.mjs` in the Workstream repository) starts tasks as the person signed in to it, through the same API as the web app. It can read their teams and tasks and start tasks, and nothing else.

## 1. Find the command line

Use the first that works:

1. `workstream` on the `PATH` (`command -v workstream`).
2. `node cli/workstream.mjs` when this is the Workstream repository.
3. `node ~/Dev/workstream/cli/workstream.mjs`, or wherever the person keeps the Workstream checkout. Ask if you cannot find it.

Below, `workstream` means whichever you found. It needs Node 22 or later.

## 2. Make sure it is signed in

Run `workstream envs --json`. If it says "Not signed in" or "Your sign-in has ended":

- Run `workstream login --no-browser` (add `--api <the API's address>` the first time on this computer; ask the person for it if no earlier login remembered one) in the background.
- It prints a link and a code. Show both to the person and ask them to open the link, sign in, check the code and press Allow. It waits up to fifteen minutes and prints "Signed in as …" when they have.
- Signing in is always the person's own step in their browser. Never try to complete it yourself.

## 3. Read the issue

```bash
gh issue view <number> --json number,title,body,url
```

Add `-R <owner>/<repo>` when the issue is not in the current repository. If `gh` is not signed in, ask the person to run `gh auth login`.

## 4. Choose the team and the environment

`workstream envs --json` lists every environment the person can use, with its team and the repository it starts with (`repositoryUrl`).

- Prefer the environment whose `repositoryUrl` is the issue's repository (compare `github.com/<owner>/<repo>`, ignoring `https://`, a trailing `.git` and case).
- If only one environment exists, use it.
- Otherwise, or if several match, ask the person which one, listing the team and environment names.

Pass both by id: `--team <teamId> --env <id>`.

## 5. Start the task

Write the prompt to a temporary file, so quotes and backticks in the issue cannot break the command:

```text
Work on GitHub issue #<number> in <owner>/<repo>: <title>
<url>

<body>

Work on a new branch. When the change is done and its checks pass, open a pull request that says "Fixes #<number>", and reply with the pull request's link.
```

Keep the prompt under 20,000 characters: if the body is longer, cut it and end with "(the rest is in the issue)". Then:

```bash
workstream start --team <teamId> --env <environmentId> --title "<owner>/<repo>#<number>: <title>" --json - < /path/to/prompt.txt
```

Keep the title under 120 characters (shorten the issue's title if needed). The command prints progress on standard error and waits until the desktop is ready, usually under a minute; run it in the background if you have other work. Add `--no-wait` if the person only wants the link.

The last line on standard output is JSON with the task's `link`, `state` and `step`. Exit code 0 means the desktop is ready; 1 means it failed or is still starting after the timeout (the JSON says which, and the link still works); 2 means the command was wrong.

## 6. Report

Tell the person the task's link and whether its desktop is ready. They watch the agent there, talk to it, and take over the desktop when they want. Later, `workstream status <link> --json` says where the task is, and `workstream tasks` lists recent ones.

If starting is refused:

- "Your team can run N desktops at once": a limit; the person stops a task in the web app or asks to wait.
- "Not found, or not yours to see": the team or environment is not theirs. List again with `workstream envs`.
- "This connection can only read and start tasks": the command line cannot change, stop or delete tasks or send messages. Point the person at the link to do that in the web app.
