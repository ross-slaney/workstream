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
| `agent` | The desktop operator | `agent::desktop-operator` | The background jobs that start, stop and delete desktops, keep a team's spare, prepare environments, and write down what each team keeps on storage. No credential: the jobs run in the API process. |
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
| `TEAM_VIEW_USAGE` | team | See what the team used this month: models, desktop hours by size, storage by kind. |
| `TEAM_MANAGE_MEMBERS` | team | Invite, remove and change people. |
| `TEAM_MANAGE_SETTINGS` | team | Rename the team; connect and disconnect GitHub. |
| `TEAM_CREATE_ENVIRONMENT` | team | Add an environment. |
| `TEAM_CREATE_TASK` | team | Start a task. |
| `ENVIRONMENT_VIEW` | environment | See an environment and start tasks from it. Variable values are never returned. |
| `ENVIRONMENT_EDIT` | environment | Change or delete it, including its variables, and ask for its setup to be prepared again. |
| `TASK_VIEW` | task | See the task, read its conversation and watch its desktop. |
| `TASK_PROMPT` | task | Write to the agent and interrupt it. |
| `TASK_DRIVE` | task | Use the desktop's mouse, keyboard and clipboard. Without it the relay drops what the browser would send them. |
| `TASK_MANAGE` | task | Rename, stop, start and delete it; share it, see who it is shared with, and take a share back. |
| `TASK_RUN_AGENT` | task | The desktop's own: read its setup, exchange messages, call the model, and get a GitHub token for its environment's repository. |
| `TASK_OPERATE_DESKTOP` | task | The operator's own: start, stop and delete the desktop, and count the disk or archive the task keeps. |
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

Everyone on a team can see, use and manage every task and environment in it, and share any of its tasks. Work a person wants to keep to themselves goes in their own team. Someone a task is shared with gets that task alone, never `TASK_MANAGE`: they cannot stop, delete or share it, and see nothing else of the team.

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
| What the tasks keep, for the storage job | `Tasks` that have a machine or an archive | `TASK_OPERATE_DESKTOP`, as the operator | `Id` |
| The task a desktop works on | `Tasks` | `TASK_RUN_AGENT`, as the desktop | `Id`, first one |
| The environment a desktop prepares | `Environments` | `ENVIRONMENT_RUN_SETUP`, as the desktop | `Id`, first one |

"Shared with me" leaves out the teams the person is on (their SqlOS memberships), so what the page finds is what a task grant reaches. The filter is applied under each of the person's nodes, so the page also reads past their own teams' tasks under their team grants.

A team's people and its pending invitations are SqlOS's own rows, read after one `TEAM_VIEW` check on the team. Who a task is shared with is SqlOS's grants on the task's node with the three sharing roles whose `EffectiveTo` has not passed, and its waiting invitations are `TaskInvitations` rows, both read after one `TASK_MANAGE` check on the task. Usage and the administrator's numbers are sums over `ModelUsage`, `DesktopRuns` and `StorageRuns`, read after one check (`TEAM_VIEW_USAGE` on the team, `ADMIN_VIEW` on root); SqlOS has no authorized count.

## App data, and why SqlOS does not hold it

| Table | Holds | Why it is app data |
| --- | --- | --- |
| `Teams` | The node, the month's model allowance, the limit on running desktops. | Limits are numbers, not access. |
| `Environments` | Name, apps, size, repository, setup script, instructions, protected variables, and `SetupInputs`: a SHA-256 of the repository, branch, script, variables and apps, written where the environment is written, because that is the one place the variables are readable. | Configuration. |
| `EnvironmentSetups` | An environment's saved setup (the snapshot's name, the inputs and image version it was made for, its commit, its size, when it was made and last used) and the attempt to prepare it in progress or how the last one failed. One row an environment, deleted with it. | The operator's stock and a lifecycle, like `Spares`: nobody is granted on it, and seeing it is seeing the environment. |
| `Tasks` | Title, environment, who created it (a principal id, a fact), the desktop's state and address, when it last emailed its people. | The desktop's lifecycle. |
| `TaskMessages` | The conversation. | Content. |
| `TaskInvitations` | A task shared with an address that has no account yet: the address, the role and end it will be granted with, who shared it, the hash of the link's token, and when the link stops working (a week, or sooner if the access ends sooner). | There is no principal to grant yet. SqlOS's invitations are memberships of an organization, and this person is not joining the team, so the grant waits here until they take the link; then it is a grant and the row is gone. If SqlOS gains an invitation to a role on a node, this table goes. |
| `DesktopRuns` | When each machine ran, at which size, what for (a task's desktop, a spare, a machine preparing an environment), what a task's start began from (the image, a saved setup, a spare, its own stopped machine), and when each step of it happened. | Usage, cost, and how long starts take. |
| `StorageRuns` | Each stretch of a team keeping something at one size: the kind (`disk`, `archive`, `setup`), the task or environment it belongs to, the gigabytes, and when it began and ended. | Usage and cost, like `DesktopRuns`. It relates a team to an amount, not a principal to a node, and is derived: a job makes the open rows agree with what the tasks and environments say is kept, so nothing else writes them. |
| `Spares` | A team's spare: the machine, its size, when someone last wanted it. | The operator's stock, which nobody is granted on. It relates a team to a machine, not a principal to a node. |
| `ModelUsage` | Tokens and cost of each model call. | Usage. |
| `PersonSettings` | A person's own settings, one row by their user id: whether task emails reach them. No row is every default. | A preference, not access. It relates the person to nothing in the tree; it is read and changed only by the person it belongs to, found by the id in their token, so there is nothing to check. |
| `TaskWatchers` | When each person last had a task's desktop on screen, one row a person a task. Removed with the task. | A fact about what someone did, like `Tasks.CreatedBy`, and never a permission: whether they may watch is `TASK_VIEW`. It is read only to keep from emailing someone about the task they are looking at. |
| `GitHubConnections` | A team's GitHub connection: the installation's id, the GitHub account it is on, who connected it (a principal id, a fact) and when. One a team at most. | Configuration of the team, like its limits. It relates a team to an account on GitHub, which is not a principal, and grants nobody anything: who may change it is `TEAM_MANAGE_SETTINGS` on the team, and who may use it is a desktop holding `TASK_RUN_AGENT` on a task in the team. A table of its own rather than columns on `Teams`, because GitHub names an installation when it is removed and that lookup needs an index, which on the protected `Teams` table SqlOS would copy under every ancestor. |

`Tasks.CreatedBy` records who started a task. It grants nothing.

## Flows

### Sign up and sign in

The app draws sign-in itself against SqlOS's headless endpoints: an emailed code in production, or a password where `Workstream:Auth:PasswordSignIn` is on (development and tests). A new person gets a team named after them, a default environment, and `team_admin` on the team.

Where the deployment has a GitHub OAuth client (`SqlOS:Oidc:GitHub:ClientId` and `ClientSecret`; infra/README.md, "Sign in with GitHub"), the form also shows Continue with GitHub, on the sign-in page and on the command line's page. It is SqlOS's own GitHub connection, seeded in code (`api/Workstream.cs`, `SeedGitHubSignIn`); the app adds no table, no flow and no rule of its own:

1. SqlOS lists GitHub with the sign-in flow, and the form shows the button. Without the two settings it lists nothing and the form has no button.
2. Pressing it asks SqlOS where to go (`/headless/provider/start`) and the browser leaves for GitHub, which asks the person for their profile and their email addresses (`read:user`, `user:email`) and nothing of their repositories.
3. GitHub sends the browser to SqlOS's callback on the API (`/sqlos/auth/oidc/callback`). SqlOS reads the GitHub account's number and its verified primary address. An account it has seen before is that person. Otherwise an address that already has an account is that person, and the GitHub account is attached to them. Otherwise it is someone new, who gets what a new person gets. A GitHub account with no verified primary address is refused.
4. SqlOS sends the browser to the web app's `/auth/callback` with a code, which the page exchanges with what the sign-in page kept for the trip (its `state` and PKCE verifier, in the tab's storage). On the command line's page SqlOS hands the browser back to that page, signed in, to allow the command line or not.

Someone GitHub did not sign in comes back to the form, which says so and still takes their email. That includes a person on several teams: SqlOS asks which team the session is for, and after a trip to a provider SqlOS 11.4.1 does not tell a page of the app's own which teams there are, so the page cannot answer as it does for an emailed code. They sign in with their email. An invitation is for one address, so its form shows no GitHub button.

Signing in with GitHub says who a person is and nothing more. SqlOS does not keep the token GitHub gave it, or hand it to the app, so a desktop's repository access still comes from the team's GitHub connection ("Connect GitHub" below).

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

### Stop, start, delete

Stopping releases the machine but keeps its disk; starting brings the same desktop back, and its agent starts again what it had left running and reopens what was open (architecture.md, "The agent"). A desktop nobody has watched or written to for a while, whose agent is idle, is stopped by the operator. Deleting a task deletes the machine, the conversation and the node.

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

A team has a model allowance for the calendar month and a limit on desktops running at once. People on the team see what was used this month (`GET /api/teams/{id}/usage`, `TEAM_VIEW_USAGE`), in the units a bill would use:

- **Models**: dollars spent against the allowance, from `ModelUsage`.
- **Desktop time**: hours for each size the team's tasks ran on (small, standard, large), from `DesktopRuns` with the purpose `task`. A spare and a machine preparing an environment are Workstream's cost, not the team's. How many are running now is shown beside the limit.
- **Storage**: GB-months for each kind the team kept, from `StorageRuns`, with the gigabytes kept now.

| Kind | What is kept | Counted at |
| --- | --- | --- |
| `disk` | The disk of a task's desktop, while the task has a machine (`Tasks.Host`), running or not. | The disk's full size for the desktop's size (`Workstream:Desktops:Azure:DiskSizesGb`). |
| `archive` | What is kept of an archived task (`Tasks.Archive`). | What is on it (`Tasks.ArchiveBytes`). |
| `setup` | An environment's saved setup (`EnvironmentSetups.Save`). | What is on it (`SaveBytes`). |

A GB-month is one gigabyte kept for a month of 730 hours: the sum, over the rows, of gigabytes times the hours kept inside the month, divided by 730.

Storage is not written when something happens. Every fifteen minutes a job (`StorageJobs`, as `agent::desktop-operator`) reads what every team keeps now, from the operator's page of tasks and from the saved setups, and makes the open `StorageRuns` rows agree: it begins a row for anything kept that has none, ends the row of anything that is gone, and when a size changed ends the row and begins another at the same moment. Run twice, it does nothing the second time, and a run that was missed is put right by the next, so a row is exact to fifteen minutes.

The team's page shows amounts and no prices, except the model allowance. A system administrator sees every running desktop, can change a team's limits, and sees what each line costs Workstream: machine hours by purpose and size, and storage by kind, each at its rate in `DesktopOptions.Prices` (architecture.md, "What the desktops cost"). Payments are not built, and no customer price exists; the numbers that a bill would be made from are.

## Decisions

- **Team is the only container.** One grant per person per team keeps every list cheap and the rules short. Private work goes in a team of the person's own.
- **The desktop is a principal, and its task is a grant.** The agent's key identifies one service account. The account holds `task_desktop` on the one task it works on, so a key taken from a desktop reaches that task and nothing else, and stops working when the task is deleted. The key carries no task: the API finds the task by the grant, which is why a spare can become a task's desktop without being told anything new.
- **Sharing is additive, a grant on the task.** The team keeps its one grant on the team and sees every task; a person outside it is given one task, by a role on that task's node, with SqlOS's `EffectiveTo` as the end. Nothing a share grants reaches the team node, so the team's lists cost what they did.
- **The API is the only way into a desktop.** Desktops have no inbound access from the internet or from each other. The relay authorizes every stream.
- **A GitHub connection is a row, and its tokens are minted, not kept.** Which installation a team uses is configuration, not access: nobody is granted on it. A desktop's token is made for one repository, for an hour, at the moment it is asked for, so nothing in the database or on a desktop's disk is worth stealing, and a stolen token is worth one repository for less than an hour.
- **An installation is the person's to name only if GitHub says their account reaches it.** The installation id GitHub puts in the address can be changed by anyone, so the connection is made only after the person's own GitHub account is asked which installations it can reach, and only by the signed-in person the state was signed for.
- **Sign in with GitHub is SqlOS's connection and says only who a person is.** The whole of it is one seeded connection and a button over SqlOS's headless flow. It asks GitHub for the profile and the verified address, not for repositories, because nothing could use more: SqlOS reads the profile with GitHub's token and drops it. Giving a desktop the token of the person who started its task needs SqlOS to keep that token, or to hand it to the app at sign-in. Until it does, the team's GitHub App connection is how a desktop reaches a repository, and the app does not run an OAuth flow of its own beside SqlOS's to get one.
- **All inference goes through the gateway.** The Foundry key never leaves the API. Usage is recorded where it is spent.
- **The command line signs in through the browser with SqlOS's device flow, not with an API key and not with a loopback redirect.** SqlOS has a client for exactly this (`SeedCliClient`: public, no secret, the device and refresh grants, an allow-list of scopes and one audience), and its headless flow has the device steps (`/headless/device/resolve`, `approve`, `deny`), so the approval is one more page of the web app over the sign-in it already has. The token is the person's own session, so the command line has the person's grants as they are now, and loses them when the person does. An API key in SqlOS is a machine client bound to a service account, a principal of its own: to act as the person it would need grants copied from theirs on every team and kept in step with their memberships, a stored copy of access that the rules forbid. Authorization code with PKCE on a loopback address would also work in SqlOS, but it needs a free local port and a browser on the same machine, and the web app's sign-in would have to finish another client's request; the device flow needs neither, so it also works over SSH, in a container or on a dev VM. A scope of its own (`workstream.start`) keeps the command line to reading and starting tasks: a token taken from a laptop cannot stop, delete, rename or write to a task, change a team, or open a desktop.
- **The agent is Workstream's own, on the Claude Agent SDK.** Anthropic's terms for hosting Claude Code itself require each end user to bring their own credential; products built on the Agent SDK use the developer's API key or cloud provider. Claude Code is not wired to Workstream's inference.

## Not built yet

Payments, API keys for integrations, single sign-on, a desktop that pushes and opens pull requests as the person who started its task (from their sign-in with GitHub, once SqlOS keeps or hands over the person's GitHub token), connecting GitHub to an account that signed in by emailed code at another address, signing in with GitHub for a person on several teams, a private task inside a shared team, Windows desktops, a list in the web app of the command lines a person has signed in, to sign one out from there, a saved setup for a spare.
