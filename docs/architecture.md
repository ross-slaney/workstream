# How the pieces fit

[The core model](core-model.md) says who can do what. This says what runs where and how a task's desktop works.

## The pieces

```text
a person's browser
  │  pages                    web app (client/, Expo server output)
  │  REST, sign-in            ┐
  │  the desktop's display    │ API (api/): REST + SqlOS, the relay,
  ▼  (a WebSocket)            ┘ the model gateway, the jobs
                                 │                    │
            private network      │ display            │ model calls, with Workstream's key
                                 ▼                    ▼
                         a task's desktop        Microsoft Foundry (Claude)
                         (one machine per task)
                                 │
                                 └── calls the API at its public address, with its own key
```

- **The web app** has no secrets and no database. Public pages (the landing page, sign-in, an invitation) render on the server; everything signed-in renders in the browser, which holds the session.
- **The API** is one process: the REST API with SqlOS for sign-in and authorization, the relay that carries a desktop's display to the people watching it, the gateway every agent's model calls go through, and the background jobs (Hangfire, in the app's own database) that make, stop and delete desktops.
- **A desktop** is a Debian virtual machine with the XFCE desktop on a TigerVNC display, made from one image. A machine made from the image starts as it is: nothing provisions it, and it learns what it is for from its user data. In development it is a Docker container built from the same two scripts.

## What happens when someone starts a task

1. The browser posts the task with an id it made. The API checks `TEAM_CREATE_TASK` on the team and the team's limit of running desktops, saves the task as `starting` with the person's first message, and queues a job.
2. The job (`api/Desktops/DesktopJobs.cs`), as `agent::desktop-operator`, makes the desktop's service account, grants it `task_desktop` on the task, issues its key, and asks the host for a machine (`AzureDesktopHost` or `DockerDesktopHost`). The machine is told two things, the API's address and its key, as its user data. The host answers as soon as it has taken the request; the job then follows the machine, and writes down each step as it happens.
3. The host says the machine is on. Its service (`desktop/start.sh`, then `desktop/session.sh`) starts three things at once: the agent (`desktop/agent/agent.mjs`), the display, and XFCE on it.
4. The display answers. From this moment the person has a screen to watch, a few seconds before the agent has said anything, and the job's work is done.
5. The agent reads what it is told from the machine's user data, and exchanges with the API about once a second from then on: it hands over what it said and did, and takes what people wrote. The first exchange turns the task `ready`.
6. The agent reads its setup from the API with its key: the apps for the panel, the repository, the setup script, the variables and the instructions. It prepares the desktop where the person can watch: it clones the repository, shows the environment's apps on the panel, runs the setup script in a terminal window, and opens the browser and the editor.
7. The person's browser reads the conversation twice a second while the desktop is starting, and every second and a half after. It shows each step as the task reports it (`step`: `machine`, `boot`, `agent`), and opens a WebSocket to the API for the display as soon as there is one.

Each of those moments is kept with the run (`DesktopRun`: asked, on, display, ready), so how long a start takes is something the product knows and not something anyone has to guess. The system administrator's page shows the middle start of the last seven days, by what it started from.

## A spare, so a task starts at once

Step 2 is the slow one, and it is Azure's: between fifteen seconds and a minute to hand over a machine, more on a bad hour, whether the machine is new or was stopped. So while someone is in a team's workspace, the operator keeps one desktop running ahead of them (`Spare`, `api/Operations/Spares.cs`).

- The web app says someone is there when a team's pages open and when the new-task dialog opens. The API makes the team a spare if it has none and the limits allow: a machine like any other, told the API's address and a key of its own.
- The spare boots, draws its desktop, opens the browser and reads its agent's program off the disk, so nothing is cold. Its agent asks the API twice a second what it is for. Its key opens nothing, because its account is granted nothing.
- A task from an environment of the same size takes the spare as it is made: one transaction deletes the spare's row, writes the task with the spare's machine, and grants the spare's account `task_desktop` on the task. The person has the screen at once, and the agent's next question, at most half a second later, is answered with the task. Then the operator makes the team's next spare.
- A spare nobody on the team has wanted for fifteen minutes is removed, with its machine and its key.

A spare is a running machine nobody is using yet, so what it can cost is limited by settings (`Workstream:Desktops:Spares`, set in `infra/api.bicep`): one a team, two at once over every team, six hours of spare time a day, and none for a team already at its limit of running desktops. With nobody in the app there are none.

## A saved setup, so a task starts with its setup done

Cloning the repository and running the setup script is the slow part of a new desktop once it is up, and it is the same work for every task of an environment. So it is done once (`EnvironmentSetup`, the `PrepareAsync` and `SavePreparedAsync` jobs in `api/Desktops/DesktopJobs.cs`):

- The operator makes a machine for the environment, told the API's address and a key for the environment's preparing account. Its agent sees from `GET /api/desktop/setup` that it is preparing (`prepare`), clones the repository and runs the setup script out of sight, and posts what the script writes to `POST /api/desktop/setup` every few seconds, which the environment's page shows. It never exchanges and never calls a model.
- When the script exits 0 the agent removes everything in `~/.workstream` (the variables, the logs, the browser's profile), the git token and the shell's history, writes `~/.workstream/prepared.json` (when, and at which commit), and says it is done with the commit and how much is on the disk. The operator deallocates the machine, saves its disk (`IDesktopHost.SaveAsync`: a full snapshot on Azure, a committed image in Docker) and deletes the machine.
- A task's machine is made from the save while the save is for the environment's setup inputs (a fingerprint of the repository, branch, script, variables and apps) and for this release's image version. On Azure a disk is made from the snapshot with `createOption: Copy`, stating no security profile because it inherits Trusted Launch from the snapshot, and the machine attaches it (`createOption: Attach`). Making the snapshot took about 13 seconds when measured, and a machine made from one booted like one made from the image. The task's agent finds `prepared.json`, pulls the branch, skips the setup script, and says "Started from the setup prepared 2 days ago (main at a1b2c3d), 14 commits newer now."
- Anything else starts from the image and does the setup itself, as before: an environment never prepared, a save out of date, a preparation that failed.

What it can cost is limited by settings (`Workstream:Desktops:Saves`): one machine preparing at once over every team, four hours of preparing a day, 45 minutes at most each, one save an environment, deleted after 30 days nothing started from it. Its time is a line of its own on the system administrator's page, and so are the saves kept.

Stopping a task deallocates its machine and keeps its disk. Starting it again brings the same desktop back, and the agent resumes the same conversation. Deleting a task deletes the machine, its disk and its network interface, and ends its key. A desktop nobody watches or writes to, whose agent is idle, is stopped after half an hour by a sweep that also fails a desktop that never arrived, and deletes a machine no task knows once it has been there half an hour (a newer one may belong to a task that has just started).

## The display

A desktop's display is a VNC server with no password, on a network that admits one caller: the API. The browser never reaches a desktop. It opens `GET /api/tasks/{id}/desktop` as a WebSocket carrying the person's access token; the API validates the token, checks `TASK_VIEW` on the task, connects to the desktop's private address, and passes bytes both ways. What the browser sends is read message by message (`api/Desktops/RfbClientFilter.cs`): for someone a task is shared with only to watch or to write to the agent, without `TASK_DRIVE`, key and pointer events and the clipboard are dropped there, and the browser draws their screen view-only. It does this from the moment the display has answered, which is before the agent's first exchange, until the desktop is asked to stop. It checks again every minute and closes the stream when access is gone. The browser draws it with noVNC.

VNC rather than RDP for two reasons: the screen has to exist before anyone connects, because the agent works on it whether or not a person is watching, and several people can watch the same screen at once. There is no outside service and no VPN between the person and the desktop.

## The agent

`desktop/agent/harness.mjs` is the one file that knows which agent runs: the Claude Agent SDK, with every permission granted, because the desktop is the task's own machine and is deleted with it. It is told where it is and to work where people can see: the browser tools drive the Chromium window on the desktop (the same one the person can use), a small set of desktop tools take screenshots and click and type for anything else, and a file the agent edits opens in VS Code as it changes. The repository's own `CLAUDE.md` and skills apply, as they would on a laptop.

The agent has its own window on the desktop, the Agent button on the panel, which shows the same conversation. A person at the desktop can talk to the agent there; what they type reaches the web app's conversation too.

Another harness would replace `harness.mjs` and nothing else: the rest of the agent only hands it prompts and takes what it said and did.

### After a stop

A stopped desktop keeps its disk and loses every program. So the agent keeps, in `~/.workstream/state.json`, what it would need to bring them back (`desktop/agent/restore.mjs`): the long-running commands the agent started (the harness notes every Bash call made with `run_in_background`, or ending in a lone `&`) with the folder each ran in, the pages open in the browser (read from Chromium's debugging port every fifteen seconds), and the files the agent opened in VS Code.

The first time the agent runs on a machine that has just started, which it knows because its mark in `/dev/shm` is gone, and the desktop was prepared before, it brings them back: the commands run again in a terminal window titled Restored, VS Code opens on the workspace with those files, and the browser opens the pages, a page on one of the desktop's own servers once that server answers. The agent is told so in one line of its instructions, and the conversation gets one line saying it happened. An agent that restarts on a running desktop brings nothing back, since nothing stopped.

## Email

The API sends two kinds of email through Azure Communication Services: SqlOS's own (sign-in codes, invitations) and the email to a task's people when the agent's turn ends (`api/Email/TaskEmails.cs`, [the core model](core-model.md#hear-when-a-task-is-finished-or-needs-you)). The task emails are SqlOS templates that code owns: the API writes them into SqlOS's store at every start, with every word from `client/locales/emails/en.json`, which the API carries inside it. They are sent by a Hangfire job a few seconds after a turn ends. Where `SqlOS:EmailOtp:FromAddress` and its connection string are not set, nothing is sent.

## Models

The harness calls models the way it calls Microsoft Foundry, at `…/api/model/anthropic` on the API, with the desktop's key. The gateway (`api/Model/ModelGateway.cs`) checks the key and the team's allowance, swaps in Workstream's own Foundry key, passes the request and the answer through as they are, and reads the token counts off the answer. Each call is saved as a `ModelUsage` row with its cost in dollars. What a team has left this month is its allowance less the sum of those rows; the gateway remembers it for a few seconds between calls.

The prices are a table in `ModelOptions`, with the two things that make a call cost more than its plain rate: a cache write kept for an hour, and Haiku's higher rate for a prompt over 100,000 tokens. They are what Workstream pays; `Markup` is 1 until there is billing.

## What a desktop can and cannot do

A desktop runs whatever its agent is asked to, so it is treated as untrusted:

- It holds one credential, its key. The key reaches `/api/desktop/*` and `/api/model/*` for its own task. It is not a person's token and opens no other route. The key is in the machine's user data, which Azure serves to that machine alone, and is never written to its disk: a copy of a desktop's disk carries nobody's key.
- It never holds a model key. A leaked desktop key is worth what is left of one team's monthly allowance, until the task is deleted.
- It has no public address. Its subnet admits the API's relay to the display and refuses everything else on the network, other desktops included. It reaches the internet through one NAT gateway.
- It has no Azure identity.
- A spare is a desktop with no task. Its key authenticates and nothing more until a task takes it, and it is never given to another team.
- A machine preparing an environment holds a key that reads that environment's setup and says how it went, and nothing else: no task, no conversation, no model. Its disk is saved for the environment's tasks only after it has removed the variables, the git token, the logs and the browser's profile, and its key is in its user data, never on the disk. What the setup script itself leaves on the disk is in the save, and every task of the environment starts with it.
- An environment's variables are stored protected and are sent only to a desktop made from that environment. They are on that desktop in plain text, because the work needs them there: anyone on the team can read them by opening the desktop.

## What is kept where

| Kept in | What |
| --- | --- |
| SqlOS (the app's SQL database) | People, teams, memberships, invitations, sessions; the resource tree and every grant. |
| The app's tables | Teams' limits, environments, tasks, messages, desktop runs (for hours), model usage (for dollars). |
| A desktop's disk | The work itself: the repository, files, the browser's profile, the agent's session, and what to start again after a stop. It goes with the task. |
| A saved setup (a snapshot in the desktops' group) | An environment's repository and whatever its setup script installed, and nothing of any task. It goes with the environment. |
| The browser | The session's tokens, and which team was open last. |
