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
| `service_account` | A task's desktop | `service_account::desktop-{taskId}` | What the agent on that desktop signs in as, with a key only that desktop holds. It writes the agent's messages and uses the model. |
| `agent` | The desktop operator | `agent::desktop-operator` | The background jobs that start, stop and delete desktops. No credential: the jobs run in the API process. |

A team is a SqlOS organization. SqlOS keeps its name, memberships and invitations; the app mirrors each membership into the team's group (`IdentityProvisioning.cs`).

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

## Roles

| Role | Granted on | To | Carries |
| --- | --- | --- | --- |
| `system_admin` | root | `group::system_admins` | Everything. |
| `desktop_operator` | root | `agent::desktop-operator` | `TASK_OPERATE_DESKTOP` |
| `team_member` | team | the team's group | `TEAM_VIEW`, `TEAM_VIEW_USAGE`, `TEAM_CREATE_ENVIRONMENT`, `TEAM_CREATE_TASK`, `ENVIRONMENT_VIEW`, `ENVIRONMENT_EDIT`, `TASK_VIEW`, `TASK_USE`, `TASK_MANAGE` |
| `team_admin` | team | whoever created the team, and anyone an administrator promotes | `TEAM_MANAGE_MEMBERS`, `TEAM_MANAGE_SETTINGS` |
| `task_desktop` | task | the task's desktop | `TASK_RUN_AGENT` |

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
| A task is deleted | The service account is expired and the task's node is removed, with the grant on it. |

Grants are derived from these events. Nothing revokes "the grant this action made".

## Lists

| List | Page over | Permission | Order |
| --- | --- | --- | --- |
| My teams | `Teams` | `TEAM_VIEW` | `CreatedAt` |
| A team's tasks | `Tasks` in the team | `TASK_VIEW` | newest first (`CreatedAt`) |
| A team's environments | `Environments` in the team | `ENVIRONMENT_VIEW` | `Name` |
| A task's messages | `TaskMessages` of the task, after an id | `TASK_VIEW` | `Id` |
| Desktops the operator sweeps | `Tasks` | `TASK_OPERATE_DESKTOP` | `Id` |

A team's people and its pending invitations are SqlOS's own rows, read after one `TEAM_VIEW` check on the team. Usage and the administrator's numbers are sums, read after one check (`TEAM_VIEW_USAGE` on the team, `ADMIN_VIEW` on root); SqlOS has no authorized count.

## App data, and why SqlOS does not hold it

| Table | Holds | Why it is app data |
| --- | --- | --- |
| `Teams` | The node, the month's model allowance, the limit on running desktops. | Limits are numbers, not access. |
| `Environments` | Name, apps, size, repository, setup script, instructions, protected variables. | Configuration. |
| `Tasks` | Title, environment, who created it (a principal id, a fact), the desktop's state and address. | The desktop's lifecycle. |
| `TaskMessages` | The conversation. | Content. |
| `DesktopRuns` | When each desktop ran. | Usage, and the administrator's graph. |
| `ModelUsage` | Tokens and cost of each model call. | Usage. |

`Tasks.CreatedBy` records who started a task. It grants nothing.

## Flows

### Sign up and sign in

The app draws sign-in itself against SqlOS's headless endpoints: an emailed code in production, or a password where `Workstream:Auth:PasswordSignIn` is on (development and tests). A new person gets a team named after them, a default environment, and `team_admin` on the team.

### Start a task

1. The person picks a team and an environment and writes what they want done.
2. The API checks `TEAM_CREATE_TASK` on the team and `ENVIRONMENT_VIEW` on the environment, and refuses if the team already runs as many desktops as its limit allows.
3. It saves the task (desktop state `starting`) and the first message, and queues the start job.
4. The job, as `agent::desktop-operator`, provisions the desktop's service account, grants it `task_desktop`, and asks the desktop host for a machine carrying two values: the API's address and the desktop's key.
5. The desktop boots, reads its setup from the API with its key, prepares itself, and starts the agent. Its first exchange with the API marks the desktop `ready`.

### Talk to the agent

A person's message is a row in `TaskMessages`. The desktop exchanges with the API about once a second: it sends what the agent said and did, and receives what people wrote since its last exchange. The agent's messages are rows written as the desktop's service account. Every model call the agent makes goes to the API's model gateway with the desktop's key; the gateway checks `TASK_RUN_AGENT`, checks the team's allowance, forwards the call to Foundry and records its usage.

### Watch and use the desktop

The browser opens a WebSocket to `GET /api/tasks/{id}/desktop`, carrying the person's access token. The API validates the token, checks `TASK_USE` on the task, connects to the desktop's display on its private address, and relays bytes both ways. It checks again every minute and closes the stream when access is gone. Desktops accept connections only from the API.

### Stop, start, delete

Stopping releases the machine but keeps its disk; starting brings the same desktop back. A desktop nobody has watched or written to for a while, whose agent is idle, is stopped by the operator. Deleting a task deletes the machine, the conversation and the node.

### Usage and limits

A team has a model allowance for the calendar month and a limit on desktops running at once. People on the team see what was used. A system administrator sees every running desktop and can change a team's limits. Payments are not built; the numbers that a bill would be made from are.

## Decisions

- **Team is the only container.** One grant per person per team keeps every list cheap and the rules short. Private work goes in a team of the person's own.
- **The desktop is a principal.** The agent's key identifies one service account with one grant on one task, so a key taken from a desktop reaches that task and nothing else, and stops working when the task is deleted.
- **The API is the only way into a desktop.** Desktops have no inbound access from the internet or from each other. The relay authorizes every stream.
- **All inference goes through the gateway.** The Foundry key never leaves the API. Usage is recorded where it is spent.
- **The agent is Workstream's own, on the Claude Agent SDK.** Anthropic's terms for hosting Claude Code itself require each end user to bring their own credential; products built on the Agent SDK use the developer's API key or cloud provider. Claude Code is not wired to Workstream's inference.

## Not built yet

Payments, API keys for integrations, single sign-on, a private task inside a shared team, view-only watching, Windows desktops.
