import { apiJson, apiSend } from "@/lib/api/client";

/*
 * Every call the app makes to the Workstream API, and the shapes it answers with. One function per route
 * (api/WorkstreamApi.*.cs). Controllers hand these to a read hook or call them for an action; nothing else
 * in the app knows a path.
 */

export type ListPage<T> = { data: T[]; nextCursor: string | null };

export type Me = { userId: string; name: string; email: string | null; systemAdmin: boolean };

/** The person's own settings: `taskEmails` is whether they are emailed when a task they started or wrote in is finished or needs them. */
export type MySettings = { taskEmails: boolean };

/** `pauseAfterMinutes`: a desktop nobody has used for that long is paused. `archiveAfterDays`: a desktop paused for that long is archived. */
export type Team = { id: string; name: string; admin: boolean; modelAllowanceUsd: number; maxRunningDesktops: number; pauseAfterMinutes: number; archiveAfterDays: number };

export type TeamPerson = { userId: string; name: string; email: string | null; admin: boolean; joinedAt: string };

/** `link` comes only with the answer to making it; `emailed` says whether Workstream sent it to them as well. */
export type PendingInvitation = { id: string; email: string; admin: boolean; expiresAt: string; link: string | null; emailed: boolean };

/** The hours a team's desktops of one size (`small`, `standard`, `large`) ran this month. */
export type SizeHours = { size: string; hours: number };

/**
 * The storage of one kind a team kept this month: `disk` (desktops' disks, at their full size), `archive`
 * (archived tasks) or `setup` (environments' saved setups), both at what is on them. `gb` is kept now;
 * `gbMonths` is the month so far, a gigabyte kept for the whole month being one.
 */
export type StorageUse = { kind: string; gb: number; gbMonths: number };

/** What a team used this month, in the units a bill would use. A size or a kind it did not use is not listed. */
export type TeamUsage = {
  since: string;
  modelSpentUsd: number;
  modelAllowanceUsd: number;
  desktopHours: SizeHours[];
  storage: StorageUse[];
  runningDesktops: number;
  maxRunningDesktops: number;
};

/**
 * A team's GitHub connection. `available` says whether Workstream's GitHub App is set up at all; `manageUrl`
 * is where on GitHub the installation is changed (which repositories it reaches) or removed.
 */
export type GitHub = {
  available: boolean;
  connected: boolean;
  account: string | null;
  /** `Organization` or `User`, as GitHub says. */
  accountType: string | null;
  connectedAt: string | null;
  connectedByName: string | null;
  manageUrl: string | null;
};

/** A repository the team's GitHub connection reaches. `url` is what an environment clones. */
export type GitHubRepository = { fullName: string; url: string; defaultBranch: string; private: boolean };

/** What GitHub hands back after an installation, which the web app passes on as the person signed in here. */
export type ConnectGitHub = { state: string; code: string | null; installationId: number | null };

export type DesktopApp = { id: string; name: string; description: string; launcher: string; default: boolean };

export type DesktopSize = "small" | "standard" | "large";

/**
 * How an environment's prepared setup stands: its repository cloned and its setup script run once on a
 * machine of its own, whose disk tasks then start from. `because` says why a save is out of date: the
 * environment's `setup` changed, or the `desktop`s are newer than it. `startedAt` is null while a preparation
 * waits for a machine.
 */
export type SetupState = "nothing" | "not_prepared" | "preparing" | "prepared" | "out_of_date" | "failed";

export type EnvironmentSetup = {
  state: SetupState;
  because: "setup" | "desktop" | null;
  preparedAt: string | null;
  commit: string | null;
  bytes: number;
  monthlyUsd: number;
  startedAt: string | null;
  output: string | null;
  failure: string | null;
};

export type Environment = {
  id: string;
  teamId: string;
  name: string;
  apps: string[];
  size: DesktopSize;
  repositoryUrl: string | null;
  repositoryBranch: string | null;
  setupScript: string | null;
  instructions: string | null;
  variableNames: string[];
  updatedAt: string;
  setup: EnvironmentSetup;
};

/** A variable as it is sent: a null value keeps the value the environment already has for that name. */
export type VariableChange = { name: string; value: string | null };

export type WriteEnvironment = {
  name: string;
  apps: string[];
  size: DesktopSize;
  repositoryUrl: string | null;
  repositoryBranch: string | null;
  setupScript: string | null;
  instructions: string | null;
  variables: VariableChange[];
};

/**
 * Where a task's desktop is. `stopping` and `stopped` are what people read as pausing and paused: the machine
 * is released and its disk kept. `archived`: the disk is kept as a copy and the machine is deleted; starting
 * makes a new machine from the copy.
 */
export type DesktopState = "starting" | "ready" | "stopping" | "stopped" | "archiving" | "archived" | "failed" | "deleting";

/**
 * How far a starting desktop has come: its machine is being made or turned on, the machine is on and starting
 * up, or its screen can be watched and the agent has not joined yet.
 */
export type StartStep = "machine" | "boot" | "agent";

export type Task = {
  id: string;
  teamId: string;
  environmentId: string;
  title: string;
  createdBy: string;
  createdByName: string | null;
  createdAt: string;
  state: DesktopState;
  /** Means something only while `state` is `starting`. */
  step: StartStep;
  failure: string | null;
  size: DesktopSize;
  agentWorking: boolean;
  stateChangedAt: string;
};

/** `mine` says the reader wrote it; a person's message with no name was typed at the desktop itself. */
export type Message = { id: number; author: "person" | "agent"; authorName: string | null; mine: boolean; kind: "text" | "activity"; text: string; createdAt: string };

/** What the reader may do with a task: write to its agent, use its desktop's mouse and keyboard, and rename, stop, delete and share it. */
export type TaskAccess = { prompt: boolean; drive: boolean; manage: boolean };

export type Conversation = { messages: Message[]; task: Task; access: TaskAccess };

/** What a task is shared to do: watch it, watch it and write to its agent, or use its desktop too. */
export type ShareRole = "watcher" | "prompter" | "driver";

/** How long a share lasts. */
export type ShareLength = "hour" | "day" | "week" | "never";

/** Someone outside the team a task is shared with. `until` is null when it lasts until it is taken back. */
export type SharedPerson = { userId: string; name: string; email: string | null; role: ShareRole; until: string | null };

/** A task shared with someone who has no account yet. `link` comes only with the answer to making it. */
export type TaskInvitation = { id: string; email: string; role: ShareRole; until: string | null; expiresAt: string; link: string | null };

export type TaskAccessList = { teamId: string; teamName: string; people: SharedPerson[]; invitations: TaskInvitation[] };

/** What sharing made: a grant for someone with an account, or an invitation for someone without. `link` is what to send them. */
export type TaskShared = { person: SharedPerson | null; invitation: TaskInvitation | null; link: string; emailed: boolean };

export type RunningPoint = { at: string; running: number };

export type RunningDesktop = { taskId: string; title: string; teamId: string; teamName: string; size: string; state: string; agentWorking: boolean; since: string };

export type TeamNumbers = {
  id: string;
  name: string;
  people: number;
  tasks: number;
  runningDesktops: number;
  maxRunningDesktops: number;
  modelSpentUsd: number;
  modelAllowanceUsd: number;
  desktopHours: number;
};

/** What machines of one size cost this month for one purpose: `task` (tasks' desktops), `spare` or `prepare` (machines preparing an environment's setup). */
export type MachineCost = { purpose: string; size: string; hours: number; usd: number };

/** What storage of one kind (`disk`, `archive`, `setup`) cost this month over every team: how many are kept now and their gigabytes, the month's GB-months, and their cost. */
export type StorageCost = { kind: string; kept: number; gb: number; gbMonths: number; usd: number };

export type SpareNow = { teamId: string; teamName: string; size: string; state: string; since: string };

/** The machines' time this month by purpose and size, the storage kept by kind, and the spares running now. */
export type MachineCosts = { thisMonth: MachineCost[]; storage: StorageCost[]; spares: SpareNow[] };

/** Starts over the last seven days from one source (`image`, `save`, `spare`, `stopped`, `archive`): how many, and the middle one's seconds to a screen and to the agent. */
export type StartTimes = { source: string; starts: number; screenSeconds: number | null; agentSeconds: number | null };

export type AdminOverview = {
  runningDesktops: number;
  teams: number;
  people: number;
  tasks: number;
  modelSpentUsd: number;
  desktopHours: number;
  running: RunningPoint[];
  desktops: RunningDesktop[];
  teamNumbers: TeamNumbers[];
  machines: MachineCosts;
  starts: StartTimes[];
};

const id = encodeURIComponent;

export const workstream = {
  me: () => apiJson<Me>("/api/me"),
  mySettings: () => apiJson<MySettings>("/api/me/settings"),
  changeMySettings: (settings: MySettings) => apiSend<MySettings>("PUT", "/api/me/settings", settings),
  apps: () => apiJson<DesktopApp[]>("/api/apps"),

  teams: () => apiJson<ListPage<Team>>("/api/teams"),
  team: (teamId: string) => apiJson<Team>(`/api/teams/${id(teamId)}`),
  createTeam: (name: string) => apiSend<Team>("POST", "/api/teams", { name }),
  renameTeam: (teamId: string, name: string) => apiSend<Team>("PUT", `/api/teams/${id(teamId)}`, { name }),
  setDesktopTimes: (teamId: string, pauseAfterMinutes: number, archiveAfterDays: number) =>
    apiSend<Team>("PUT", `/api/teams/${id(teamId)}/desktop-times`, { pauseAfterMinutes, archiveAfterDays }),
  usage: (teamId: string) => apiJson<TeamUsage>(`/api/teams/${id(teamId)}/usage`),

  people: (teamId: string) => apiJson<TeamPerson[]>(`/api/teams/${id(teamId)}/people`),
  setAdmin: (teamId: string, userId: string, admin: boolean) => apiSend<void>("PUT", `/api/teams/${id(teamId)}/people/${id(userId)}/admin`, { admin }),
  removePerson: (teamId: string, userId: string) => apiSend<void>("DELETE", `/api/teams/${id(teamId)}/people/${id(userId)}`),
  invitations: (teamId: string) => apiJson<PendingInvitation[]>(`/api/teams/${id(teamId)}/invitations`),
  invite: (teamId: string, email: string, admin: boolean) => apiSend<PendingInvitation>("POST", `/api/teams/${id(teamId)}/invitations`, { email, admin }),
  revokeInvitation: (teamId: string, invitationId: string) => apiSend<void>("DELETE", `/api/teams/${id(teamId)}/invitations/${id(invitationId)}`),

  github: (teamId: string) => apiJson<GitHub>(`/api/teams/${id(teamId)}/github`),
  githubRepositories: (teamId: string) => apiJson<GitHubRepository[]>(`/api/teams/${id(teamId)}/github/repositories`),
  /** Where on GitHub an administrator installs the app, with a state the API signed for them. */
  startGitHubInstall: (teamId: string) => apiSend<{ url: string }>("POST", `/api/teams/${id(teamId)}/github/install`),
  connectGitHub: (body: ConnectGitHub) => apiSend<{ teamId: string; gitHub: GitHub }>("POST", "/api/github/connect", body),
  disconnectGitHub: (teamId: string) => apiSend<void>("DELETE", `/api/teams/${id(teamId)}/github`),

  environments: (teamId: string) => apiJson<ListPage<Environment>>(`/api/teams/${id(teamId)}/environments`),
  addEnvironment: (teamId: string, environmentId: string, body: WriteEnvironment) =>
    apiSend<Environment>("POST", `/api/teams/${id(teamId)}/environments`, { id: environmentId, ...body }),
  changeEnvironment: (environmentId: string, body: WriteEnvironment) => apiSend<Environment>("PUT", `/api/environments/${id(environmentId)}`, body),
  deleteEnvironment: (environmentId: string) => apiSend<void>("DELETE", `/api/environments/${id(environmentId)}`),
  /** "Prepare again": the environment's setup is done again on a machine of its own, and saved for its tasks. */
  prepareEnvironment: (environmentId: string) => apiSend<Environment>("POST", `/api/environments/${id(environmentId)}/setup`),

  tasks: (teamId: string) => apiJson<ListPage<Task>>(`/api/teams/${id(teamId)}/tasks`),
  /**
   * Someone is in the team's workspace: the API keeps a desktop running for the task they may be about to
   * start, so it starts at once. Nothing comes back, and whether one is made is the API's decision.
   */
  wantSpare: (teamId: string, environmentId?: string) => apiSend<void>("POST", `/api/teams/${id(teamId)}/spare`, { environmentId: environmentId ?? null }),
  startTask: (teamId: string, body: { id: string; environmentId: string; prompt: string; promptKey: string }) =>
    apiSend<Task>("POST", `/api/teams/${id(teamId)}/tasks`, body),
  renameTask: (taskId: string, title: string) => apiSend<Task>("PUT", `/api/tasks/${id(taskId)}`, { title }),
  deleteTask: (taskId: string) => apiSend<void>("DELETE", `/api/tasks/${id(taskId)}`),
  stopDesktop: (taskId: string) => apiSend<Task>("POST", `/api/tasks/${id(taskId)}/stop`),
  startDesktop: (taskId: string) => apiSend<Task>("POST", `/api/tasks/${id(taskId)}/start`),
  interrupt: (taskId: string) => apiSend<void>("POST", `/api/tasks/${id(taskId)}/interrupt`),
  conversation: (taskId: string, after: number) => apiJson<Conversation>(`/api/tasks/${id(taskId)}/messages?after=${after}`),
  say: (taskId: string, text: string, key: string) => apiSend<Message>("POST", `/api/tasks/${id(taskId)}/messages`, { text, key }),

  /** Tasks of other teams shared with the person. */
  sharedTasks: () => apiJson<ListPage<Task>>("/api/shared-tasks"),
  taskAccess: (taskId: string) => apiJson<TaskAccessList>(`/api/tasks/${id(taskId)}/access`),
  shareTask: (taskId: string, email: string, role: ShareRole, expiresIn: ShareLength) =>
    apiSend<TaskShared>("POST", `/api/tasks/${id(taskId)}/access`, { email, role, expiresIn }),
  revokeShare: (taskId: string, userId: string) => apiSend<void>("DELETE", `/api/tasks/${id(taskId)}/access/people/${id(userId)}`),
  withdrawTaskInvitation: (taskId: string, invitationId: string) => apiSend<void>("DELETE", `/api/tasks/${id(taskId)}/access/invitations/${id(invitationId)}`),
  acceptTaskInvitation: (token: string) => apiSend<{ taskId: string }>("POST", "/api/task-invitations/accept", { token }),

  adminOverview: () => apiJson<AdminOverview>("/api/admin/overview"),
  setTeamLimits: (teamId: string, modelAllowanceUsd: number, maxRunningDesktops: number) =>
    apiSend<Team>("PUT", `/api/admin/teams/${id(teamId)}/limits`, { modelAllowanceUsd, maxRunningDesktops }),
};
