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
- **The Mac** is not a machine Workstream keeps: it is a run of one workflow in Workstream's own repository on GitHub's Mac runners, started by the API when a desktop needs an iPhone app built ("Phones on a desktop").
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

What it can cost is limited by settings (`Workstream:Desktops:Saves`): one machine preparing at once over every team, four hours of preparing a day, each attempt as long as the setup script may run and half an hour more ("The setup script", above), one save an environment, deleted after 30 days nothing started from it. Its time is a line of its own on the system administrator's page, and so is the storage the saves take.

The Azure host's part of this is checked against Azure itself by `tests/Workstream.Tests/AzureSavedSetupLive.cs`, which runs `AzureDesktopHost` in a resource group of its own when it is told one ([infra/README.md](../infra/README.md), "What it costs when people use it"): a machine from the gallery image, a file written on its disk, the machine stopped and its disk saved, a second machine made from the save with the file there, and all of it removed. Measured in West US 3 on 21 November 2026, from image 0.6.0:

| Step | Standard (4 cores, 64 GB disk, 12 GB on it) | Large (8 cores, 128 GB disk, 22 GB on it) |
| --- | --- | --- |
| Make a machine from the image, until it has an address | 5 s | 4 s |
| That machine is on | 8 s | 8 s |
| Stop it, once the setup is done | 15 s | 15 s |
| Save its disk (a full snapshot) | 3 s | 3 s |
| Make a machine from the save, until it has an address | 6 s | 6 s |
| That machine is on | 13 s | 8 s |
| Its desktop's service starts, counted from its kernel | not asked | 5 s, and 6 s on the machine from the image |
| Read 10 GB the setup wrote, for the first time | not written | 142 s, the speed of the disk (writing them took 134 s) |

So a machine made from a save starts like one made from the image, what the setup left is all there and reads at the disk's own speed, and the save is ready seconds after the setup ends. Azure's own times vary from run to run: in another run of the same check a machine took 52 seconds to be on, and Azure's guest agent, which a desktop does not wait for, took four minutes to answer.

## Running, paused, archived

A desktop costs its machine by the hour while it runs and its disk by the month for as long as the disk exists, so a desktop nobody is using is stepped down in two stages ([the core model](core-model.md#pause-archive-delete) has the rules and who may do what):

- **Paused.** `IDesktopHost.StopAsync` releases the machine (Azure: deallocate; Docker: stop) and keeps its disk. Where a deployment hibernates its desktops ("Hibernating a paused desktop" below; no deployment does yet), the machine writes its memory to that disk first. A person pauses a desktop from its menu; the sweep (`DesktopJobs.SweepAsync`, every five minutes) pauses each ready desktop that has not been used for its team's pause time, 30 minutes unless the team changed it. Resuming starts the same machine (`StartAsync`), which takes Azure between fifteen seconds and a minute, and the agent picks the conversation up where it was.
- **Archived.** The sweep archives a desktop that has been paused for its team's archive time, 7 days unless the team changed it (`DesktopJobs.ArchiveAsync`). It copies the disk (`IDesktopHost.ArchiveAsync`: on Azure a full snapshot on standard storage, named `wsa-…` and tagged `archive`; in Docker the stopped container committed as a `workstream-archive` image), writes the archive's name and size on the task (`Archive`, `ArchiveBytes`), and then deletes the machine and its disk. Bringing it back is making a machine with `DesktopSpec.Archive`: on Azure a disk is made from the snapshot and attached, the same way a machine is made from a saved setup, and the machine gets a new key in its user data. The archive is deleted once the new machine's display has answered; a start that fails before that removes the machine and keeps the archive.
- **Deleted.** Only when a person deletes the task: the machine, its disk and its network interface, the archive if there is one, and its key.

### Hibernating a paused desktop

A pause throws away every program on the desktop, and the agent starts again what it can ("After a stop"). Azure can instead hibernate a machine: it asks the machine to write its memory to its own disk, releases it as a stop does, and the next start reads the memory back, so every program is where it was. `AzureDesktopHost` does that when `Workstream:Desktops:Azure:Hibernate` is on, which no deployment has yet ([hibernation.md](hibernation.md) has what was measured and what turning it on means).

- **Making the machine.** With hibernation on and without Trusted Launch, because Azure refuses the two together on Linux; on a size Azure can hibernate (the Dasv6 sizes, set in `infra/api.bicep`); from the gallery's `machine-hibernate` definition, because Azure makes such a machine only from a definition that says its image can hibernate. A machine preparing a saved setup is made without hibernation, so no machine's memory is ever on a disk other machines start from. A machine made from a saved setup or an archive is the kind its disk was made as, whatever the deployment does now: a disk keeps its security type, so a desktop archived with Trusted Launch comes back with it and does not hibernate.
- **Pausing.** `StopAsync` asks Azure to deallocate with `hibernate=true` and waits for the machine to be released. A machine that was not made to hibernate (one from before the deployment hibernated, or a preparing one) is stopped the plain way without asking. So is one Azure refuses to hibernate, or one that does not manage it (Azure waited ten minutes for such a machine before it gave up, and the API waits that out), with a warning in the log: the desktop then comes back as from any pause.
- **On the desktop** (`desktop/image/hibernate.sh`, installed by `configure.sh`). At boot, on a machine Azure made with hibernation (its firmware offers the S4 sleep state) and on no other, a swap file as large as the machine's memory is made and the boot settings are told where it is. Azure's request arrives through the Hyper-V tools as a kernel event; a udev rule answers it by turning that swap on and telling the kernel to hibernate, and turns the swap off again when the machine is back. The desktop runs with no swap, as it always has.
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

The Android emulator runs on every size, on AMD's processors as on Intel's. `/dev/kvm` is open to the desktop's person through the `kvm` group, which the desktop's service carries, and the emulator boots as that person to Android's home screen, with `adb` listing it. Measured on desktops made from image 0.6.0, started on the desktop's display as the agent starts it:

| Machine | First boot on a new desktop | Later boots |
| --- | --- | --- |
| `Standard_D8as_v7` (AMD EPYC 9V45) | 46 s with the disk already read, up to 180 s cold | 33 s |
| `Standard_D8as_v6` (AMD EPYC 9V74) | 92 s | 32 s |

Two things were wrong before, and neither was the processor. The emulator stops at once ("QEMU main loop exits abnormally with code 1") when it is started from a folder its user cannot read, such as the root-only folder an Azure run command starts in; that is what made it look as if it ran as root and not as the desktop's person. And the phone `avdmanager` makes by itself has two processors and 2 GB, which with the software renderer a desktop has took three minutes to start and came up with "System UI isn't responding"; the desktop's phone has four processors and 4 GB and a hardware keyboard (`image/phone.sh`). So the emulator wants a Standard or Large desktop; on a Small one it starts, slowly. The bake boots a throwaway phone made the same way, as the desktop's person, with the command the agent is told, and fails unless Android says it has booted and `adb` lists it (`image/check-emulator.sh`); `emulator -accel-check` alone passes even where the emulator cannot start. The iPhone is the next section.

`host.docker.internal` is the desktop, from its containers and from itself. On a Mac, Docker Desktop gives every container that name for the computer it runs on, and projects' scripts, compose files and settings use it; Docker Engine on Linux has no such name and no setting that adds one to every container. A desktop gives it without any change to a project (`image/configure.sh`):

- The name is one address, `172.16.255.254`, in the desktop's `/etc/hosts`. It is outside the networks Docker hands out.
- Containers ask the desktop for names. Docker tells each container to ask at its bridge's address (`"dns"` in `daemon.json`), where the desktop's own resolver listens as well (`DNSStubListenerExtra`) and answers from `/etc/hosts` before asking onward as it did. That covers `docker run`, a compose project's own network and the steps of a `docker build`.
- `--add-host host.docker.internal:host-gateway`, which projects written with Linux in mind already pass, means the same address (`"host-gateway-ip"`).
- Whatever is sent to that address is delivered to the desktop's loopback address (`image/host-docker-internal.nft`, loaded at boot by `workstream-host-name.service`, with `route_localnet` on). So a container reaches a server on the desktop whether it listens on every address or only on `localhost`, as it does through Docker Desktop, and reaches another container's published port the same way.

The bake checks it from a network namespace joined to Docker's bridge, which is what a container is, against a server that listens only on localhost (`image/check-host-name.sh`). On real desktops of both sizes above it was checked after a restart with `docker run`, a compose service, a `docker build` step, `--add-host`, `--network host`, a container's published port, and as the desktop's person, and names outside the desktop resolved as before. A server that listens only on IPv6's `::1` is not reached. The local container desktop has no Docker inside it; it is given the name by Docker itself (`--add-host host.docker.internal:host-gateway` in `DockerDesktopHost`), which is how it reaches the API on a Linux computer too.

A desktop started again after a stop starts Docker when it has containers, so the ones a project told Docker to restart (a database with a restart policy) come back with it.

The agent is told how to work on a project the way a developer does (`agent/instructions.mjs`): follow the repository's `CLAUDE.md` and `AGENTS.md`, use its skills for running the stack or implementing an issue rather than working the steps out, give long builds a long timeout, and deliver changes on a branch of their own as a pull request opened with `gh`. git and gh sign in through the team's GitHub connection or a pasted `GITHUB_TOKEN` (`agent/github.mjs`, "GitHub" below).

## Phones on a desktop

An environment can give its desktops two more apps (`desktop/apps.json`): `android`, the Android phone above in a window of its own, and `iphone`, an iPhone simulator. Each has a button on the panel, comes up with the desktop, and is there for the person to click and type on. The agent uses them the way a person does: it opens the app, reads the screen and taps through what it changed, and is told to check a change on every surface the desktop has (`agent/phones.mjs`).

One command is the same for the agent, a terminal and the panel's buttons (`agent/phone.mjs`, on the PATH as `phone`):

| | |
| --- | --- |
| `phone android start` | Starts the emulator and waits until Android has booted. The Android app is built on the desktop, by the project's own tools. |
| `phone ios start` | Boots the iPhone and opens its window. |
| `phone ios run <folder>` | Gets the project's iPhone app (below), installs it and opens it. |
| `phone ios snapshot`, `tap`, `type`, `scroll`, `open`, `screenshot`, `logs` | Reads the iPhone's screen as lines of text with a reference each, and acts on it. |

### The iPhone simulator

It is Apple's own iOS runtime, run on Linux by [x86Sim](https://github.com/x86sim/x86sim), which translates the runtime's arm64 code and draws with Mesa's software Vulkan, and driven by [agent-sim](https://github.com/Developing-Gamer/agent-sim), which gives the screen as text, takes taps and typing, and streams the screen to a browser. The image has agent-sim, one release checked against its hash (`image/configure.sh`), and the packages the simulator needs. The simulator itself is not in the image: it is x86Sim, the runtime (8 GB, downloaded from Apple) and one phone, 30 GB in all, and only some desktops want it.

- **Installing it** (`phone ios setup`) is the first line of the setup of an environment that has the iPhone, before the environment's own script (`setupScriptFor`). So it is done once, on the machine that prepares the environment, and is in the saved setup every task starts from. Such an environment has a setup even with no repository and no script (`WorkEnvironment.HasSetup`).
- **Size.** The phone takes 9 GB of memory, beside the project's own stack, so the iPhone needs a Large desktop, and an environment with it on a smaller one is refused.
- **Its window** is a Chromium window on agent-sim's stream, which listens on the desktop's own loopback address with a token in its address. Nothing of the phone is reachable from outside the desktop: a person sees it as they see everything else, on the desktop's display through the API's relay.
- **Where it runs.** agent-sim keeps the phone as a service of the person's own systemd, which the desktop's session is not part of. The image keeps that systemd running (lingering), and `phone` reaches it from wherever it is called.

Measured on a `Standard_D8as_v7` (AMD EPYC 9V45) running Debian 13, on 24 November 2026: installing took 17 minutes, the phone's first boot 209 seconds and later boots 26 to 35, installing an app of 179 MB took 48 seconds, and a change to a screen's code reached an Expo development build from Metro on the same machine in about two and a half seconds. In about an hour of use the phone stopped answering once, and `phone ios stop` and `start` brought it back in half a minute. Siri does not open on it, and it has no debugger.

Apple's simulator runtime comes with Apple's licence terms, which whoever runs a deployment should read for themselves.

### The iPhone app is built on a Mac

An iPhone app is built with Xcode, which needs macOS. A desktop sends the app's source to the API, and the API has it built on a Mac and keeps the result for the team ([the core model](core-model.md#build-an-iphone-app) has who may do what).

```text
the desktop                        the API                                the Mac
phone ios build
  hash of the native code   ─────► the team's build for that hash?
                             ◄───── done: here is the app
                                    or: waiting for its source
  the source, one archive   ─────► kept in the team's storage
                                    asks GitHub to run ios-build.yml ───► a new Mac, for this build
                                                                 ◄─────── take the build, with the builder's key
                                    the source ─────────────────────────► builder/ios.sh
                                                                 ◄─────── the log, the app, how it ended
  asks every ten seconds     ◄───── done: here is the app
install, open
```

- **The hash** is what makes a build reusable. For an Expo app it is Expo's own fingerprint of the native project; for any other, a hash of the files that name its packages, configure the app or are native source. The kind of app, its folder and the Mac's recipe version (`RECIPE` in `phone.mjs`) go into it too. JavaScript and Dart are not in it: they reach the phone from the desktop's own server, so a task that changes only those takes the build the team already has.
- **The source** is the files git knows of or would add, from the folder the app's packages are installed from: the app's own folder when it has a lockfile, or the nearest folder above it that has one, for an app in a workspace. Nothing git ignores is sent.
- **The Mac** is `.github/workflows/ios-build.yml` in Workstream's own repository, on GitHub's `xcode-27` runner. `builder/ios.sh` builds the Debug configuration for the arm64 simulator: an Expo app (`expo prebuild`, then `xcodebuild`), a React Native app (`pod install`, then `xcodebuild`), a Flutter app or a plain Xcode project. Only the Expo path has been run on a real project so far.
- **Nothing of a project is in the run's log.** That repository is public, and so are its runs. The build's whole output goes to a file that is sent to the API, and nothing is uploaded as an artifact; the run says that a build ran, for how long and whether it worked. The run is told the build's id and the API's address, which are no secret; its key is the GitHub environment's `BUILDER_KEY` secret, the same value the API was deployed with.
- **The files** (the source until the build ends, then the app and the log) are in a storage account of the deployment's own, private, read and written only by the API's identity (`AzureBuildFiles`, `infra/data.bicep`), under the team's id. In development and the tests they are in a folder.
- **Who starts the Mac.** The request to GitHub needs a token that may run the workflow. A deployment can set one (`Workstream:Builds:Token`); without it the API uses the system administrator's own GitHub token, from their sign-in with GitHub, since the deployment's administrator is who owns the repository.

Measured on 24 November 2026 with Genius, an Expo 58 app on React Native 0.88: the source was 25 MB as an archive, the Mac built it with Xcode 27.2 in 1,070 seconds, and the app came back as 38 MB.

A Mac that GitHub runs is new for each build, so no two teams' builds ever share a machine. GitHub's terms limit what its runners may be used for; a deployment that builds for many teams should read them, and can put another Mac behind `IMacBuilder` without changing anything a desktop does.

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
| A minute of the Mac, for an iPhone build | $0.062 (`Workstream:Builds:MacMinuteUsd`): GitHub's price for its standard Mac runner in a private repository. In a public one, as Workstream's is, GitHub does not charge for it. |

The system administrator's page multiplies each line by its rate. A team's Usage page shows the same lines as amounts, hours by size and GB-months by kind, with no dollars: there is no customer price yet, and when there is one it is a second column in this table, not new rows. A spare's and a preparing machine's own disks last minutes and are not counted.

## Models

The harness calls models the way it calls Microsoft Foundry, at `…/api/model/anthropic` on the API, with the desktop's key. The gateway (`api/Model/ModelGateway.cs`) checks the key and the team's allowance, swaps in Workstream's own Foundry key, passes the request and the answer through as they are, and reads the token counts off the answer. Each call is saved as a `ModelUsage` row with its cost in dollars. What a team has left this month is its allowance less the sum of those rows; the gateway remembers it for a few seconds between calls.

The prices are a table in `ModelOptions`, with the two things that make a call cost more than its plain rate: a cache write kept for an hour, and Haiku's higher rate for a prompt over 100,000 tokens. They are what Workstream pays; `Markup` is 1 until there is billing.

## GitHub

People may sign in with GitHub, which is SqlOS's own connection to a GitHub OAuth client, with the scopes `read:user`, `user:email` and `repo` ([the core model](core-model.md), "Sign up and sign in"). SqlOS keeps the token GitHub gives it; the API reads a person's token through `IGitHubTokens` (`api/GitHub/GitHub.cs`) and calls GitHub's API with it through one named client (`GitHubClient`): the person's repositories and branches, a repository's tree and files, a commit, a branch and a pull request. Nothing in the app keeps the token.

The API uses it for the environment form (the person's repositories and branches, and a repository's workstream.yml), for saving workstream.yml, and for the desktops: a task's desktop gets the token of the person who started the task, so the agent pushes and opens pull requests as them ([the core model](core-model.md), "A desktop's GitHub token").

On the desktop, git's credential helper for `https://github.com` is `desktop/agent/github.mjs`, and `gh` is a small wrapper first on the PATH (`desktop/agent/shims/gh`) that sets `GH_TOKEN` from the same script each time it runs. Each asks the API with the desktop's key, which it reads where the agent does (its user data, never its disk), so a token never runs out in the middle of a task and none is written down. A `GITHUB_TOKEN` an environment already has is used as it is. The agent is told to finish project work as a branch and a pull request, never on the default branch.

## What a desktop can and cannot do

A desktop runs whatever its agent is asked to, so it is treated as untrusted:

- It holds one credential, its key. The key reaches `/api/desktop/*` and `/api/model/*` for its own task. It is not a person's token and opens no other route. The key is in the machine's user data, which Azure serves to that machine alone, and is never written to its disk: a copy of a desktop's disk carries nobody's key.
- It never holds a model key. A leaked desktop key is worth what is left of one team's monthly allowance, until the task is deleted.
- It can ask for the GitHub token of the person who started its task, which reaches what their GitHub account reaches. It is never written to the desktop's disk, but it is in the desktop's memory while git or gh uses it. A leaked desktop key is also worth that person's GitHub access until the task is deleted.
- It has no public address. Its subnet admits the API's relay to the display and refuses everything else on the network, other desktops included. It reaches the internet through one NAT gateway.
- It has no Azure identity.
- A spare is a desktop with no task. Its key authenticates and nothing more until a task takes it, and it is never given to another team.
- A machine preparing an environment holds a key that reads that environment's setup, gets the GitHub token of whoever last saved the environment while its attempt lasts, and says how it went, and nothing else: no task, no conversation, no model. Its disk is saved for the environment's tasks only after it has removed the variables, the logs and the browser's profile, and its key is in its user data, never on the disk. What the setup script itself leaves on the disk is in the save, and every task of the environment starts with it.
- A container on a desktop reaches that desktop's own loopback address through `host.docker.internal`, the agent's window and the browser's debugging port included. That is no more than any program on the desktop can do, and a container there is the task's own: nothing outside the machine can use that way in (the rule matches Docker's bridges only, and the network admits only the API's relay).
- An environment's variables are stored protected and are sent only to a desktop made from that environment. They are on that desktop in plain text, because the work needs them there: anyone on the team can read them by opening the desktop.
- It can have an iPhone app built: the source it sends for that goes to the API, and from there to a Mac that GitHub runs, for as long as the build takes. It can read its own team's builds, by a hash or an id, and nobody else's.
- The Mac holds one credential, the builder's key, which is a secret of the deployment and of the workflow's GitHub environment. It reaches a build's source only between taking that build and ending it, and nothing else of any team: no task, no conversation, no model, no GitHub token.

## What is kept where

| Kept in | What |
| --- | --- |
| SqlOS (the app's SQL database) | People, teams, memberships, invitations, sessions; the resource tree and every grant; the GitHub token of each person who signed in with GitHub, encrypted. |
| The app's tables | Teams' limits and their pause and archive times, environments, tasks, messages, how the setup script went on a task's desktop (its state and last lines), desktop runs (for hours by size), storage runs (for GB-months by kind), and model usage (for dollars). The app keeps no GitHub token of its own. |
| A desktop's disk | The work itself: the repository, files, the browser's profile, the agent's session, and what to start again after a stop. It goes with the task. |
| A task's archive (a snapshot in the desktops' group) | A copy of that disk, in place of the disk, while the task is archived. It goes with the task. |
| A saved setup (a snapshot in the desktops' group) | An environment's repository and whatever its setup script installed, and nothing of any task. It goes with the environment. |
| The builds' storage account (in the deployment's own group) | The source of an iPhone build until the Mac is done with it, then the app and the Mac's log, under the team's id, for as long as a desktop keeps taking the app and thirty days more. |
| The browser | The session's tokens, and which team was open last. |
