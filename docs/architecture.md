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

- **The web app** has no secrets and no database. Public pages (the landing page, the docs, sign-in, an invitation) render on the server; everything signed-in renders in the browser, which holds the session. Its `/auth/callback` page is where SqlOS sends the browser, with a code, after someone signs in with GitHub.
- **The API** is one process: the REST API with SqlOS for sign-in and authorization, the relay that carries a desktop's display to the people watching it, the gateway every agent's model calls go through, and the background jobs (Hangfire, in the app's own database) that make, stop and delete desktops.
- **A desktop** is a Debian virtual machine with the XFCE desktop on a TigerVNC display, made from one image. A machine made from the image starts as it is: nothing provisions it, and it learns what it is for from its user data. In development it is a Docker container built from the same two scripts.

## What happens when someone starts a task

1. The browser posts the task with an id it made. The API checks `TEAM_CREATE_TASK` on the team and the team's limit of running desktops, saves the task as `starting` with the person's first message, and queues a job.
2. The job (`api/Desktops/DesktopJobs.cs`), as `agent::desktop-operator`, makes the desktop's service account, grants it `task_desktop` on the task, issues its key, and asks the host for a machine (`AzureDesktopHost` or `DockerDesktopHost`). The machine is told two things, the API's address and its key, as its user data. The host answers as soon as it has taken the request; the job then follows the machine, and writes down each step as it happens.
3. The host says the machine is on. Its service (`desktop/start.sh`, then `desktop/session.sh`) starts three things at once: the agent (`desktop/agent/agent.mjs`), the display, and XFCE on it.
4. The display answers. From this moment the person has a screen to watch, a few seconds before the agent has said anything, and the job's work is done.
5. The agent reads what it is told from the machine's user data, and exchanges with the API about once a second from then on: it hands over what it said and did, and takes what people wrote. The first exchange turns the task `ready`.
6. The agent reads its setup from the API with its key: the apps for the panel, the repository, the setup script, the variables and the instructions. It prepares the desktop where the person can watch: it sets git and gh up to ask the API for a GitHub token when they need one, clones the repository, shows the environment's apps on the panel, runs the setup script in a terminal window, and opens the browser and the editor.
7. The person's browser reads the conversation twice a second while the desktop is starting, and every second and a half after. It shows each step as the task reports it (`step`: `machine`, `boot`, `agent`), and opens a WebSocket to the API for the display as soon as there is one.

Each of those moments is kept with the run (`DesktopRun`: asked, on, display, ready), so how long a start takes is something the product knows and not something anyone has to guess. The system administrator's page shows the middle start of the last seven days, by what it started from.

## Starting a task from a terminal

`cli/workstream.mjs` starts the same task from a person's own computer, which is how a local Claude Code hands an issue to Workstream (`.agents/skills/start-workstream-task`). It signs in once through the browser with SqlOS's device flow, approved on the web app's `/device` page, and then calls the same REST API as the web app with a token of the person's that can read and start tasks and nothing else ([the core model](core-model.md#the-command-line)). `workstream start` posts the task, prints its link in the web app, and reads the task every two seconds until its desktop is ready.

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
- When the script exits 0 the agent removes everything in `~/.workstream` (the variables, the logs, the browser's profile) and the shell's history (git keeps no token: it asks the API for one each time, as the machine it runs on), writes `~/.workstream/prepared.json` (when, and at which commit), and says it is done with the commit and how much is on the disk. The operator deallocates the machine, saves its disk (`IDesktopHost.SaveAsync`: a full snapshot on Azure, a committed image in Docker) and deletes the machine.
- A task's machine is made from the save while the save is for the environment's setup inputs (a fingerprint of the repository, branch, script, variables and apps) and for this release's image version. On Azure a disk is made from the snapshot with `createOption: Copy`, stating no security profile because it inherits Trusted Launch from the snapshot, and the machine attaches it (`createOption: Attach`). Making the snapshot took about 13 seconds when measured, and a machine made from one booted like one made from the image. The task's agent finds `prepared.json`, pulls the branch, skips the setup script, and says "Started from the setup prepared 2 days ago (main at a1b2c3d), 14 commits newer now."
- Anything else starts from the image and does the setup itself, as before: an environment never prepared, a save out of date, a preparation that failed.

What it can cost is limited by settings (`Workstream:Desktops:Saves`): one machine preparing at once over every team, four hours of preparing a day, 45 minutes at most each, one save an environment, deleted after 30 days nothing started from it. Its time is a line of its own on the system administrator's page, and so is the storage the saves take.

## Running, paused, archived

A desktop costs its machine by the hour while it runs and its disk by the month for as long as the disk exists, so a desktop nobody is using is stepped down in two stages ([the core model](core-model.md#pause-archive-delete) has the rules and who may do what):

- **Paused.** `IDesktopHost.StopAsync` releases the machine (Azure: deallocate; Docker: stop) and keeps its disk. Where a deployment hibernates its desktops ("Hibernating a paused desktop" below; no deployment does yet), the machine writes its memory to that disk first. A person pauses a desktop from its menu; the sweep (`DesktopJobs.SweepAsync`, every five minutes) pauses each ready desktop that has not been used for its team's pause time, 30 minutes unless the team changed it. Resuming starts the same machine (`StartAsync`), which takes Azure between fifteen seconds and a minute, and the agent picks the conversation up where it was.
- **Archived.** The sweep archives a desktop that has been paused for its team's archive time, 7 days unless the team changed it (`DesktopJobs.ArchiveAsync`). It copies the disk (`IDesktopHost.ArchiveAsync`: on Azure a full snapshot on standard storage, named `wsa-…` and tagged `archive`; in Docker the stopped container committed as a `workstream-archive` image), writes the archive's name and size on the task (`Archive`, `ArchiveBytes`), and then deletes the machine and its disk. Bringing it back is making a machine with `DesktopSpec.Archive`: on Azure a disk is made from the snapshot and attached, the same way a machine is made from a saved setup, and the machine gets a new key in its user data. The archive is deleted once the new machine's display has answered; a start that fails before that removes the machine and keeps the archive.
- **Deleted.** Only when a person deletes the task: the machine, its disk and its network interface, the archive if there is one, and its key.

### Hibernating a paused desktop

A pause throws away every program on the desktop, and the agent starts again what it can ("After a stop"). Azure can instead hibernate a machine: it asks the machine to write its memory to its own disk, releases it as a stop does, and the next start reads the memory back, so every program is where it was. `AzureDesktopHost` does that when `Workstream:Desktops:Azure:Hibernate` is on, which no deployment has yet (`infra/README.md`, "Hibernation", has what was measured and what turning it on means).

- **Making the machine.** With hibernation on and without Trusted Launch, because Azure refuses the two together on Linux; on a size Azure can hibernate (the Dasv6 sizes, set in `infra/api.bicep`); from the gallery's `machine-hibernate` definition, because Azure makes such a machine only from a definition that says its image can hibernate. A machine preparing a saved setup is made without hibernation, so no machine's memory is ever on a disk other machines start from.
- **Pausing.** `StopAsync` asks Azure to deallocate with `hibernate=true` and waits for the machine to be released. A machine that was not made to hibernate (one from before the deployment hibernated, or a preparing one) is stopped the plain way without asking. So is one Azure refuses to hibernate, or one that has not hibernated in five minutes, with a warning in the log: the desktop then comes back as from any pause.
- **On the desktop** (`desktop/image/hibernate.sh`, installed by `configure.sh`). At boot, on a machine Azure made with hibernation (its firmware offers the S4 sleep state) and on no other, a swap file as large as the machine's memory is made and the boot settings are told where it is. Azure's request arrives through the Hyper-V tools as a kernel event; a udev rule answers it by turning that swap on and running `systemctl hibernate`; a systemd-sleep hook turns the swap off again when the machine is back. The desktop runs with no swap, as it always has.
- **Resuming.** `StartAsync` is the same request as for any paused machine. The machine boots, finds its memory and continues: the display, the session, the agent and whatever the person and the agent had running. The agent's exchange with the API fails once on the connection it had and is made again within seconds; its key is the one it already holds, since a paused desktop keeps its key. Its mark in `/dev/shm` is still there, so it brings nothing back: nothing stopped.
- **Only the machine that hibernated wakes up.** An archive of a hibernated desktop is a copy of a disk with a machine's memory on it, and bringing it back makes a new machine with a new key. That machine must boot afresh. The boot settings name the machine they were written for (`workstream.machine=<the machine's id>`), and an early-boot script (`desktop/image/resume-here.sh`) calls the resume off on any other; the agent then finds its mark gone and brings back what was running, as after any stop.

What counts as use is decided in three places. The desktop's exchange says whether the agent is working, and the task's `ActiveAt` moves with it and once more when it stops. A person's message moves it. And the relay moves it when a key or pointer event reaches the display (below). Nothing else does: an open pane does not, and the web app closes its stream while the browser tab is hidden.

`ArchiveBytes` is how much was on the disk, which the agent says in its exchange every five minutes (`df`, as a preparing machine does); Azure does not say how much of a snapshot is used, so for a desktop that never said, it is the disk's whole size.

The same sweep fails a desktop that never arrived, and deletes a machine or an archive no task knows once it has been there half an hour (a newer one may belong to a task that has just started, or be a copy being made now).

## The display

A desktop's display is a VNC server with no password, on a network that admits one caller: the API. The browser never reaches a desktop. It opens `GET /api/tasks/{id}/desktop` as a WebSocket carrying the person's access token; the API validates the token (the web app's, with `workstream.write`), checks `TASK_VIEW` on the task, connects to the desktop's private address, and passes bytes both ways. What the browser sends is read message by message (`api/Desktops/RfbClientFilter.cs`): for someone a task is shared with only to watch or to write to the agent, without `TASK_DRIVE`, key and pointer events and the clipboard are dropped there, and the browser draws their screen view-only. It does this from the moment the display has answered, which is before the agent's first exchange, until the desktop is asked to stop. It checks again every minute and closes the stream when access is gone. The browser draws it with noVNC.

The same filter notices use. A key event that passes it, or a pointer event that moves the pointer or changes a button, is a person at the desktop, and the relay writes that on the task at once and then at most once a minute while it goes on (`DesktopRelay.UseAsync`). Requests for the picture are not use, however long the stream stays open, and neither is the clipboard or a pointer event that repeats where the pointer already is, which a browser can send by itself. The web app closes the stream when its tab is hidden and opens it again when the tab is shown (`client/components/app/desktop-stream.tsx`). A paused or archived desktop has no stream: its pane says so, and a click on it, or a message, starts it again.

VNC rather than RDP for two reasons: the screen has to exist before anyone connects, because the agent works on it whether or not a person is watching, and several people can watch the same screen at once. There is no outside service and no VPN between the person and the desktop.

## The agent

`desktop/agent/harness.mjs` is the one file that knows which agent runs: the Claude Agent SDK, with every permission granted, because the desktop is the task's own machine and is deleted with it. It is told where it is and to work where people can see: the browser tools drive the Chromium window on the desktop (the same one the person can use), a small set of desktop tools take screenshots and click and type for anything else, and a file the agent edits opens in VS Code as it changes. The repository's own `CLAUDE.md` and skills apply, as they would on a laptop.

The agent has its own window on the desktop, the Agent button on the panel, which shows the same conversation. A person at the desktop can talk to the agent there; what they type reaches the web app's conversation too.

Another harness would replace `harness.mjs` and nothing else: the rest of the agent only hands it prompts and takes what it said and did.

### After a stop

A paused desktop keeps its disk and loses every program (unless it was hibernated, "Hibernating a paused desktop" above), and so does one brought back from its archive, whose disk is a copy of the one it had. So the agent keeps, in `~/.workstream/state.json`, what it would need to bring them back (`desktop/agent/restore.mjs`): the long-running commands the agent started (the harness notes every Bash call made with `run_in_background`, or ending in a lone `&`) with the folder each ran in, the pages open in the browser (read from Chromium's debugging port every fifteen seconds), and the files the agent opened in VS Code.

The first time the agent runs on a machine that has just started, which it knows because its mark in `/dev/shm` is gone, and the desktop was prepared before, it brings them back: the commands run again in a terminal window titled Restored, VS Code opens on the workspace with those files, and the browser opens the pages, a page on one of the desktop's own servers once that server answers. The agent is told so in one line of its instructions, and the conversation gets one line saying it happened. An agent that restarts on a running desktop brings nothing back, since nothing stopped.

## Email

The API sends two kinds of email through Azure Communication Services: SqlOS's own (sign-in codes, invitations) and the email to a task's people when the agent's turn ends (`api/Email/TaskEmails.cs`, [the core model](core-model.md#hear-when-a-task-is-finished-or-needs-you)). The task emails are SqlOS templates that code owns: the API writes them into SqlOS's store at every start, with every word from `client/locales/emails/en.json`, which the API carries inside it. They are sent by a Hangfire job a few seconds after a turn ends. Where `SqlOS:EmailOtp:FromAddress` and its connection string are not set, nothing is sent.

## A project's whole stack on a desktop

A team's real project usually runs as several programs at once: a database in a container, an API, a bundler, a browser driving it. The image has what such a stack needs (`desktop/image/install.sh`): Docker Engine, the .NET 10 SDK (Aspire 9 and later is a NuGet package of the project, with no workload), Node 22, sqlcmd, Playwright's system libraries (the browsers themselves are the project's own version), Java 17, and the Android SDK with an emulator set up as `Phone`. The ASP.NET Core development certificate is made and trusted for the desktop's person, and the limits a big project reaches (watched files, open files) are raised.

| Size | Machine | Disk |
| --- | --- | --- |
| Small | 2 cores, 8 GB (`Standard_D2as_v7`) | 64 GB |
| Standard | 4 cores, 16 GB (`Standard_D4as_v7`) | 64 GB |
| Large | 8 cores, 32 GB (`Standard_D8as_v7`) | 128 GB |

The machines are a setting too (`Workstream:Desktops:Azure:Sizes`): a deployment that hibernates its desktops uses the generation before, `Standard_D2as_v6`, `Standard_D4as_v6` and `Standard_D8as_v6`, which have the same cores, memory and price, and gives up as much of each disk as the machine has memory (8, 16 and 32 GB) for the memory to be written to.

The disk of each size is a setting (`Workstream:Desktops:Azure:DiskSizesGb`), and is what the desktop's storage is counted at ("What the desktops cost" below). The image is 64 GB, so a machine with a larger disk grows its file system to it when it first boots (`image/grow-disk.sh`). A project with a database, an API and a web bundler wants Large.

The Android emulator needs an Intel machine. Every Dasv7 size exposes AMD's virtualization (`/dev/kvm` opens and `emulator -accel-check` passes, which the bake checks), but the emulator's virtual machine stops as soon as it starts ("QEMU main loop exits abnormally with code 1"), on a desktop and on a plain Debian machine alike. On an Intel `Standard_D4s_v6` the same emulator and system image boot in about a minute. So Android builds work on every size, and the emulator would work if a size were an Intel one (`Workstream:Desktops:Azure:Sizes`). The agent is told which, from the processor it finds (`agent/instructions.mjs`). iPhone and iPad builds and the iOS simulator need macOS and Apple's tools, which no Linux desktop can run. The agent is told so and does the rest of a task.

The agent is told how to work on a project the way a developer does (`agent/instructions.mjs`): follow the repository's `CLAUDE.md` and `AGENTS.md`, use its skills for running the stack or implementing an issue rather than working the steps out, give long builds a long timeout, and deliver changes on a branch of their own as a pull request opened with `gh`. git and gh sign in through the team's GitHub connection or a pasted `GITHUB_TOKEN` (`agent/github.mjs`, "GitHub" below).

## What the desktops cost

Two kinds of rows are what usage and cost are added up from, and neither holds a price:

- `DesktopRun`: one stretch of a machine running, with its size and what it ran for (a task's desktop, a spare, a machine preparing an environment). Hours by size are a team's desktop time; hours by purpose and size are what the machines cost.
- `StorageRun`: one stretch of a team keeping something at one size. A desktop's disk (`disk`) is counted at its full size, since that is what a provisioned disk is billed at; an archived task (`archive`) and a saved setup (`setup`) are snapshots, counted at what is on them. Gigabytes times hours kept, over 730, is GB-months.

Storage rows are derived, not written by the jobs that make and remove disks. `StorageJobs.ReconcileAsync` runs every fifteen minutes on the same Hangfire server: it lists what every team keeps now (tasks with a machine or an archive, environments with a save) and makes the open rows agree, beginning, ending, or ending and beginning again when a size changed (`StorageRun.Reconcile`, [the core model](core-model.md#usage-and-limits)). A row is therefore exact to fifteen minutes, and the job can be run again, or miss a run, without counting anything twice.

The rates are one table, `DesktopOptions.Prices` (`Workstream:Desktops:Prices`), and they are what Workstream pays Azure, in West US 3:

| Line | Rate |
| --- | --- |
| A running machine, by size | $0.091 (small), $0.182 (standard), $0.364 (large) an hour |
| A desktop's disk (`disk`) | $0.075 a gigabyte a month: a Standard SSD, $4.80 for 64 GiB and $9.60 for 128 |
| An archived task (`archive`), a saved setup (`setup`) | $0.05 a gigabyte a month: a full snapshot on standard storage |

The system administrator's page multiplies each line by its rate. A team's Usage page shows the same lines as amounts, hours by size and GB-months by kind, with no dollars: there is no customer price yet, and when there is one it is a second column in this table, not new rows. A spare's and a preparing machine's own disks last minutes and are not counted.

## Models

The harness calls models the way it calls Microsoft Foundry, at `…/api/model/anthropic` on the API, with the desktop's key. The gateway (`api/Model/ModelGateway.cs`) checks the key and the team's allowance, swaps in Workstream's own Foundry key, passes the request and the answer through as they are, and reads the token counts off the answer. Each call is saved as a `ModelUsage` row with its cost in dollars. What a team has left this month is its allowance less the sum of those rows; the gateway remembers it for a few seconds between calls.

The prices are a table in `ModelOptions`, with the two things that make a call cost more than its plain rate: a cache write kept for an hour, and Haiku's higher rate for a prompt over 100,000 tokens. They are what Workstream pays; `Markup` is 1 until there is billing.

## GitHub

GitHub appears twice, and the two do not meet. People may sign in with GitHub, which is SqlOS's own connection to a GitHub OAuth client and reads a person's profile and verified address only ([the core model](core-model.md), "Sign up and sign in"); SqlOS keeps no GitHub token from it. Repositories are reached through Workstream's GitHub App, below.

A team connects GitHub by installing Workstream's GitHub App on its account or organization ([the core model](core-model.md), "Connect GitHub"). The API holds the app's private key and signs, with it, the short JWT GitHub takes from an app (`api/GitHub/GitHubApp.cs`, RS256, nine minutes). With that it asks GitHub for an installation token whenever a desktop asks for one, for that desktop's repository alone, and hands it over without keeping it.

On the desktop, git's credential helper for `https://github.com` is `desktop/agent/github.mjs`, and `gh` is a small wrapper first on the PATH (`desktop/agent/shims/gh`) that sets `GH_TOKEN` from the same script each time it runs. Each asks the API with the desktop's key, which it reads where the agent does (its user data, never its disk), so a token never runs out in the middle of a task and none is written down. A `GITHUB_TOKEN` an environment already has is used as it is. The agent is told to finish project work as a branch and a pull request, never on the default branch.

## What a desktop can and cannot do

A desktop runs whatever its agent is asked to, so it is treated as untrusted:

- It holds one credential, its key. The key reaches `/api/desktop/*` and `/api/model/*` for its own task. It is not a person's token and opens no other route. The key is in the machine's user data, which Azure serves to that machine alone, and is never written to its disk: a copy of a desktop's disk carries nobody's key.
- It never holds a model key. A leaked desktop key is worth what is left of one team's monthly allowance, until the task is deleted.
- It can ask for a GitHub token for its own environment's repository, when its team has connected GitHub: one repository, to push and open pull requests, for an hour. It never holds the app's key or anyone's own GitHub token. A leaked desktop key is also worth that repository's tokens until the task is deleted.
- It has no public address. Its subnet admits the API's relay to the display and refuses everything else on the network, other desktops included. It reaches the internet through one NAT gateway.
- It has no Azure identity.
- A spare is a desktop with no task. Its key authenticates and nothing more until a task takes it, and it is never given to another team.
- A machine preparing an environment holds a key that reads that environment's setup, gets a GitHub token for that environment's repository while its attempt lasts, and says how it went, and nothing else: no task, no conversation, no model. Its disk is saved for the environment's tasks only after it has removed the variables, the logs and the browser's profile, and its key is in its user data, never on the disk. What the setup script itself leaves on the disk is in the save, and every task of the environment starts with it.
- An environment's variables are stored protected and are sent only to a desktop made from that environment. They are on that desktop in plain text, because the work needs them there: anyone on the team can read them by opening the desktop.

## What is kept where

| Kept in | What |
| --- | --- |
| SqlOS (the app's SQL database) | People, teams, memberships, invitations, sessions; the resource tree and every grant. |
| The app's tables | Teams' limits and their pause and archive times, environments, tasks, messages, desktop runs (for hours by size), storage runs (for GB-months by kind), model usage (for dollars), and which GitHub installation a team is connected to. No GitHub token is kept anywhere. |
| A desktop's disk | The work itself: the repository, files, the browser's profile, the agent's session, and what to start again after a stop. It goes with the task. |
| A task's archive (a snapshot in the desktops' group) | A copy of that disk, in place of the disk, while the task is archived. It goes with the task. |
| A saved setup (a snapshot in the desktops' group) | An environment's repository and whatever its setup script installed, and nothing of any task. It goes with the environment. |
| The browser | The session's tokens, and which team was open last. |
