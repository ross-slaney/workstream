import { apiJson, apiSend } from "@/lib/api/client";

/*
 * Every call the app makes to the Workstream API, and the shapes it answers with. One function per route
 * (api/WorkstreamApi.*.cs). Controllers hand these to a read hook or call them for an action; nothing else
 * in the app knows a path.
 */

export type ListPage<T> = { data: T[]; nextCursor: string | null };

export type Me = { userId: string; name: string; email: string | null; systemAdmin: boolean };

export type Team = { id: string; name: string; admin: boolean; modelAllowanceUsd: number; maxRunningDesktops: number };

export type TeamPerson = { userId: string; name: string; email: string | null; admin: boolean; joinedAt: string };

/** `link` comes only with the answer to making it; `emailed` says whether Workstream sent it to them as well. */
export type PendingInvitation = { id: string; email: string; admin: boolean; expiresAt: string; link: string | null; emailed: boolean };

export type TeamUsage = {
  since: string;
  modelSpentUsd: number;
  modelAllowanceUsd: number;
  desktopHours: number;
  runningDesktops: number;
  maxRunningDesktops: number;
};

export type DesktopApp = { id: string; name: string; description: string; launcher: string; default: boolean };

export type DesktopSize = "small" | "standard" | "large";

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

export type DesktopState = "starting" | "ready" | "stopping" | "stopped" | "failed" | "deleting";

export type Task = {
  id: string;
  teamId: string;
  environmentId: string;
  title: string;
  createdBy: string;
  createdByName: string | null;
  createdAt: string;
  state: DesktopState;
  failure: string | null;
  size: DesktopSize;
  agentWorking: boolean;
  stateChangedAt: string;
};

/** `mine` says the reader wrote it; a person's message with no name was typed at the desktop itself. */
export type Message = { id: number; author: "person" | "agent"; authorName: string | null; mine: boolean; kind: "text" | "activity"; text: string; createdAt: string };

export type Conversation = { messages: Message[]; task: Task };

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
};

const id = encodeURIComponent;

export const workstream = {
  me: () => apiJson<Me>("/api/me"),
  apps: () => apiJson<DesktopApp[]>("/api/apps"),

  teams: () => apiJson<ListPage<Team>>("/api/teams"),
  team: (teamId: string) => apiJson<Team>(`/api/teams/${id(teamId)}`),
  createTeam: (name: string) => apiSend<Team>("POST", "/api/teams", { name }),
  renameTeam: (teamId: string, name: string) => apiSend<Team>("PUT", `/api/teams/${id(teamId)}`, { name }),
  usage: (teamId: string) => apiJson<TeamUsage>(`/api/teams/${id(teamId)}/usage`),

  people: (teamId: string) => apiJson<TeamPerson[]>(`/api/teams/${id(teamId)}/people`),
  setAdmin: (teamId: string, userId: string, admin: boolean) => apiSend<void>("PUT", `/api/teams/${id(teamId)}/people/${id(userId)}/admin`, { admin }),
  removePerson: (teamId: string, userId: string) => apiSend<void>("DELETE", `/api/teams/${id(teamId)}/people/${id(userId)}`),
  invitations: (teamId: string) => apiJson<PendingInvitation[]>(`/api/teams/${id(teamId)}/invitations`),
  invite: (teamId: string, email: string, admin: boolean) => apiSend<PendingInvitation>("POST", `/api/teams/${id(teamId)}/invitations`, { email, admin }),
  revokeInvitation: (teamId: string, invitationId: string) => apiSend<void>("DELETE", `/api/teams/${id(teamId)}/invitations/${id(invitationId)}`),

  environments: (teamId: string) => apiJson<ListPage<Environment>>(`/api/teams/${id(teamId)}/environments`),
  addEnvironment: (teamId: string, environmentId: string, body: WriteEnvironment) =>
    apiSend<Environment>("POST", `/api/teams/${id(teamId)}/environments`, { id: environmentId, ...body }),
  changeEnvironment: (environmentId: string, body: WriteEnvironment) => apiSend<Environment>("PUT", `/api/environments/${id(environmentId)}`, body),
  deleteEnvironment: (environmentId: string) => apiSend<void>("DELETE", `/api/environments/${id(environmentId)}`),

  tasks: (teamId: string) => apiJson<ListPage<Task>>(`/api/teams/${id(teamId)}/tasks`),
  startTask: (teamId: string, body: { id: string; environmentId: string; prompt: string; promptKey: string }) =>
    apiSend<Task>("POST", `/api/teams/${id(teamId)}/tasks`, body),
  renameTask: (taskId: string, title: string) => apiSend<Task>("PUT", `/api/tasks/${id(taskId)}`, { title }),
  deleteTask: (taskId: string) => apiSend<void>("DELETE", `/api/tasks/${id(taskId)}`),
  stopDesktop: (taskId: string) => apiSend<Task>("POST", `/api/tasks/${id(taskId)}/stop`),
  startDesktop: (taskId: string) => apiSend<Task>("POST", `/api/tasks/${id(taskId)}/start`),
  interrupt: (taskId: string) => apiSend<void>("POST", `/api/tasks/${id(taskId)}/interrupt`),
  conversation: (taskId: string, after: number) => apiJson<Conversation>(`/api/tasks/${id(taskId)}/messages?after=${after}`),
  say: (taskId: string, text: string, key: string) => apiSend<Message>("POST", `/api/tasks/${id(taskId)}/messages`, { text, key }),

  adminOverview: () => apiJson<AdminOverview>("/api/admin/overview"),
  setTeamLimits: (teamId: string, modelAllowanceUsd: number, maxRunningDesktops: number) =>
    apiSend<Team>("PUT", `/api/admin/teams/${id(teamId)}/limits`, { modelAllowanceUsd, maxRunningDesktops }),
};
