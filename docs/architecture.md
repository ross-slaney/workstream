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
6. The agent reads its setup from the API with its key: the apps for the panel, the repository, the setup script, the variables and the instructions. It prepares the desktop where the person can watch: it sets git and gh up to ask the API for a GitHub token when they need one, clones the repository, shows the environment's apps on the panel, runs the setup script in a terminal window, and opens the browser and the editor. The agent starts on what the person asked once the script has ended ("The setup script", below).
7. The person's browser reads the conversation twice a second while the desktop is starting, and every second and a half after. It shows each step as the task reports it (`step`: `machine`, `boot`, `agent`), and opens a WebSocket to the API for the display as soon as there is one.

Each of those moments is kept with the run (`DesktopRun`: asked, on, display, ready), so how long a start takes is something the product knows and not something anyone has to guess. The system administrator's page shows the middle start of the last seven days, by what it started from.

## Starting a task from a terminal

`cli/workstream.mjs` starts the same task from a person's own computer, which is how a local Claude Code hands an issue to Workstream (`.agents/skills/start-workstream-task`). It signs in once through the browser with SqlOS's device flow, approved on the web app's `/device` page, and then calls the same REST API as the web app with a token of the person's that can read and start tasks and nothing else ([the core model](core-model.md#the-command-line)). `workstream start` posts the task, prints its link in the web app, and reads the task every two seconds until its desktop is ready.

## The setup script

An environment's setup script installs and builds what its project needs: packages, container images, a first build. It runs once on a new desktop, as the desktop's person, in the repository's folder, with the environment's variables.

- **How long it may run** is one setting, `Workstream:Desktops:SetupMinutes`, 45 minutes unless a deployment says otherwise. The desktop is told it with its setup (`setupMinutes`) and keeps the time itself (`desktop/agent/setup.mjs`). A script still running then is stopped, with everything it started (its whole process group; containers are Docker's and stay), and that is reported as a setup that did not finish in time. There is no other limit on it: a task's desktop is `ready` from its agent's first exchange, which comes before the script starts, so the sweep's start timeout (`StartTimeoutMinutes`, for a desktop that never exchanged) never applies to a setup that is running, and a desktop whose setup runs says its agent is at work, so it is not stopped as idle. Cloning the repository has its own half hour.
- **What the person sees.** On a task's own desktop the script runs in a terminal window on the screen, and the desktop tells the API how it is going every five seconds (`POST /api/desktop/setup`: `running` with the last 4,000 characters it wrote, then `done` or `failed` with its exit code). The API keeps that with the task (`TaskSetups`), and the task's conversation read carries it (`setup`), so the task's screen says "Running the setup script", how many minutes of how many, and the script's last lines as they change. A script that failed, or was stopped for running too long, is said in those words with its last lines, on the screen and as the agent's message in the conversation. The task does not fail with it: the desktop is there, and the agent is told in its instructions that the setup did not finish and where its log is (`~/.workstream/setup.log`).
- **On a machine preparing a saved setup** the same script runs out of sight with the same limit, and what it writes reaches the environment's page the same way. The whole attempt (the machine, the clone, the script, saving the disk) is given the script's limit and half an hour more, at least (`DesktopOptions.PrepareMinutes`; `Saves:MaxMinutes`, 75, when that is longer).
- **What does not belong in it.** A program the script leaves running does not outlast it: on a task's desktop it ends with the script's terminal window, and a saved setup keeps a disk, not what was running. Starting the stack is the agent's work, from the repository's own instructions or skill. Containers are the exception that is kept: images pulled and containers made during setup are on the disk, and in a save.

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
- A task's machine is made from the save while the save is for the environment's setup inputs (a fingerprint of the repository, branch, script, variables, apps and size, since a save is one size's disk) and for this release's image version. On Azure a disk is made from the snapshot with `createOption: Copy`, stating no security profile because it inherits Trusted Launch from the snapshot, and the machine attaches it (`createOption: Attach`). The task's agent finds `prepared.json`, pulls the branch, skips the setup script, and says "Started from the setup prepared 2 days ago (main at a1b2c3d), 14 commits newer now."
- Anything else starts from the image and does the setup itself, as before: an environment never prepared, a save out of date, a preparation that failed, and a task started while its environment is still being prepared.

What it can cost is limited by settings (`Workstream:Desktops:Saves`): one machine preparing at once over every team, four hours of preparing a day, each attempt as long as the setup script may run and half an hour more ("The setup script", above), one save an environment, deleted after 30 days nothing started from it. Its time is a line of its own on the system administrator's page, and so are the saves kept.

The Azure host's part of this is checked against Azure itself by `tests/Workstream.Tests/AzureSavedSetupLive.cs`, which runs `AzureDesktopHost` in a resource group of its own when it is told one ([infra/README.md](../infra/README.md), "What it costs when people use it"): a machine from the gallery image, a file written on its disk, the machine stopped and its disk saved, a second machine made from the save with the file there, and all of it removed. Measured in West US 3 on 21 November 2026, from image 0.6.0:

| Step | Standard (4 cores, 64 GB disk, 12 GB on it) | Large (8 cores, 128 GB disk, 22 GB on it) |
| --- | --- | --- |
| Stop the preparing machine | 15 s | LARGE_STOP |
| Save its disk (a full snapshot) | 3 s | LARGE_SAVE |
| Make a machine from the save, until it has an address | 6 s | LARGE_CREATE |
| That machine is on | 13 s | LARGE_ON |
| Its guest answers a command | 30 s | LARGE_GUEST |
| Read 10 GB the setup wrote, for the first time | not written | LARGE_READ |

A machine made from the image took 5, 8 and 15 seconds for the same three steps, so starting from a save costs nothing a person would notice, and the save is ready seconds after the setup ends.

Stopping a task deallocates its machine and keeps its disk. Starting it again brings the same desktop back, and the agent resumes the same conversation. Deleting a task deletes the machine, its disk and its network interface, and ends its key. A desktop nobody watches or writes to, whose agent is idle, is stopped after half an hour by a sweep that also fails a desktop that never arrived, and deletes a machine no task knows once it has been there half an hour (a newer one may belong to a task that has just started).

## The display

A desktop's display is a VNC server with no password, on a network that admits one caller: the API. The browser never reaches a desktop. It opens `GET /api/tasks/{id}/desktop` as a WebSocket carrying the person's access token; the API validates the token (the web app's, with `workstream.write`), checks `TASK_VIEW` on the task, connects to the desktop's private address, and passes bytes both ways. What the browser sends is read message by message (`api/Desktops/RfbClientFilter.cs`): for someone a task is shared with only to watch or to write to the agent, without `TASK_DRIVE`, key and pointer events and the clipboard are dropped there, and the browser draws their screen view-only. It does this from the moment the display has answered, which is before the agent's first exchange, until the desktop is asked to stop. It checks again every minute and closes the stream when access is gone. The browser draws it with noVNC.

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

## A project's whole stack on a desktop

A team's real project usually runs as several programs at once: a database in a container, an API, a bundler, a browser driving it. The image has what such a stack needs (`desktop/image/install.sh`): Docker Engine, the .NET 10 SDK (Aspire 9 and later is a NuGet package of the project, with no workload), Node 22, sqlcmd, Playwright's system libraries (the browsers themselves are the project's own version), Java 17, and the Android SDK with an emulator set up as `Phone`. The ASP.NET Core development certificate is made and trusted for the desktop's person, and the limits a big project reaches (watched files, open files) are raised.

| Size | Machine | Disk |
| --- | --- | --- |
| Small | 2 cores, 8 GB (`Standard_D2as_v7`) | 64 GB |
| Standard | 4 cores, 16 GB (`Standard_D4as_v7`) | 64 GB |
| Large | 8 cores, 32 GB (`Standard_D8as_v7`) | 128 GB |

The disk of each size is a setting (`Workstream:Desktops:Azure:DiskSizesGb`) priced beside the machines (`DesktopOptions.Prices`). The image is 64 GB, so a machine with a larger disk grows its file system to it when it first boots (`image/grow-disk.sh`). A project with a database, an API and a web bundler wants Large.

The Android emulator runs on every size, on AMD's processors as on Intel's. `/dev/kvm` is open to the desktop's person through the `kvm` group, which the desktop's service carries, and the emulator boots as that person to Android's home screen, with `adb` listing it. Measured on desktops made from image 0.6.0, started on the desktop's display as the agent starts it:

| Machine | First boot on a new desktop | Later boots |
| --- | --- | --- |
| `Standard_D8as_v7` (AMD EPYC 9V45) | 46 s with the disk already read, up to 180 s cold | 33 s |
| `Standard_D8as_v6` (AMD EPYC 9V74) | 92 s | 32 s |

Two things were wrong before, and neither was the processor. The emulator stops at once ("QEMU main loop exits abnormally with code 1") when it is started from a folder its user cannot read, such as the root-only folder an Azure run command starts in; that is what made it look as if it ran as root and not as the desktop's person. And the phone `avdmanager` makes by itself has two processors and 2 GB, which with the software renderer a desktop has took three minutes to start and came up with "System UI isn't responding"; the desktop's phone has four processors and 4 GB and a hardware keyboard (`image/phone.sh`). So the emulator wants a Standard or Large desktop; on a Small one it starts, slowly. The bake boots a throwaway phone made the same way, as the desktop's person, with the command the agent is told, and fails unless Android says it has booted and `adb` lists it (`image/check-emulator.sh`); `emulator -accel-check` alone passes even where the emulator cannot start. iPhone and iPad builds and the iOS simulator need macOS and Apple's tools, which no Linux desktop can run. The agent is told so and does the rest of a task.

`host.docker.internal` is the desktop, from its containers and from itself. On a Mac, Docker Desktop gives every container that name for the computer it runs on, and projects' scripts, compose files and settings use it; Docker Engine on Linux has no such name and no setting that adds one to every container. A desktop gives it without any change to a project (`image/configure.sh`):

- The name is one address, `172.16.255.254`, in the desktop's `/etc/hosts`. It is outside the networks Docker hands out.
- Containers ask the desktop for names. Docker tells each container to ask at its bridge's address (`"dns"` in `daemon.json`), where the desktop's own resolver listens as well (`DNSStubListenerExtra`) and answers from `/etc/hosts` before asking onward as it did. That covers `docker run`, a compose project's own network and the steps of a `docker build`.
- `--add-host host.docker.internal:host-gateway`, which projects written with Linux in mind already pass, means the same address (`"host-gateway-ip"`).
- Whatever is sent to that address is delivered to the desktop's loopback address (`image/host-docker-internal.nft`, loaded at boot by `workstream-host-name.service`, with `route_localnet` on). So a container reaches a server on the desktop whether it listens on every address or only on `localhost`, as it does through Docker Desktop, and reaches another container's published port the same way.

The bake checks it from a network namespace joined to Docker's bridge, which is what a container is, against a server that listens only on localhost (`image/check-host-name.sh`). On real desktops of both sizes above it was checked after a restart with `docker run`, a compose service, a `docker build` step, `--add-host`, `--network host`, a container's published port, and as the desktop's person, and names outside the desktop resolved as before. A server that listens only on IPv6's `::1` is not reached. The local container desktop has no Docker inside it; it is given the name by Docker itself (`--add-host host.docker.internal:host-gateway` in `DockerDesktopHost`), which is how it reaches the API on a Linux computer too.

A desktop started again after a stop starts Docker when it has containers, so the ones a project told Docker to restart (a database with a restart policy) come back with it.

The agent is told how to work on a project the way a developer does (`agent/instructions.mjs`): follow the repository's `CLAUDE.md` and `AGENTS.md`, use its skills for running the stack or implementing an issue rather than working the steps out, give long builds a long timeout, and deliver changes on a branch of their own as a pull request opened with `gh`. git and gh sign in through the team's GitHub connection or a pasted `GITHUB_TOKEN` (`agent/github.mjs`, "GitHub" below).

## Models

The harness calls models the way it calls Microsoft Foundry, at `…/api/model/anthropic` on the API, with the desktop's key. The gateway (`api/Model/ModelGateway.cs`) checks the key and the team's allowance, swaps in Workstream's own Foundry key, passes the request and the answer through as they are, and reads the token counts off the answer. Each call is saved as a `ModelUsage` row with its cost in dollars. What a team has left this month is its allowance less the sum of those rows; the gateway remembers it for a few seconds between calls.

The prices are a table in `ModelOptions`, with the two things that make a call cost more than its plain rate: a cache write kept for an hour, and Haiku's higher rate for a prompt over 100,000 tokens. They are what Workstream pays; `Markup` is 1 until there is billing.

## GitHub

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
- A container on a desktop reaches that desktop's own loopback address through `host.docker.internal`, the agent's window and the browser's debugging port included. That is no more than any program on the desktop can do, and a container there is the task's own: nothing outside the machine can use that way in (the rule matches Docker's bridges only, and the network admits only the API's relay).
- An environment's variables are stored protected and are sent only to a desktop made from that environment. They are on that desktop in plain text, because the work needs them there: anyone on the team can read them by opening the desktop.

## What is kept where

| Kept in | What |
| --- | --- |
| SqlOS (the app's SQL database) | People, teams, memberships, invitations, sessions; the resource tree and every grant. |
| The app's tables | Teams' limits, environments, tasks, messages, how the setup script went on a task's desktop (its state and last lines), desktop runs (for hours), model usage (for dollars), and which GitHub installation a team is connected to. No GitHub token is kept anywhere. |
| A desktop's disk | The work itself: the repository, files, the browser's profile, the agent's session, and what to start again after a stop. It goes with the task. |
| A saved setup (a snapshot in the desktops' group) | An environment's repository and whatever its setup script installed, and nothing of any task. It goes with the environment. |
| The browser | The session's tokens, and which team was open last. |
