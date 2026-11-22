# Core model

Workstream gives each task its own cloud desktop with an agent working on it. A person watches the desktop in the browser, talks to the agent beside it, takes the mouse when they want to, and keeps up to four tasks on screen at once.

This document defines who can do what to what. It is written in SqlOS terms first: principals, nodes, permissions, roles and grants. Tables hold only what SqlOS cannot express. Code that disagrees with this document is wrong, or this document is out of date; fix one of them in the same pull request.

## Words

| Word | Meaning |
| --- | --- |
| Team | A group of people who share tasks and environments. A person who signs up gets a team of their own. Someone who creates their account from an invitation joins that team and gets none of their own. Anyone can create another. |
| Environment | What a desktop comes with: its apps and, optionally, a repository, a setup script, variables and instructions for the agent. |
| Saved setup | An environment's repository cloned and setup script run once, on a machine made only for that, whose disk is kept. A task's desktop starts from it with the setup already done. |
| Task | One piece of work. It has a desktop and a conversation with the agent on that desktop. |
| Desktop | The task's cloud computer: a Linux virtual machine with a graphical desktop. |
| Running | A desktop someone is using or whose agent is working. |
| Paused | A desktop whose machine is released and whose disk is kept. It resumes in about half a minute. In the code and the API this is the state `stopped`, the name the rows already hold. |
| Archived | A desktop paused for as long as its team allows: its disk is copied to an archive, and the machine and the disk are deleted. It comes back on a new machine made from the archive. |
| Agent | The program on the desktop that does the work. It is built on the Claude Agent SDK and calls models only through Workstream. |
| Message | One entry in a task's conversation, written by a person or by the agent. |
| Share | One task given to a person outside its team: to watch it, to write to its agent too, or to use its desktop as well, for a while or until it is taken back. |
| GitHub connection | A team's link to one installation of Workstream's GitHub App on a GitHub account or organization. The team's desktops get short-lived tokens for their repositories through it. |

## Principals: who

Any column that names an actor holds a principal id.

| SqlOS type | In Workstream | Id | Notes |
| --- | --- | --- | --- |
| `user` | A person | the SqlOS user id | Made at sign-up. |
| `group` | A team's people | `group::{teamId}` | Mirrors the team's memberships. Joining the team is joining its group. |
| `group` | System administrators | `group::system_admins` | Seeded. Its members belong to the platform, not to a team. |
| `service_account` | A desktop | `service_account::desktop-{desktopId}` | What the agent on that desktop signs in as, with a key only that desktop holds. The id is the task's for a desktop made for a task, and the spare's own for a spare. The key says which desktop is calling; what it may do is the grant below. |
| `service_account` | The machine preparing an environment | `service_account::desktop-{setupId}` | One for each environment, with an id made from the environment's id (`EnvironmentSetup.DesktopIdFor`), so it is never a task's or a spare's. Each attempt to prepare gives it a new key, so a machine from an earlier attempt cannot speak for the current one. |
| `agent` | The desktop operator | `agent::desktop-operator` | The background jobs that start, stop and delete desktops, keep a team's spare and prepare environments. No credential: the jobs run in the API process. |
| `agent` | The task notifier | `agent::task-notifier` | The background job that emails a task's people when the agent's turn ends. No credential. |

A team is a SqlOS organization. SqlOS keeps its name, memberships and invitations; the app mirrors each membership into the team's group (`IdentityProvisioning.cs`).

A person's command line (`cli/workstream.mjs`) is not a principal of its own. It holds a SqlOS session of the person's, made by the `workstream-cli` client, so it is the `user` with the person's grants and nothing more; what tells it apart is the scope its token carries (below).

## Resource tree: what, and under what

```text
root                          system_admin    -> group::system_admins
│                             desktop_operator -> agent::desktop-operator
│                             task_notifier   -> agent::task-notifier
└── team::{teamId}            team_member     -> group::{teamId}
    │                         team_admin      -> each administrator
    ├── environment::{id}     environment_setup -> service_account::desktop-{setupId}
    └── task::{id}            task_desktop    -> service_account::desktop-{taskId}
                              task_watcher    -> a person outside the team it is shared with, until a time or not
                              task_prompter   -> the same, who may also write to the agent
                              task_driver     -> the same, who may also use the desktop
```

A grant on a node applies to everything under it. A team's people hold one grant, on the team, so listing a team's tasks costs one index read however many tasks there are. On a single task the only grants are its own desktop's and those of the people it is shared with, each of whom holds one of the three sharing roles, with the end the sharer chose as the grant's `EffectiveTo`. A share reaches that task and nothing above or beside it.

A task's messages are rows that carry the task's node (`IHasResourceId`). They are read as authorized pages of the rows themselves.

## Permissions

Each permission is checked on a node of its own type. Creates check the parent.

| Permission | Node | Allows |
| --- | --- | --- |
| `ADMIN_VIEW` | root | The system administrator's dashboard, and changing a team's limits. |
| `TEAM_VIEW` | team | See the team, its people, its limits, and its GitHub connection with the repositories it reaches. |
| `TEAM_VIEW_USAGE` | team | See what the team used this month. |
| `TEAM_MANAGE_MEMBERS` | team | Invite, remove and change people. |
| `TEAM_MANAGE_SETTINGS` | team | Rename the team; change when its desktops are paused and archived; connect and disconnect GitHub. |
| `TEAM_CREATE_ENVIRONMENT` | team | Add an environment. |
| `TEAM_CREATE_TASK` | team | Start a task. |
| `ENVIRONMENT_VIEW` | environment | See an environment and start tasks from it. Variable values are never returned. |
| `ENVIRONMENT_EDIT` | environment | Change or delete it, including its variables, and ask for its setup to be prepared again. |
| `TASK_VIEW` | task | See the task, read its conversation and watch its desktop. |
| `TASK_PROMPT` | task | Write to the agent and interrupt it. Writing to a paused or archived task starts its desktop. |
| `TASK_DRIVE` | task | Use the desktop's mouse, keyboard and clipboard. Without it the relay drops what the browser would send them. Starting a paused, archived or failed desktop is using it, and takes this too. |
| `TASK_MANAGE` | task | Rename, pause and delete it; share it, see who it is shared with, and take a share back. |
| `TASK_RUN_AGENT` | task | The desktop's own: read its setup, exchange messages, call the model, and get a GitHub token for its environment's repository. |
| `TASK_OPERATE_DESKTOP` | task | The operator's own: start, pause, archive and delete the desktop. |
| `TEAM_OPERATE_SPARE` | team | The operator's own: make and remove the desktop kept ready for the team's next task. |
| `TASK_NOTIFY` | task | The notifier's own: read what the agent last said and email the task's people about it. |
| `ENVIRONMENT_RUN_SETUP` | environment | The preparing desktop's own: read the environment's setup, with its variables, and say how it went. |
| `ENVIRONMENT_OPERATE_SETUP` | environment | The operator's own: make the machine that prepares the environment, save its disk, and remove both. |

## Scopes

A scope is the client's ceiling, not a decision about a node: a token without a scope is refused that kind of route before anything is loaded, and a token with it still needs the permission on the node. SqlOS grants a sign-in the scopes both requested and allowed for its client.

| Scope | Allows | Clients |
| --- | --- | --- |
| `workstream.read` | Every `GET` under `/api`. Required on every token. | `workstream-app`, `workstream-cli` |
| `workstream.write` | Every other route under `/api`, and the desktop's display (`/api/tasks/{id}/desktop`), which carries the mouse and the keyboard. | `workstream-app` |
| `workstream.start` | Starting a task (`POST /api/teams/{id}/tasks`), and no other change. | `workstream-cli` |

## Roles

| Role | Granted on | To | Carries |
| --- | --- | --- | --- |
| `system_admin` | root | `group::system_admins` | `ADMIN_VIEW`: the dashboard's numbers and every team's limits. Nothing inside a team. |
| `desktop_operator` | root | `agent::desktop-operator` | `TASK_OPERATE_DESKTOP`, `TEAM_OPERATE_SPARE`, `ENVIRONMENT_OPERATE_SETUP` |
| `task_notifier` | root | `agent::task-notifier` | `TASK_NOTIFY` |
| `team_member` | team | the team's group | `TEAM_VIEW`, `TEAM_VIEW_USAGE`, `TEAM_CREATE_ENVIRONMENT`, `TEAM_CREATE_TASK`, `ENVIRONMENT_VIEW`, `ENVIRONMENT_EDIT`, `TASK_VIEW`, `TASK_PROMPT`, `TASK_DRIVE`, `TASK_MANAGE` |
| `team_admin` | team | whoever created the team, and anyone an administrator promotes | `TEAM_MANAGE_MEMBERS`, `TEAM_MANAGE_SETTINGS` |
| `task_desktop` | task | the desktop that works on the task | `TASK_RUN_AGENT` |
| `environment_setup` | environment | the environment's preparing account | `ENVIRONMENT_RUN_SETUP` |
| `task_watcher` | task | a person outside the team the task is shared with | `TASK_VIEW` |
| `task_prompter` | task | the same | `TASK_VIEW`, `TASK_PROMPT` |
| `task_driver` | task | the same | `TASK_VIEW`, `TASK_PROMPT`, `TASK_DRIVE` |

Everyone on a team can see, use and manage every task and environment in it, and share any of its tasks. Work a person wants to keep to themselves goes in their own team. Someone a task is shared with gets that task alone, never `TASK_MANAGE`: they cannot pause, delete or share it, and see nothing else of the team. Bringing a paused desktop back is not managing the task but using it: whoever may write to its agent does it by writing, and whoever may use its desktop by clicking it.

## Grants: what makes and removes them

| Event | Grant |
| --- | --- |
| A person signs up | SqlOS organization and membership are created; the team's group gets `team_member` on the team; the person gets `team_admin`. |
| A person creates another team | The same, for the new team. |
| A person accepts an invitation | Accepting is signing in, or creating an account, with the invitation's token attached (SqlOS's own flow; the link proves the mailbox). Their membership puts them in the team's group. An invitation as an administrator also grants `team_admin`. |
| An administrator promotes or demotes someone | `team_admin` is granted or revoked. The last administrator cannot be demoted or removed. |
| A person is removed or leaves | Their membership is deleted; they leave the group; their `team_admin` on the team is removed. |
| A desktop is made for a task | The desktop's service account is provisioned and granted `task_desktop` on the task. |
| A spare is made | Its service account is provisioned and granted nothing. |
| A task starts on a spare | The spare's service account is granted `task_desktop` on the task. Nothing else changes hands: the machine keeps the key it has. |
| A spare is removed unused | Its service account is expired. It never held a grant. |
| Someone on the team shares a task with a person who has an account | The person gets `task_watcher`, `task_prompter` or `task_driver` on the task, with `EffectiveTo` an hour, a day or a week from now, or none. It replaces any of the three they held on it: a person holds one. The account's address must be proved (an emailed code, or a link sent to it); an account whose address nobody proved is treated as someone without one. |
| Someone on the team shares a task with someone who has no account | Nothing is granted yet. A `TaskInvitations` row keeps the address, the role, the end and the hash of the link's token. |
| That person takes the link | Signed in with the address it was sent to (a new account is an ordinary sign-up, with a team of their own), they get the role on the task with the end the sharer chose, and the row is deleted. |
| Someone on the team takes a share back, or withdraws an invitation | The person's sharing grant on the task is removed, or the row is deleted. |
| A share's time is up | Nothing runs: SqlOS stops counting the grant at its `EffectiveTo`, in every check and every page. Sharing again sets a new end on the same grant. |
| A task's desktop is archived | Every desktop account that holds `task_desktop` on the task is expired: its machine is gone. The grants stay, and open nothing while the accounts are expired. |
| An archived desktop is brought back | The task's own desktop account is provisioned, or given a new key if it was there, and granted `task_desktop` on the task, as when a desktop is first made. |
| A task is deleted | Every desktop account that holds `task_desktop` on the task is expired, its invitations and sharing grants are removed, and the task's node is removed, with the grants on it. |
| An environment is prepared | Its preparing account is provisioned the first time and granted `environment_setup` on the environment, once; each attempt issues it a new key. |
| An attempt to prepare ends (saved, failed, too long) | The preparing account is expired. Its grant stays, and opens nothing while the account is expired. |
| An environment is deleted | Its preparing account is expired, and the environment's node is removed, with the grant on it. |

Grants are derived from these events. Nothing revokes "the grant this action made".

## Lists

| List | Page over | Permission | Order |
| --- | --- | --- | --- |
| My teams | `Teams` | `TEAM_VIEW` | `CreatedAt` |
| A team's tasks | `Tasks` in the team | `TASK_VIEW` | newest first (`CreatedAt`) |
| Shared with me | `Tasks` in every team but the person's own | `TASK_VIEW` | newest first (`CreatedAt`) |
| A team's environments | `Environments` in the team | `ENVIRONMENT_VIEW` | `Name` |
| A task's messages | `TaskMessages` of the task, after an id | `TASK_VIEW` | `Id` |
| Desktops the operator sweeps | `Tasks` | `TASK_OPERATE_DESKTOP` | `Id` |
| The task a desktop works on | `Tasks` | `TASK_RUN_AGENT`, as the desktop | `Id`, first one |
| The environment a desktop prepares | `Environments` | `ENVIRONMENT_RUN_SETUP`, as the desktop | `Id`, first one |

"Shared with me" leaves out the teams the person is on (their SqlOS memberships), so what the page finds is what a task grant reaches. The filter is applied under each of the person's nodes, so the page also reads past their own teams' tasks under their team grants.

A team's people and its pending invitations are SqlOS's own rows, read after one `TEAM_VIEW` check on the team. Who a task is shared with is SqlOS's grants on the task's node with the three sharing roles whose `EffectiveTo` has not passed, and its waiting invitations are `TaskInvitations` rows, both read after one `TASK_MANAGE` check on the task. Usage and the administrator's numbers are sums, read after one check (`TEAM_VIEW_USAGE` on the team, `ADMIN_VIEW` on root); SqlOS has no authorized count.

## App data, and why SqlOS does not hold it

| Table | Holds | Why it is app data |
| --- | --- | --- |
| `Teams` | The node, the month's model allowance, the limit on running desktops, and when the team's desktops are paused (minutes without use) and archived (days paused). | Limits and settings are numbers, not access. |
| `Environments` | Name, apps, size, repository, setup script, instructions, protected variables, and `SetupInputs`: a SHA-256 of the repository, branch, script, variables and apps, written where the environment is written, because that is the one place the variables are readable. | Configuration. |
| `EnvironmentSetups` | An environment's saved setup (the snapshot's name, the inputs and image version it was made for, its commit, its size, when it was made and last used) and the attempt to prepare it in progress or how the last one failed. One row an environment, deleted with it. | The operator's stock and a lifecycle, like `Spares`: nobody is granted on it, and seeing it is seeing the environment. |
| `Tasks` | Title, environment, who created it (a principal id, a fact), the desktop's state and address, when it was last used, how much is on its disk, its archive's name and size (`Archive`, `ArchiveBytes`) while it has one, when it last emailed its people. | The desktop's lifecycle. |
| `TaskMessages` | The conversation. | Content. |
| `TaskInvitations` | A task shared with an address that has no account yet: the address, the role and end it will be granted with, who shared it, the hash of the link's token, and when the link stops working (a week, or sooner if the access ends sooner). | There is no principal to grant yet. SqlOS's invitations are memberships of an organization, and this person is not joining the team, so the grant waits here until they take the link; then it is a grant and the row is gone. If SqlOS gains an invitation to a role on a node, this table goes. |
| `DesktopRuns` | When each machine ran, what for (a task's desktop, a spare, a machine preparing an environment), what a task's start began from (the image, a saved setup, a spare, its own paused machine, its archive), and when each step of it happened. | Usage, cost, and how long starts take. |
| `Spares` | A team's spare: the machine, its size, when someone last wanted it. | The operator's stock, which nobody is granted on. It relates a team to a machine, not a principal to a node. |
| `ModelUsage` | Tokens and cost of each model call. | Usage. |
| `PersonSettings` | A person's own settings, one row by their user id: whether task emails reach them. No row is every default. | A preference, not access. It relates the person to nothing in the tree; it is read and changed only by the person it belongs to, found by the id in their token, so there is nothing to check. |
| `TaskWatchers` | When each person last had a task's desktop on screen, one row a person a task. Removed with the task. | A fact about what someone did, like `Tasks.CreatedBy`, and never a permission: whether they may watch is `TASK_VIEW`. It is read only to keep from emailing someone about the task they are looking at. |
| `GitHubConnections` | A team's GitHub connection: the installation's id, the GitHub account it is on, who connected it (a principal id, a fact) and when. One a team at most. | Configuration of the team, like its limits. It relates a team to an account on GitHub, which is not a principal, and grants nobody anything: who may change it is `TEAM_MANAGE_SETTINGS` on the team, and who may use it is a desktop holding `TASK_RUN_AGENT` on a task in the team. A table of its own rather than columns on `Teams`, because GitHub names an installation when it is removed and that lookup needs an index, which on the protected `Teams` table SqlOS would copy under every ancestor. |

`Tasks.CreatedBy` records who started a task. It grants nothing.

## Flows

### Sign up and sign in

The app draws sign-in itself against SqlOS's headless endpoints: an emailed code in production, or a password where `Workstream:Auth:PasswordSignIn` is on (development and tests). A new person gets a team named after them, a default environment, and `team_admin` on the team.

An invitation's link carries a token. Signing in, or creating an account, with the token attached is what accepts it: the link proves the mailbox, so someone new only gives their name. They land in the team that invited them, and someone who created their account this way gets no team of their own.

When a person is removed from a team, or leaves it, SqlOS ends the session that was for that team. They sign in again and see the teams they are still on.

The system administrator is the one address the deployment names (`Workstream:Administrator:Email`). At every start the API makes that person the only member of `group::system_admins`; a changed address moves the role, and no address leaves the group empty.

### Start a task

1. The person picks a team and an environment and writes what they want done.
2. The API checks `TEAM_CREATE_TASK` on the team and `ENVIRONMENT_VIEW` on the environment, and refuses if the team already runs as many desktops as its limit allows.
3. It saves the task (desktop state `starting`) and the first message, and queues the start job.
4. The job, as `agent::desktop-operator`, provisions the desktop's service account, grants it `task_desktop`, and asks the desktop host for a machine carrying two values: the API's address and the desktop's key.
5. The desktop boots. Its display answers first, and from then the team can watch it; its agent's first exchange with the API marks the desktop `ready`. The agent reads its setup from the API with its key and prepares the desktop.

### A spare desktop

Making a machine takes Azure from fifteen seconds to a minute, and nothing Workstream does shortens that. So while someone is in a team's workspace, the operator keeps one desktop running for the task they may be about to start.

1. The web app says someone is there when the team's pages open and when the new-task dialog opens (`POST /api/teams/{id}/spare`). The check is `TEAM_CREATE_TASK` on the team: whoever may start a task may have one warmed up.
2. If the team has no spare and the limits allow, the operator (`TEAM_OPERATE_SPARE` on the team) makes one for the environment the team used last. Its service account is granted nothing. Its agent asks the API twice a second what it is for, and is told there is nothing yet.
3. A task started from an environment of the same size takes the spare in the same transaction that makes the task: the spare's row is deleted, the task records its machine, and the spare's account is granted `task_desktop` on the task. The screen can be watched at once. The agent's next question is answered with the task's setup.
4. The operator then makes the team's next spare. A spare nobody on the team has wanted for fifteen minutes is removed with its machine and its key.

The limits are settings (`Workstream:Desktops:Spares`): one spare a team, so many running at once over every team, so many hours of spare time a day, and none for a team already at its limit of running desktops. A spare is never given to another team.

### Prepare an environment

Cloning a repository and running a setup script can take minutes on every new desktop. An environment with a repository or a setup script is prepared instead: the setup is done once, on a machine of its own, and that machine's disk is saved for the environment's tasks to start from.

1. Something asks for it: the environment is added with a setup, or its setup inputs change; someone presses "Prepare again" (`POST /api/environments/{id}/setup`, `ENVIRONMENT_EDIT`); or a task starts and finds no save it can use (it is not asked again for settings whose last attempt failed), or finds one older than a week (the task uses the old one, and the new one is made behind it).
2. The operator (`ENVIRONMENT_OPERATE_SETUP` on the environment) makes a machine when the limits allow (`Workstream:Desktops:Saves`: one preparing at once, four hours of preparing a day, 45 minutes at most each). The machine is told the API's address and a new key for the environment's preparing account, which holds `environment_setup` on the environment and nothing else. Its time is a `DesktopRun` with the purpose `prepare`.
3. The machine's agent reads its setup (`GET /api/desktop/setup`, found by its page of environments with `ENVIRONMENT_RUN_SETUP`), which says it is preparing and carries no task and no model. It clones the repository, runs the setup script, and says how it is going (`POST /api/desktop/setup`). Before it says it is done it removes everything of the preparing from the disk: the variables, the logs and the browser's profile; git keeps no token, since it asks for one each time (a preparing desktop gets one for its environment's repository from the team's GitHub connection while its attempt lasts). It leaves one file saying when and at which commit it was prepared.
4. Only when the script exits 0, the operator stops the machine, saves its disk as a snapshot, removes the machine, ends the key, and removes the save it replaces: an environment keeps one. A failed attempt keeps nothing, and its machine is removed.
5. A save is used only for the setup inputs it was made from and the desktops' image version it was made on (`Workstream:Desktops:Azure:ImageId`). A task whose environment has such a save gets a machine made from it; its run's source is `save`, its agent pulls the branch's newer commits and does not run the setup script, and its first line says where it started from. Any other task starts from the image and does the setup itself, as before.

A save nothing has started from for 30 days is deleted. Deleting the environment deletes its save, and ends a preparing machine's key. The sweep deletes any snapshot no environment knows and any machine no task and no attempt knows.

### The command line

A person's own computer, a local Claude Code for instance, starts tasks with `cli/workstream.mjs`, as the person.

1. `workstream login --api <the API>` reads `GET /cli` (public: the issuer, the `workstream-cli` client, its scopes, the token's audience and the web app's address) and checks that the issuer and the audience are that API's own. It asks SqlOS for a device code (`/sqlos/auth/device_authorization`) and prints the link and the code.
2. The link opens SqlOS, which hands the browser to the web app's device page (`/device?request=…`, `client/app/device.tsx`). Signed out, the person signs in there with the same form as the sign-in page, inside the device request. The page shows the code and what the command line may do; the person allows it or not.
3. The command line's next poll of `/sqlos/auth/token` gets an access token and a refresh token for a new session of the person's, with `workstream.read` and `workstream.start`. They are kept in `~/.config/workstream/`, readable by that user alone, and refreshed there.
4. `workstream start` lists the person's teams and the team's environments (reads), starts the task (`TEAM_CREATE_TASK` on the team, `ENVIRONMENT_VIEW` on the environment, as from the web app), prints the task's link in the web app and follows the task until its desktop is ready. `CreatedBy` is the person.
5. `workstream logout` ends the session at SqlOS (`/sqlos/auth/logout`), so its tokens stop working everywhere at once. Like any of the person's sessions, it also ends when it goes unused past SqlOS's idle limit, and when the person leaves the team it was approved for.

The device page sends SqlOS's session cookie with its requests (the app's own sign-in does not), because approving a device needs SqlOS to know who is approving. The command line's tokens never reach the browser.

### Talk to the agent

A person's message is a row in `TaskMessages`. The desktop exchanges with the API about once a second: it sends what the agent said and did, and receives what people wrote since its last exchange. The agent's messages are rows written as the desktop's service account. Every model call the agent makes goes to the API's model gateway with the desktop's key; the gateway checks `TASK_RUN_AGENT`, checks the team's allowance, forwards the call to Foundry and records its usage.

### Watch and use the desktop

The browser opens a WebSocket to `GET /api/tasks/{id}/desktop`, carrying the person's access token. The API validates the token, which must carry `workstream.write` (the command line's cannot open a desktop), checks `TASK_VIEW` on the task, connects to the desktop's display on its private address, and relays the display's bytes to the browser, from the moment the display answers until the desktop is asked to stop. What the browser sends is read as the remote framebuffer protocol's client messages (`Desktops/RfbClientFilter.cs`). For someone without `TASK_DRIVE` it passes what only asks for the picture (pixel format, encodings, update requests, continuous updates, fences, the desktop's size) and drops key events, pointer events and the clipboard, and QEMU's key events and xvp's power actions; a stream it cannot read is closed. The browser draws a watcher's screen with noVNC's `viewOnly`. It checks both again every minute: the stream closes when `TASK_VIEW` is gone, and the mouse and keyboard follow `TASK_DRIVE`. Desktops accept connections only from the API.

Watching is not using. An open stream marks nothing on the task but who has it on screen (`TaskWatchers`, for the emails below), and the browser closes the stream while its tab is hidden. A key or pointer event that reaches the display is use: the filter notices it and the relay writes it on the task (`Tasks.ActiveAt`), at once and then at most once a minute.

### Share a task

1. Someone on the team (`TASK_MANAGE` on the task) gives an address, what the person may do (watch; watch and write to the agent; or use the desktop too) and for how long (an hour, a day, a week, or until it is taken back).
2. An account whose address is proved gets the role on the task at once. The answer carries a link to the task, which is emailed to them where email is set up (`task.shared`, one of the templates the API writes at every start, its words in `client/locales/emails/en.json`).
3. Anyone else gets an invitation: a link to `/share?token=…`, emailed where email is set up (`task.shared-invite`), which works for a week, or until the access would end if that is sooner. They create their account the ordinary way, with a team of their own, and the page takes the link signed in: the token proves the mailbox, the address says whose it is, and only that address can take it. Then they have the role on the task with the end the sharer chose.
4. The task is in their "Shared with me" list and opens in their own workspace beside their team's tasks. A watcher reads the conversation and watches the screen; nothing they send reaches the agent or the desktop.
5. The team sees who has the task in its Share dialog, and takes a share back or withdraws an invitation there. An ended or taken-back share stops at the next request, and an open stream within a minute.

### Hear when a task is finished or needs you

When the agent's turn ends (the desktop's exchange says it was working and now is not), the exchange queues a job that runs fifteen seconds later as `agent::task-notifier`, which checks `TASK_NOTIFY` on the task. The job writes to nobody when the agent is working again, when the conversation's last message is not the agent's words (someone answered, or the agent's last line was about something it did), or when the task already emailed in the last ten minutes. Otherwise it emails "finished", or "needs you" when the agent's last words end with a question, to the person who started the task and everyone who wrote in it, each of whom must:

- still hold `TASK_VIEW` on the task, so someone who left the team hears nothing more of it;
- not have turned these emails off (`PersonSettings`, at `GET` and `PUT /api/me/settings`, a switch on the team's page);
- not have had the task's desktop on screen in the last two minutes (`TaskWatchers`, written by the relay when it lets them in and every minute after).

The email is one of two SqlOS templates the API writes at every start (`task.finished`, `task.needs-you`), its words from `client/locales/emails/en.json`, and it links to the task in the team's workspace, or, for someone it is shared with, in their own. Each person gets it once a turn, however often the job runs (SqlOS's idempotency key). Where email is not set up (development, staging), the job sends nothing.

### Pause, archive, delete

A desktop is in one of three states that last, and only a person ends a task.

| State | What it is | What is kept | How it ends |
| --- | --- | --- | --- |
| Running | Someone is using it, or its agent is working. | The machine and its disk. | Paused by a person (`TASK_MANAGE`), or by the operator after the team's pause time without use. |
| Paused (`stopped`) | The machine is released. | The disk. | Resumed in about half a minute; archived by the operator after the team's archive time. |
| Archived | The disk is copied to an archive, then the machine and the disk are deleted. | The archive. | Brought back on a new machine made from the archive, in about a minute. |

**Use** is what keeps a desktop running, and it is three things: its agent working (the desktop's exchange says so, and the idle time counts from the moment it stops), a person's message in the conversation, and a person's key or pointer event reaching the display through the relay. Having the task open is not use: an open pane, a stream, and reading the conversation mark nothing. Every five minutes the operator's sweep (`TASK_OPERATE_DESKTOP`, as a page of the tasks) pauses each ready desktop whose `ActiveAt` is older than its team's pause time.

**The team's times** are two numbers on `Teams`: `PauseAfterMinutes` (5 to 240, 30 for a new team) and `ArchiveAfterDays` (1 to 90, 7 for a new team). Everyone on the team sees them (`TEAM_VIEW`); a team administrator changes them (`PUT /api/teams/{id}/desktop-times`, `TEAM_MANAGE_SETTINGS`), on the team's page. A deployment sets what a new team starts with (`Workstream:Desktops:IdleMinutes`, `ArchiveDays`).

**Resuming** is starting the desktop again, within the team's limit of running desktops. A person does it by clicking the paused pane (`POST /api/tasks/{id}/start`, `TASK_DRIVE`) or by writing to the agent (`POST /api/tasks/{id}/messages`, `TASK_PROMPT`): the message is saved, the desktop starts, and the agent reads the message when it is up, because its exchange asks for what was written after the last message it read, which it keeps on its disk. When the team is at its limit neither starts anything, the message is not saved, and the answer says to pause one. Opening the task, reading it and trying its stream resume nothing. A paused desktop comes back on its own machine and disk, and its agent starts again what it had left running and reopens what was open (architecture.md, "After a stop").

**Archiving** happens in the sweep, for a desktop paused longer than its team's archive time. The operator copies the disk to an archive (`IDesktopHost.ArchiveAsync`: a snapshot on Azure), writes the archive's name and size on the task, and only then deletes the machine and its disk, so the task's files always have one owner. A copy that cannot be made leaves the desktop paused and is tried again at the next sweep. An archived task keeps its row, its node, its conversation and every grant; nothing about who may do what changes. Its desktop's key is ended when the machine is deleted. Bringing it back makes a new machine from the archive, as the task's own service account (`service_account::desktop-{taskId}`, granted `task_desktop` on the task) with a new key, and the archive is deleted once that machine has shown its screen. A start that fails before then removes the half-made machine and keeps the archive.

**Deleting** is a person's alone (`TASK_MANAGE`): it deletes the machine, its disk, the archive if there is one, the conversation and the node. Nothing deletes a task by itself, however long it has been archived. The sweep deletes only what no task owns: a machine, or an archive, that no task names once it has been there half an hour.

### Connect GitHub

A team connects GitHub so its desktops can clone private repositories, push branches and open pull requests, without anyone pasting a personal token. It is a GitHub App, Workstream's own, installed on the team's GitHub account or organization; the app's settings (`Workstream:GitHub`) are empty until the owner makes it (infra/README.md, "GitHub"), and the team's page then says GitHub is not set up.

1. An administrator presses Connect GitHub on the team's page. The API checks `TEAM_MANAGE_SETTINGS` on the team and answers with the app's installation address on GitHub, carrying a state it signed: the team and the person, good for an hour (`POST /api/teams/{id}/github/install`).
2. On GitHub they pick the account and the repositories, install, and authorize the app. GitHub sends the browser to the API's callback (`/api/github/callback`) with the state, the installation's id and a code for the person who installed it. The callback decides nothing, because the browser carries no person's token there: it hands all of it to the web app's `/github` page.
3. The web app posts it as the person signed in there (`POST /api/github/connect`). The API checks that the state is one it signed for this same person, that they still hold `TEAM_MANAGE_SETTINGS` on the team, and that GitHub, asked with the person's own GitHub token from the code, lists that installation among the ones their account reaches. The installation id in the address proves nothing by itself: anyone can type another account's. The person's GitHub token answers that one question and is kept nowhere.
4. The team's row in `GitHubConnections` is written. Connecting again replaces it.

Everyone on the team sees the connection and the repositories it reaches (`TEAM_VIEW`), read from GitHub as the installation each time; an environment's repository can be picked from them. An administrator disconnects it (`TEAM_MANAGE_SETTINGS`), which deletes the row: the app stays installed on GitHub, where whoever owns the account removes it, because another team may be connected to the same installation. When the app is removed on GitHub, its webhook (where a secret is set) disconnects every team that was connected to that installation.

### A desktop's GitHub token

The desktop asks the API for a token with its key (`GET /api/desktop/github/token`). The API finds the task the desktop holds `TASK_RUN_AGENT` on, the repository of the task's environment, and the team's connection. When the repository is on github.com, on the connection's account, the API asks GitHub, as the app, for an installation token for that one repository with three permissions: contents and pull requests to write, metadata to read. GitHub makes it good for one hour. It is handed straight to the desktop and stored nowhere; the desktop asks again whenever git or gh needs one. Anything else (no repository, another account's repository, no connection, a spare, the desktop of a deleted task) gets no token.

The token goes with the desktop, not with a person. Someone a task is shared with who may write to its agent (`task_prompter`) or use its desktop (`task_driver`) can have work pushed to that one repository, as the team's people can, while the share lasts; a `task_watcher` only watches. Sharing a task whose environment has a repository is sharing that much.

On the desktop, git's credential helper and the wrapper around gh ask for the token (`desktop/agent/github.mjs`), so cloning, pushing and `gh pr create` work. A `GITHUB_TOKEN` or `GH_TOKEN` an environment's variables already hold is used as before, without asking. The agent is told to finish project work on a branch of its own and a pull request, never on the default branch.

### Usage and limits

A team has a model allowance for the calendar month and a limit on desktops running at once; a paused or archived desktop is not running, and resuming one counts against the limit like starting one. People on the team see what was used. A system administrator sees every running desktop and can change a team's limits. Payments are not built; the numbers that a bill would be made from are.

## Decisions

- **Team is the only container.** One grant per person per team keeps every list cheap and the rules short. Private work goes in a team of the person's own.
- **The desktop is a principal, and its task is a grant.** The agent's key identifies one service account. The account holds `task_desktop` on the one task it works on, so a key taken from a desktop reaches that task and nothing else, and stops working when the task is deleted. The key carries no task: the API finds the task by the grant, which is why a spare can become a task's desktop without being told anything new.
- **Sharing is additive, a grant on the task.** The team keeps its one grant on the team and sees every task; a person outside it is given one task, by a role on that task's node, with SqlOS's `EffectiveTo` as the end. Nothing a share grants reaches the team node, so the team's lists cost what they did.
- **The API is the only way into a desktop.** Desktops have no inbound access from the internet or from each other. The relay authorizes every stream.
- **Having a task open is not using it.** A pane left open in a browser tab once kept a desktop running for as long as the tab lived, because the relay marked the task active every time it re-admitted the stream. Use is now only what a person or the agent does: input that reaches the display, a message, the agent working. The cost of a desktop follows the work, not the tab.
- **Paused keeps the name `stopped`.** `Tasks.State` is stored as its name, and the rows, the API and the command line already say `stopping` and `stopped`. Renaming them would need every stored row rewritten at the moment of a deployment, for no change in what happens to the machine. So the state keeps its name and people read Paused; `archiving` and `archived` are new names beside it.
- **The pause and archive times are the team's, not a role's.** They are two numbers on the team's row, changed with the permission that already covers the team's settings (`TEAM_MANAGE_SETTINGS`, which `team_admin` carries). No role, no grant and no permission was added for them.
- **Resuming is use, not management.** Stopping was `TASK_MANAGE` and so was starting. Now that a desktop pauses by itself, bringing it back is part of using the task: someone it is shared with to write to its agent or to use its desktop would otherwise find it paused after half an hour and be unable to do what they were given. So a message resumes (`TASK_PROMPT`) and a click resumes (`TASK_DRIVE`); pausing and deleting stay the team's (`TASK_MANAGE`).
- **A GitHub connection is a row, and its tokens are minted, not kept.** Which installation a team uses is configuration, not access: nobody is granted on it. A desktop's token is made for one repository, for an hour, at the moment it is asked for, so nothing in the database or on a desktop's disk is worth stealing, and a stolen token is worth one repository for less than an hour.
- **An installation is the person's to name only if GitHub says their account reaches it.** The installation id GitHub puts in the address can be changed by anyone, so the connection is made only after the person's own GitHub account is asked which installations it can reach, and only by the signed-in person the state was signed for.
- **All inference goes through the gateway.** The Foundry key never leaves the API. Usage is recorded where it is spent.
- **The command line signs in through the browser with SqlOS's device flow, not with an API key and not with a loopback redirect.** SqlOS has a client for exactly this (`SeedCliClient`: public, no secret, the device and refresh grants, an allow-list of scopes and one audience), and its headless flow has the device steps (`/headless/device/resolve`, `approve`, `deny`), so the approval is one more page of the web app over the sign-in it already has. The token is the person's own session, so the command line has the person's grants as they are now, and loses them when the person does. An API key in SqlOS is a machine client bound to a service account, a principal of its own: to act as the person it would need grants copied from theirs on every team and kept in step with their memberships, a stored copy of access that the rules forbid. Authorization code with PKCE on a loopback address would also work in SqlOS, but it needs a free local port and a browser on the same machine, and the web app's sign-in would have to finish another client's request; the device flow needs neither, so it also works over SSH, in a container or on a dev VM. A scope of its own (`workstream.start`) keeps the command line to reading and starting tasks: a token taken from a laptop cannot stop, delete, rename or write to a task, change a team, or open a desktop.
- **The agent is Workstream's own, on the Claude Agent SDK.** Anthropic's terms for hosting Claude Code itself require each end user to bring their own credential; products built on the Agent SDK use the developer's API key or cloud provider. Claude Code is not wired to Workstream's inference.

## Not built yet

Payments, API keys for integrations, single sign-on, a private task inside a shared team, Windows desktops, a list in the web app of the command lines a person has signed in, to sign one out from there, a saved setup for a spare.
