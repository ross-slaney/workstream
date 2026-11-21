# Core model

Workstream gives each task its own cloud desktop with an agent working on it. A person watches the desktop in the browser, talks to the agent beside it, takes the mouse when they want to, and keeps up to four tasks on screen at once.

This document defines who can do what to what. It is written in SqlOS terms first: principals, nodes, permissions, roles and grants. Tables hold only what SqlOS cannot express. Code that disagrees with this document is wrong, or this document is out of date; fix one of them in the same pull request.

## Words

| Word | Meaning |
| --- | --- |
| Team | A group of people who share tasks and environments. A person who signs up gets a team of their own. Someone who creates their account from an invitation joins that team and gets none of their own. Anyone can create another. |
| Environment | What a desktop comes with: its apps and, optionally, a repository, a setup script, variables and instructions for the agent. |
| Task | One piece of work. It has a desktop and a conversation with the agent on that desktop. |
| Desktop | The task's cloud computer: a Linux virtual machine with a graphical desktop. |
| Agent | The program on the desktop that does the work. It is built on the Claude Agent SDK and calls models only through Workstream. |
| Message | One entry in a task's conversation, written by a person or by the agent. |

## Principals: who

Any column that names an actor holds a principal id.

| SqlOS type | In Workstream | Id | Notes |
| --- | --- | --- | --- |
| `user` | A person | the SqlOS user id | Made at sign-up. |
| `group` | A team's people | `group::{teamId}` | Mirrors the team's memberships. Joining the team is joining its group. |
| `group` | System administrators | `group::system_admins` | Seeded. Its members belong to the platform, not to a team. |
| `service_account` | A desktop | `service_account::desktop-{desktopId}` | What the agent on that desktop signs in as, with a key only that desktop holds. The id is the task's for a desktop made for a task, and the spare's own for a spare. The key says which desktop is calling; what it may do is the grant below. |
| `agent` | The desktop operator | `agent::desktop-operator` | The background jobs that start, stop and delete desktops and keep a team's spare. No credential: the jobs run in the API process. |

A team is a SqlOS organization. SqlOS keeps its name, memberships and invitations; the app mirrors each membership into the team's group (`IdentityProvisioning.cs`).

A person's command line (`cli/workstream.mjs`) is not a principal of its own. It holds a SqlOS session of the person's, made by the `workstream-cli` client, so it is the `user` with the person's grants and nothing more; what tells it apart is the scope its token carries (below).

## Resource tree: what, and under what

```text
root                          system_admin    -> group::system_admins
│                             desktop_operator -> agent::desktop-operator
└── team::{teamId}            team_member     -> group::{teamId}
    │                         team_admin      -> each administrator
    ├── environment::{id}
    └── task::{id}            task_desktop    -> service_account::desktop-{taskId}
```

A grant on a node applies to everything under it. A team's people hold one grant, on the team, so listing a team's tasks costs one index read however many tasks there are. Nobody is granted anything on a single task except its own desktop.

A task's messages are rows that carry the task's node (`IHasResourceId`). They are read as authorized pages of the rows themselves.

## Permissions

Each permission is checked on a node of its own type. Creates check the parent.

| Permission | Node | Allows |
| --- | --- | --- |
| `ADMIN_VIEW` | root | The system administrator's dashboard, and changing a team's limits. |
| `TEAM_VIEW` | team | See the team, its people and its limits. |
| `TEAM_VIEW_USAGE` | team | See what the team used this month. |
| `TEAM_MANAGE_MEMBERS` | team | Invite, remove and change people. |
| `TEAM_MANAGE_SETTINGS` | team | Rename the team. |
| `TEAM_CREATE_ENVIRONMENT` | team | Add an environment. |
| `TEAM_CREATE_TASK` | team | Start a task. |
| `ENVIRONMENT_VIEW` | environment | See an environment and start tasks from it. Variable values are never returned. |
| `ENVIRONMENT_EDIT` | environment | Change or delete it, including its variables. |
| `TASK_VIEW` | task | See the task and read its conversation. |
| `TASK_USE` | task | Send messages, interrupt the agent, and watch and control the desktop. |
| `TASK_MANAGE` | task | Rename, stop, start and delete it. |
| `TASK_RUN_AGENT` | task | The desktop's own: read its setup, exchange messages, call the model. |
| `TASK_OPERATE_DESKTOP` | task | The operator's own: start, stop and delete the desktop. |
| `TEAM_OPERATE_SPARE` | team | The operator's own: make and remove the desktop kept ready for the team's next task. |

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
| `desktop_operator` | root | `agent::desktop-operator` | `TASK_OPERATE_DESKTOP`, `TEAM_OPERATE_SPARE` |
| `team_member` | team | the team's group | `TEAM_VIEW`, `TEAM_VIEW_USAGE`, `TEAM_CREATE_ENVIRONMENT`, `TEAM_CREATE_TASK`, `ENVIRONMENT_VIEW`, `ENVIRONMENT_EDIT`, `TASK_VIEW`, `TASK_USE`, `TASK_MANAGE` |
| `team_admin` | team | whoever created the team, and anyone an administrator promotes | `TEAM_MANAGE_MEMBERS`, `TEAM_MANAGE_SETTINGS` |
| `task_desktop` | task | the desktop that works on the task | `TASK_RUN_AGENT` |

Everyone on a team can see, use and manage every task and environment in it. Work a person wants to keep to themselves goes in their own team.

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
| A task is deleted | Every desktop account that holds `task_desktop` on the task is expired, and the task's node is removed, with the grants on it. |

Grants are derived from these events. Nothing revokes "the grant this action made".

## Lists

| List | Page over | Permission | Order |
| --- | --- | --- | --- |
| My teams | `Teams` | `TEAM_VIEW` | `CreatedAt` |
| A team's tasks | `Tasks` in the team | `TASK_VIEW` | newest first (`CreatedAt`) |
| A team's environments | `Environments` in the team | `ENVIRONMENT_VIEW` | `Name` |
| A task's messages | `TaskMessages` of the task, after an id | `TASK_VIEW` | `Id` |
| Desktops the operator sweeps | `Tasks` | `TASK_OPERATE_DESKTOP` | `Id` |
| The task a desktop works on | `Tasks` | `TASK_RUN_AGENT`, as the desktop | `Id`, first one |

A team's people and its pending invitations are SqlOS's own rows, read after one `TEAM_VIEW` check on the team. Usage and the administrator's numbers are sums, read after one check (`TEAM_VIEW_USAGE` on the team, `ADMIN_VIEW` on root); SqlOS has no authorized count.

## App data, and why SqlOS does not hold it

| Table | Holds | Why it is app data |
| --- | --- | --- |
| `Teams` | The node, the month's model allowance, the limit on running desktops. | Limits are numbers, not access. |
| `Environments` | Name, apps, size, repository, setup script, instructions, protected variables. | Configuration. |
| `Tasks` | Title, environment, who created it (a principal id, a fact), the desktop's state and address. | The desktop's lifecycle. |
| `TaskMessages` | The conversation. | Content. |
| `DesktopRuns` | When each machine ran, what for (a task's desktop, a spare), what a task's start began from, and when each step of it happened. | Usage, cost, and how long starts take. |
| `Spares` | A team's spare: the machine, its size, when someone last wanted it. | The operator's stock, which nobody is granted on. It relates a team to a machine, not a principal to a node. |
| `ModelUsage` | Tokens and cost of each model call. | Usage. |

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

The browser opens a WebSocket to `GET /api/tasks/{id}/desktop`, carrying the person's access token. The API validates the token, which must carry `workstream.write` (the command line's cannot open a desktop), checks `TASK_USE` on the task, connects to the desktop's display on its private address, and relays bytes both ways, from the moment the display answers until the desktop is asked to stop. It checks again every minute and closes the stream when access is gone. Desktops accept connections only from the API.

### Stop, start, delete

Stopping releases the machine but keeps its disk; starting brings the same desktop back. A desktop nobody has watched or written to for a while, whose agent is idle, is stopped by the operator. Deleting a task deletes the machine, the conversation and the node.

### Usage and limits

A team has a model allowance for the calendar month and a limit on desktops running at once. People on the team see what was used. A system administrator sees every running desktop and can change a team's limits. Payments are not built; the numbers that a bill would be made from are.

## Decisions

- **Team is the only container.** One grant per person per team keeps every list cheap and the rules short. Private work goes in a team of the person's own.
- **The desktop is a principal, and its task is a grant.** The agent's key identifies one service account. The account holds `task_desktop` on the one task it works on, so a key taken from a desktop reaches that task and nothing else, and stops working when the task is deleted. The key carries no task: the API finds the task by the grant, which is why a spare can become a task's desktop without being told anything new.
- **The API is the only way into a desktop.** Desktops have no inbound access from the internet or from each other. The relay authorizes every stream.
- **All inference goes through the gateway.** The Foundry key never leaves the API. Usage is recorded where it is spent.
- **The command line signs in through the browser with SqlOS's device flow, not with an API key and not with a loopback redirect.** SqlOS has a client for exactly this (`SeedCliClient`: public, no secret, the device and refresh grants, an allow-list of scopes and one audience), and its headless flow has the device steps (`/headless/device/resolve`, `approve`, `deny`), so the approval is one more page of the web app over the sign-in it already has. The token is the person's own session, so the command line has the person's grants as they are now, and loses them when the person does. An API key in SqlOS is a machine client bound to a service account, a principal of its own: to act as the person it would need grants copied from theirs on every team and kept in step with their memberships, a stored copy of access that the rules forbid. Authorization code with PKCE on a loopback address would also work in SqlOS, but it needs a free local port and a browser on the same machine, and the web app's sign-in would have to finish another client's request; the device flow needs neither, so it also works over SSH, in a container or on a dev VM. A scope of its own (`workstream.start`) keeps the command line to reading and starting tasks: a token taken from a laptop cannot stop, delete, rename or write to a task, change a team, or open a desktop.
- **The agent is Workstream's own, on the Claude Agent SDK.** Anthropic's terms for hosting Claude Code itself require each end user to bring their own credential; products built on the Agent SDK use the developer's API key or cloud provider. Claude Code is not wired to Workstream's inference.

## Not built yet

Payments, API keys for integrations, single sign-on, a private task inside a shared team, view-only watching, Windows desktops, a list in the web app of the command lines a person has signed in, to sign one out from there.
