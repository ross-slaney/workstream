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
| Build | A project's iPhone app, built on a Mac because a desktop cannot build one. A desktop asks for the build of the native code it has, and the team keeps one build for each. |
| Message | One entry in a task's conversation, written by a person or by the agent. |
| Share | One task given to a person outside its team: to watch it, to write to its agent too, or to use its desktop as well, for a while or until it is taken back. |
| workstream.yml | A file at the root of an environment's repository that holds the environment's size, apps, setup script, variable names and instructions. An environment filled from it, or saved to it, follows it. |

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
| `service_account` | The Mac that builds iPhone apps | `service_account::app-builder` | One for the deployment. Its key is the deployment's setting (`Workstream:Builds:Key`), which the workflow that is the Mac holds as its secret. The key says the Mac is calling; what it may do is the grant below. Where a deployment sets no key the account is expired. |

A team is a SqlOS organization. SqlOS keeps its name, memberships and invitations; the app mirrors each membership into the team's group (`IdentityProvisioning.cs`).

A person's command line (`cli/workstream.mjs`) is not a principal of its own. It holds a SqlOS session of the person's, made by the `workstream-cli` client, so it is the `user` with the person's grants and nothing more; what tells it apart is the scope its token carries (below).

## Resource tree: what, and under what

```text
root                          system_admin    -> group::system_admins
│                             desktop_operator -> agent::desktop-operator
│                             task_notifier   -> agent::task-notifier
│                             app_builder     -> service_account::app-builder
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
| `TEAM_VIEW` | team | See the team, its people and its limits. |
| `TEAM_VIEW_USAGE` | team | See what the team used this month: models, desktop hours by size, storage by kind. |
| `TEAM_MANAGE_MEMBERS` | team | Invite, remove and change people. |
| `TEAM_MANAGE_SETTINGS` | team | Rename the team; change when its desktops are paused and archived. |
| `TEAM_CREATE_ENVIRONMENT` | team | Add an environment. |
| `TEAM_CREATE_TASK` | team | Start a task. |
| `ENVIRONMENT_VIEW` | environment | See an environment and start tasks from it. Variable values are never returned. |
| `ENVIRONMENT_EDIT` | environment | Change or delete it, including its variables, ask for its setup to be prepared again, and save it to its repository as workstream.yml (as the person, with their GitHub token). |
| `TASK_VIEW` | task | See the task, read its conversation and watch its desktop. |
| `TASK_PROMPT` | task | Write to the agent and interrupt it. Writing to a paused or archived task starts its desktop. |
| `TASK_DRIVE` | task | Use the desktop's mouse, keyboard and clipboard. Without it the relay drops what the browser would send them. Starting a paused, archived or failed desktop is using it, and takes this too. |
| `TASK_MANAGE` | task | Rename, pause and delete it; share it, see who it is shared with, and take a share back. |
| `TASK_RUN_AGENT` | task | The desktop's own: read its setup, say how its setup script is going, exchange messages, call the model, get the GitHub token of the person who started the task, and ask for its team's iPhone builds: send a build's source, see how it stands, and take its app and its log. |
| `TASK_OPERATE_DESKTOP` | task | The operator's own: start, pause, archive and delete the desktop, and count the disk or archive the task keeps. |
| `TEAM_OPERATE_SPARE` | team | The operator's own: make and remove the desktop kept ready for the team's next task. |
| `TASK_NOTIFY` | task | The notifier's own: read what the agent last said and email the task's people about it. |
| `ENVIRONMENT_RUN_SETUP` | environment | The preparing desktop's own: read the environment's setup, with its variables, and say how it went. |
| `ENVIRONMENT_OPERATE_SETUP` | environment | The operator's own: make the machine that prepares the environment, save its disk, and remove both. |
| `TEAM_RUN_BUILD` | team | The Mac's own: take a build of the team's, read its source, and hand back its log and its app. |
| `TEAM_OPERATE_BUILD` | team | The operator's own: ask the Mac for a build once its source is here, fail a build the Mac never finished, and remove what nobody has taken for a while. |

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
| `desktop_operator` | root | `agent::desktop-operator` | `TASK_OPERATE_DESKTOP`, `TEAM_OPERATE_SPARE`, `ENVIRONMENT_OPERATE_SETUP`, `TEAM_OPERATE_BUILD` |
| `task_notifier` | root | `agent::task-notifier` | `TASK_NOTIFY` |
| `team_member` | team | the team's group | `TEAM_VIEW`, `TEAM_VIEW_USAGE`, `TEAM_CREATE_ENVIRONMENT`, `TEAM_CREATE_TASK`, `ENVIRONMENT_VIEW`, `ENVIRONMENT_EDIT`, `TASK_VIEW`, `TASK_PROMPT`, `TASK_DRIVE`, `TASK_MANAGE` |
| `team_admin` | team | whoever created the team, and anyone an administrator promotes | `TEAM_MANAGE_MEMBERS`, `TEAM_MANAGE_SETTINGS` |
| `task_desktop` | task | the desktop that works on the task | `TASK_RUN_AGENT` |
| `environment_setup` | environment | the environment's preparing account | `ENVIRONMENT_RUN_SETUP` |
| `app_builder` | root | the Mac that builds iPhone apps | `TEAM_RUN_BUILD`: the source of any team's build while it is being built, and nothing else of any team. |
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
| The API starts with a builder key set | The Mac's account is provisioned the first time, given the key's hash when the key changed, and granted `app_builder` on root, once. With no key set, the account is expired, and its grant opens nothing. |

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

A team's people and its pending invitations are SqlOS's own rows, read after one `TEAM_VIEW` check on the team. Who a task is shared with is SqlOS's grants on the task's node with the three sharing roles whose `EffectiveTo` has not passed, and its waiting invitations are `TaskInvitations` rows, both read after one `TASK_MANAGE` check on the task. A team's builds are `Builds` rows, found by the team and the hash: a desktop reads its team's after the page that finds its task (`TASK_RUN_AGENT`), and the Mac reads one after a `TEAM_RUN_BUILD` check on that build's team. Usage and the administrator's numbers are sums over `ModelUsage`, `DesktopRuns`, `StorageRuns` and `Builds`, read after one check (`TEAM_VIEW_USAGE` on the team, `ADMIN_VIEW` on root); SqlOS has no authorized count.

## App data, and why SqlOS does not hold it

| Table | Holds | Why it is app data |
| --- | --- | --- |
| `Teams` | The node, the month's model allowance, the limit on running desktops, and when the team's desktops are paused (minutes without use) and archived (days paused). | Limits and settings are numbers, not access. |
| `Environments` | Name, apps, size, repository, setup script, instructions, protected variables, and `SetupInputs`: a SHA-256 of the repository, branch, script, variables, apps and size, written where the environment is written, because that is the one place the variables are readable. `FromFile`: its settings are its repository's workstream.yml. `ChangedBy`: who last saved its settings (a principal id, a fact), whose GitHub token the machine preparing it uses. | Configuration. |
| `EnvironmentSetups` | An environment's saved setup (the snapshot's name, the inputs and image version it was made for, its commit, its size, when it was made and last used) and the attempt to prepare it in progress or how the last one failed. One row an environment, deleted with it. | The operator's stock and a lifecycle, like `Spares`: nobody is granted on it, and seeing it is seeing the environment. |
| `Tasks` | Title, environment, who created it (a principal id, a fact), the desktop's state and address, when it was last used, how much is on its disk, its archive's name and size (`Archive`, `ArchiveBytes`) while it has one, when it last emailed its people. | The desktop's lifecycle. |
| `TaskSetups` | The environment's setup script as it ran on a task's own desktop: running, done, failed or stopped for running too long, its exit code, the last lines it wrote, when, and the limit it ran under. One row a task that ran one, deleted with it. | A lifecycle and content of the task's, like its desktop's state: it is read with the task's conversation after `TASK_VIEW`, and written only by the task's own desktop (`TASK_RUN_AGENT`). |
| `TaskMessages` | The conversation. | Content. |
| `TaskInvitations` | A task shared with an address that has no account yet: the address, the role and end it will be granted with, who shared it, the hash of the link's token, and when the link stops working (a week, or sooner if the access ends sooner). | There is no principal to grant yet. SqlOS's invitations are memberships of an organization, and this person is not joining the team, so the grant waits here until they take the link; then it is a grant and the row is gone. If SqlOS gains an invitation to a role on a node, this table goes. |
| `DesktopRuns` | When each machine ran, at which size, what for (a task's desktop, a spare, a machine preparing an environment), what a task's start began from (the image, a saved setup, a spare, its own paused machine, its archive), and when each step of it happened. | Usage, cost, and how long starts take. |
| `StorageRuns` | Each stretch of a team keeping something at one size: the kind (`disk`, `archive`, `setup`), the task or environment it belongs to, the gigabytes, and when it began and ended. | Usage and cost, like `DesktopRuns`. It relates a team to an amount, not a principal to a node, and is derived: a job makes the open rows agree with what the tasks and environments say is kept, so nothing else writes them. |
| `Spares` | A team's spare: the machine, its size, when someone last wanted it. | The operator's stock, which nobody is granted on. It relates a team to a machine, not a principal to a node. |
| `ModelUsage` | Tokens and cost of each model call an agent makes. | Usage. |
| `PersonSettings` | A person's own settings, one row by their user id: whether task emails reach them. No row is every default. | A preference, not access. It relates the person to nothing in the tree; it is read and changed only by the person it belongs to, found by the id in their token, so there is nothing to check. |
| `TaskWatchers` | When each person last had a task's desktop on screen, one row a person a task. Removed with the task. | A fact about what someone did, like `Tasks.CreatedBy`, and never a permission: whether they may watch is `TASK_VIEW`. It is read only to keep from emailing someone about the task they are looking at. |
| `Builds` | A team's builds of its iPhone apps: the hash of the native code it is a build of, the app's folder inside the source, the task whose desktop asked first (a fact), where it stands (waiting for its source, queued, building, done, failed), why it failed, the app's bundle identifier and size, when it was asked for, taken and ended, how long the Mac had it, and when a desktop last took the app. One row a team a hash. | A lifecycle and usage, like `EnvironmentSetups` and `DesktopRuns`. Nobody is granted on a build: it relates a team to an app, and a desktop reaches it through the one task it holds `task_desktop` on. The source, the app and the log are files in the deployment's own storage, under the team's id. |

`Tasks.CreatedBy` records who started a task. It grants nothing.

## Flows

### Sign up and sign in

The app draws sign-in itself against SqlOS's headless endpoints: an emailed code in production, or a password where `Workstream:Auth:PasswordSignIn` is on (development and tests). A new person gets a team named after them, a default environment, and `team_admin` on the team.

Where the deployment has a GitHub OAuth client (`SqlOS:Oidc:GitHub:ClientId` and `ClientSecret`; infra/README.md, "Sign in with GitHub"), the form also shows Continue with GitHub, on the sign-in page and on the command line's page. It is SqlOS's own GitHub connection, seeded in code (`api/Workstream.cs`, `SeedGitHubSignIn`); the app adds no table and no flow of its own:

1. SqlOS lists GitHub with the sign-in flow, and the form shows the button. Without the two settings it lists nothing and the form has no button.
2. Pressing it asks SqlOS where to go (`/headless/provider/start`) and the browser leaves for GitHub, which asks the person, the first time, for their profile, their email addresses and their repositories (`read:user`, `user:email`, `repo`).
3. GitHub sends the browser to SqlOS's callback on the API (`/sqlos/auth/oidc/callback`). SqlOS reads the GitHub account's number and its verified primary address. An account it has seen before is that person. Otherwise an address that already has an account is that person, and the GitHub account is attached to them. Otherwise it is someone new, who gets what a new person gets. A GitHub account with no verified primary address is refused.
4. SqlOS sends the browser to the web app's `/auth/callback` with a code, which the page exchanges with what the sign-in page kept for the trip (its `state` and PKCE verifier, in the tab's storage). On the command line's page SqlOS hands the browser back to that page, signed in, to allow the command line or not.

Someone GitHub did not sign in comes back to the form, which says so and still takes their email. An invitation is for one address, so its form shows no GitHub button.

A person on several teams whom GitHub signed in is asked by SqlOS which team the session is for. SqlOS hands the browser to the sign-in page with the request, `view=organization` and its pending token (`SignInUiUrl`): the token is good once, for this request, and the code it leads to can be redeemed only with the PKCE verifier the tab kept for the trip, so it may travel in the address where SqlOS's cookie on the API's address would not reach a page on another site. The page reads the request again, which lists the teams (SqlOS 11.5.0), answers with the first, as it does after an emailed code (`useProviderTeamChoice`, `client/hooks/use-sign-in.ts`), and exchanges the code. The app does not care which: every request names its team in its address.

SqlOS keeps the token GitHub gave it at each sign-in, beside the person's GitHub identity, encrypted, and never sends it to a browser; the connection is seeded with keeping tokens on. The app reads a person's current token through one interface (`IGitHubTokens.ForUserAsync`, `api/GitHub/GitHub.cs`), and nowhere else. It asks SqlOS for the person's current token for the `github` connection (`SqlOSGitHubTokens`, `SqlOSOidcAuthService.GetProviderAccessTokenAsync`, SqlOS 11.5.0), which answers with none when they never signed in with GitHub, GitHub refused the token, or GitHub could not be reached. The token is what lists the person's repositories, reads and saves workstream.yml, and lets the desktops of tasks they start push as them (below). A person who signed in by email has none until they sign in with GitHub.

An invitation's link carries a token. Signing in, or creating an account, with the token attached is what accepts it: the link proves the mailbox, so someone new only gives their name. They land in the team that invited them, and someone who created their account this way gets no team of their own.

When a person is removed from a team, or leaves it, SqlOS ends the session that was for that team. They sign in again and see the teams they are still on.

The system administrator is the one address the deployment names (`Workstream:Administrator:Email`). At every start the API makes that person the only member of `group::system_admins`; a changed address moves the role, and no address leaves the group empty.

### Start a task

1. The person picks a team and an environment and writes what they want done.
2. The API checks `TEAM_CREATE_TASK` on the team and `ENVIRONMENT_VIEW` on the environment, and refuses if the team already runs as many desktops as its limit allows.
3. It saves the task (desktop state `starting`) and the first message, and queues the start job.
4. The job, as `agent::desktop-operator`, provisions the desktop's service account, grants it `task_desktop`, and asks the desktop host for a machine carrying two values: the API's address and the desktop's key.
5. The desktop boots. Its display answers first, and from then the team can watch it; its agent's first exchange with the API marks the desktop `ready`. The agent reads its setup from the API with its key and prepares the desktop.
6. When the environment has a setup script and the desktop did not start from a saved setup, the agent runs it, for as long as `Workstream:Desktops:SetupMinutes` allows (45), and says how it is going (`POST /api/desktop/setup`, as the task's desktop: the task is the one its account holds `task_desktop` on). The API keeps that in `TaskSetups`, and everyone who can see the task (`TASK_VIEW`) sees it with the conversation. A script that fails or is stopped for running too long leaves the task `ready`: the desktop is still there to work on.

### A spare desktop

Making a machine takes Azure from fifteen seconds to a minute, and nothing Workstream does shortens that. So while someone is in a team's workspace, the operator keeps one desktop running for the task they may be about to start.

1. The web app says someone is there when the team's pages open and when the new-task dialog opens (`POST /api/teams/{id}/spare`). The check is `TEAM_CREATE_TASK` on the team: whoever may start a task may have one warmed up.
2. If the team has no spare and the limits allow, the operator (`TEAM_OPERATE_SPARE` on the team) makes one for the environment the team used last. Its service account is granted nothing. Its agent asks the API twice a second what it is for, and is told there is nothing yet.
3. A task started from an environment of the same size takes the spare in the same transaction that makes the task: the spare's row is deleted, the task records its machine, and the spare's account is granted `task_desktop` on the task. The screen can be watched at once. The agent's next question is answered with the task's setup.
4. The operator then makes the team's next spare. A spare nobody on the team has wanted for fifteen minutes is removed with its machine and its key.

The limits are settings (`Workstream:Desktops:Spares`): one spare a team, so many running at once over every team, so many hours of spare time a day, and none for a team already at its limit of running desktops. A spare is never given to another team.

### Prepare an environment

Cloning a repository and running a setup script can take minutes on every new desktop. An environment with a repository, a setup script or the iPhone among its apps (whose simulator takes twenty minutes to install) is prepared instead: the setup is done once, on a machine of its own, and that machine's disk is saved for the environment's tasks to start from.

1. Something asks for it: the environment is added with a setup, or its setup inputs change; someone presses "Prepare again" (`POST /api/environments/{id}/setup`, `ENVIRONMENT_EDIT`); or a task starts and finds no save it can use (it is not asked again for settings whose last attempt failed), or finds one older than a week (the task uses the old one, and the new one is made behind it).
2. The operator (`ENVIRONMENT_OPERATE_SETUP` on the environment) makes a machine when the limits allow (`Workstream:Desktops:Saves`: one preparing at once, four hours of preparing a day, each attempt as long as the setup script may run and half an hour more). The machine is told the API's address and a new key for the environment's preparing account, which holds `environment_setup` on the environment and nothing else. Its time is a `DesktopRun` with the purpose `prepare`.
3. The machine's agent reads its setup (`GET /api/desktop/setup`, found by its page of environments with `ENVIRONMENT_RUN_SETUP`), which says it is preparing and carries no task and no model. It clones the repository, runs the setup script, and says how it is going (`POST /api/desktop/setup`). Before it says it is done it removes everything of the preparing from the disk: the variables, the logs and the browser's profile; git keeps no token, since it asks for one each time (a preparing desktop gets the GitHub token of whoever last saved the environment while its attempt lasts). It leaves one file saying when and at which commit it was prepared.
4. Only when the script exits 0, the operator stops the machine, saves its disk as a snapshot, removes the machine, ends the key, and removes the save it replaces: an environment keeps one. A failed attempt keeps nothing, and its machine is removed.
5. A save is used only for the setup inputs it was made from, the environment's size among them, and the desktops' image version it was made on (`Workstream:Desktops:Azure:ImageId`). A task whose environment has such a save gets a machine made from it; its run's source is `save`, its agent pulls the branch's newer commits and does not run the setup script, and its first line says where it started from. Any other task starts from the image and does the setup itself, as before; so does a task started while its environment is being prepared.

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

### A desktop's GitHub token

A desktop works on GitHub as the person who started its task.

1. On the desktop, git's credential helper and the wrapper around gh ask the API for a token with the desktop's key, each time they need one (`GET /api/desktop/github/token`, `desktop/agent/github.mjs`). A `GITHUB_TOKEN` or `GH_TOKEN` the environment's variables hold is used instead, without asking.
2. The API finds the task the desktop holds `TASK_RUN_AGENT` on and answers with the GitHub token of the person who started it (`Tasks.CreatedBy`), read through `IGitHubTokens`. A machine preparing an environment (`ENVIRONMENT_RUN_SETUP`) gets the token of whoever last saved the environment (`Environments.ChangedBy`) while its attempt lasts. A spare, and the desktop of a deleted task, get nothing.
3. When the desktop starts, its agent also asks who its commits are by (`GET /api/desktop/github/author`): the API reads the same person's GitHub account with their token and answers the name it shows (its login when it has none) and the account's noreply address, `{id}+{login}@users.noreply.github.com`, which the agent sets as git's author and committer. Without a token there is none, and commits are by "Workstream agent".
4. When that person has no GitHub token, the answer is 404 with words naming them; git and gh print the words, and the agent is told to pass them on. The task's page asks the same question (`GET /api/tasks/{id}/github`, `TASK_VIEW`) and says who needs to sign in with GitHub.

The token is the person's: it reaches every repository their GitHub account reaches, with their permissions, and GitHub decides how long it lasts. It is never written to the desktop's disk; it is in the desktop's memory while git or gh uses it. Someone a task is shared with who may write to its agent (`task_prompter`) or use its desktop (`task_driver`) can have work pushed in the starter's name, as the team's people can, while the share lasts.

### Build an iPhone app

An iPhone app is built with Xcode on a Mac, and a desktop is neither. So the desktop asks Workstream for the build, and Workstream has it made on a Mac of its own. Nothing is added to the project's repository, and nothing of the build is kept on GitHub.

1. On the desktop, `phone ios build` works out a hash of everything the native app is made from (for an Expo app, Expo's own fingerprint; otherwise the files that name the packages, configure the app or are native source). It asks with the desktop's key (`POST /api/desktop/builds`: the platform, the hash and the app's folder). The API finds the task the desktop works on (`TASK_RUN_AGENT`) and answers with the build its team has for that hash, whatever its state, or makes one that waits for its source. A team has a limit of builds asked for in a day (`Workstream:Builds:PerTeamADay`, 30).
2. A build that is done is taken at once (`GET /api/desktop/builds/{id}/app`). A change to JavaScript or Dart alone does not change the hash, so most tasks take the app their team already has.
3. For a build that waits, the desktop sends the source as one archive (`PUT /api/desktop/builds/{id}/source`): the files git knows of, from the folder the app's packages are installed from. The API keeps it under the team's id and queues a job.
4. The job, as `agent::desktop-operator` (`TEAM_OPERATE_BUILD` on the team), asks the Mac: one request to GitHub to run the workflow `ios-build.yml` of Workstream's own repository for this build (`IMacBuilder`, `api/Builds/Builds.cs`), with the token a deployment set for it or, without one, the system administrator's own GitHub token. A Mac that cannot be asked fails the build with the reason.
5. The Mac calls back with its key (`service_account::app-builder`). Each call loads the build and checks `TEAM_RUN_BUILD` on its team: it takes the build (`POST /api/builder/builds/{id}/take`), reads the source, builds, hands back the log and the app, and says how it ended. The source is then removed.
6. The desktop, which asked how the build stands every ten seconds, takes the app, installs it on its iPhone and opens it. A build that failed keeps its log, which the desktop prints for the agent; it is built again only when a desktop asks for that (`again`).

Two desktops of a team that ask for the same hash in the same moment get one build: the team has one row a hash, and the second is answered with the first's. Another team's desktop asking for the same hash gets a build of its own, and is never told the first exists.

A build the Mac has not finished in ninety minutes fails, and an app no desktop has taken for thirty days is removed with its log, both by a sweep every ten minutes (`BuildJobs.SweepAsync`).

### Picking a repository

The environment form lists the signed-in person's repositories (`GET /api/github/repositories`, most recently pushed first, up to three hundred) and a picked repository's branches (`GET /api/github/repositories/{owner}/{name}/branches`), read from GitHub with their token. Nothing is checked in Workstream: it is their GitHub, read as them.

Picking a repository reads its workstream.yml on the branch (below), which fills the form. Without one, the form shows an example of the file and the person fills in the settings. Workstream never guesses a setup: what a desktop starts with is what the file or the form says.

### workstream.yml

An environment can live in its repository as `workstream.yml` at the root of its branch: `size`, `setup`, `apps`, `variables` (names only) and `instructions`, each optional (`api/Domain/WorkstreamFile.cs`). It is read strictly: an unknown setting, size or app, a variable name a shell cannot read, or YAML that does not parse is an error with its line, and nothing is taken from it.

- **Reading.** `GET /api/github/repositories/{owner}/{name}/workstream-file?branch=` answers whether it is there and its settings or errors. A form filled from it saves the environment with `FromFile`.
- **Following.** Each task's start on an environment with `FromFile` reads the file again from GitHub, as the person starting the task, for up to eight seconds. What it says now becomes the environment's size, apps, setup script and instructions before anything is decided from them (a spare, a saved setup), so the repository stays where the environment is defined. Its variables stay as they are, since their values live here. When the person has no token, GitHub does not answer, or the file is gone or not valid, the task starts with what the environment holds. A person who changes in the form what the file decides turns `FromFile` off.
- **Saving.** `POST /api/environments/{id}/workstream-file` (`ENVIRONMENT_EDIT`) writes the environment's settings as the file on its branch, as one commit with the person's token. Where the branch refuses it (409, 422 or 403, a protected branch or a rule), the file goes on a new branch and a pull request into the environment's branch is opened instead; where that is refused too, the person is told GitHub did not let them write. Either way the environment then has `FromFile`.

### Usage and limits

A team has a model allowance for the calendar month and a limit on desktops running at once; a paused or archived desktop is not running, and resuming one counts against the limit like starting one. People on the team see what was used this month (`GET /api/teams/{id}/usage`, `TEAM_VIEW_USAGE`), in the units a bill would use:

- **Models**: dollars spent against the allowance, from `ModelUsage`.
- **Desktop time**: hours for each size the team's tasks ran on (small, standard, large), from `DesktopRuns` with the purpose `task`. A spare and a machine preparing an environment are Workstream's cost, not the team's. How many are running now is shown beside the limit.
- **Storage**: GB-months for each kind the team kept, from `StorageRuns`, with the gigabytes kept now.
- **iPhone builds**: minutes a Mac had the team's builds, from `Builds` (each build's time from when the Mac took its source until it said it was done, counted in the month it ended, whether or not it worked). A team that has had none built does not see the line.

| Kind | What is kept | Counted at |
| --- | --- | --- |
| `disk` | The disk of a task's desktop, while the task has a machine (`Tasks.Host`), running or not. | The disk's full size for the desktop's size (`Workstream:Desktops:Azure:DiskSizesGb`). |
| `archive` | What is kept of an archived task (`Tasks.Archive`). | What is on it (`Tasks.ArchiveBytes`). |
| `setup` | An environment's saved setup (`EnvironmentSetups.Save`). | What is on it (`SaveBytes`). |

A GB-month is one gigabyte kept for a month of 730 hours: the sum, over the rows, of gigabytes times the hours kept inside the month, divided by 730.

Storage is not written when something happens. Every fifteen minutes a job (`StorageJobs`, as `agent::desktop-operator`) reads what every team keeps now, from the operator's page of tasks and from the saved setups, and makes the open `StorageRuns` rows agree: it begins a row for anything kept that has none, ends the row of anything that is gone, and when a size changed ends the row and begins another at the same moment. Run twice, it does nothing the second time, and a run that was missed is put right by the next, so a row is exact to fifteen minutes.

The team's page shows amounts and no prices, except the model allowance. A system administrator sees every running desktop, can change a team's limits, and sees what each line costs Workstream: machine hours by purpose and size, and storage by kind, each at its rate in `DesktopOptions.Prices` (architecture.md, "What the desktops cost"), and the Mac's minutes at `Workstream:Builds:MacMinuteUsd`. Payments are not built, and no customer price exists; the numbers that a bill would be made from are.

## Decisions

- **Team is the only container.** One grant per person per team keeps every list cheap and the rules short. Private work goes in a team of the person's own.
- **The desktop is a principal, and its task is a grant.** The agent's key identifies one service account. The account holds `task_desktop` on the one task it works on, so a key taken from a desktop reaches that task and nothing else, and stops working when the task is deleted. The key carries no task: the API finds the task by the grant, which is why a spare can become a task's desktop without being told anything new.
- **Sharing is additive, a grant on the task.** The team keeps its one grant on the team and sees every task; a person outside it is given one task, by a role on that task's node, with SqlOS's `EffectiveTo` as the end. Nothing a share grants reaches the team node, so the team's lists cost what they did.
- **The API is the only way into a desktop.** Desktops have no inbound access from the internet or from each other. The relay authorizes every stream.
- **Having a task open is not using it.** A pane left open in a browser tab once kept a desktop running for as long as the tab lived, because the relay marked the task active every time it re-admitted the stream. Use is now only what a person or the agent does: input that reaches the display, a message, the agent working. The cost of a desktop follows the work, not the tab.
- **Paused keeps the name `stopped`.** `Tasks.State` is stored as its name, and the rows, the API and the command line already say `stopping` and `stopped`. Renaming them would need every stored row rewritten at the moment of a deployment, for no change in what happens to the machine. So the state keeps its name and people read Paused; `archiving` and `archived` are new names beside it.
- **The pause and archive times are the team's, not a role's.** They are two numbers on the team's row, changed with the permission that already covers the team's settings (`TEAM_MANAGE_SETTINGS`, which `team_admin` carries). No role, no grant and no permission was added for them.
- **Resuming is use, not management.** Stopping was `TASK_MANAGE` and so was starting. Now that a desktop pauses by itself, bringing it back is part of using the task: someone it is shared with to write to its agent or to use its desktop would otherwise find it paused after half an hour and be unable to do what they were given. So a message resumes (`TASK_PROMPT`) and a click resumes (`TASK_DRIVE`); pausing and deleting stay the team's (`TASK_MANAGE`).
- **A desktop works on GitHub as the person who started its task.** One sign-in with GitHub, one approval screen the first time, and pushes and pull requests in the person's own name, with nothing to paste. The token is the person's, from SqlOS, which keeps provider tokens the way it keeps its other secrets; the app reads it through one interface and keeps no copy. It is handed to the desktop of a task that person started and to no other, and only when asked. The cost is that a desktop, and anyone who can tell its agent what to do, can reach everything that person's GitHub account can while the task lasts. A GitHub App's installation tokens would be narrower (one repository, an hour), but they act as the app, not the person, and need an installation per account; the team connection that did that was removed.
- **workstream.yml is read at each start, and the environment keeps a copy.** Reading it at the start keeps the repository the source of truth without a webhook or a sync job; keeping the last values means a task still starts when GitHub is slow or the starter has no token. Variables are names in the file and values here, so a secret never goes into a repository.
- **All inference goes through the gateway.** The Foundry key never leaves the API. Usage is recorded where it is spent.
- **The command line signs in through the browser with SqlOS's device flow, not with an API key and not with a loopback redirect.** SqlOS has a client for exactly this (`SeedCliClient`: public, no secret, the device and refresh grants, an allow-list of scopes and one audience), and its headless flow has the device steps (`/headless/device/resolve`, `approve`, `deny`), so the approval is one more page of the web app over the sign-in it already has. The token is the person's own session, so the command line has the person's grants as they are now, and loses them when the person does. An API key in SqlOS is a machine client bound to a service account, a principal of its own: to act as the person it would need grants copied from theirs on every team and kept in step with their memberships, a stored copy of access that the rules forbid. Authorization code with PKCE on a loopback address would also work in SqlOS, but it needs a free local port and a browser on the same machine, and the web app's sign-in would have to finish another client's request; the device flow needs neither, so it also works over SSH, in a container or on a dev VM. A scope of its own (`workstream.start`) keeps the command line to reading and starting tasks: a token taken from a laptop cannot stop, delete, rename or write to a task, change a team, or open a desktop.
- **An iPhone app is built on Workstream's side, never in the project's repository.** The Mac is a workflow of Workstream's own repository, started by Workstream and paid for by whoever runs the deployment; the source goes there from the desktop through the API, and the app comes back the same way. A project adds no workflow, no file and no secret, and its GitHub account sees no run, no artifact and no minutes. The cost is that a project's source for that build leaves the desktop for a Mac GitHub runs, for as long as the build takes.
- **A build belongs to a team and is found by a hash the desktop works out.** One row a team a hash is what makes a second task free and two tasks at once one build. The hash is not checked by the API: a desktop that sent a wrong one would get the wrong app, and only its own team's, since every lookup is inside the team its task is in. Sharing builds across teams would save Mac time for two teams on one repository, and would let one team learn what another has built; it was not done.
- **The Mac is one principal with one grant.** Its key is a deployment's secret, the same for every build, so nothing secret travels in a workflow's inputs, which a public repository's runs may show. What the key reaches is narrow: a build's source only between taking it and ending it, and nothing else of any team.

- **The agent is Workstream's own, on the Claude Agent SDK.** Anthropic's terms for hosting Claude Code itself require each end user to bring their own credential; products built on the Agent SDK use the developer's API key or cloud provider. Claude Code is not wired to Workstream's inference.

## Not built yet

Payments, API keys for integrations, single sign-on, connecting GitHub to an account that signed in by emailed code at another address, a private task inside a shared team, Windows desktops, iPhone builds for a real device or for the App Store, a team's storage counted for the apps it keeps, a list in the web app of the command lines a person has signed in, to sign one out from there, a saved setup for a spare.
