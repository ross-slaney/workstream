import type { Online } from "@/lib/online";
import type { GitHub, GitHubRepository, PendingInvitation, Team, TeamPerson } from "@/lib/workstream/api";

/**
 * A team: its name, who is on it, inviting someone, and its GitHub connection. Everyone sees the people and
 * the connection with the repositories it reaches; an administrator can also rename the team, invite, change
 * who administers it, remove someone, and connect or disconnect GitHub. Anyone can leave. Below, the
 * person's own switch for the emails about tasks.
 */
export type TeamScreenProps = {
  team: Team | null;
  people: Online<TeamPerson[]>;
  github: Online<GitHub>;
  /** What the connection reaches, read from GitHub. Null while the team is not connected. */
  githubRepositories: Online<GitHubRepository[]> | null;
  onRetryGitHub: () => void;
  /** Leave for GitHub to install Workstream's app on an account. */
  onConnectGitHub: () => void;
  onDisconnectGitHub: () => void;
  /** Invitations still waiting. Empty for someone who is not an administrator. */
  invitations: PendingInvitation[];
  /** The person looking at the page. */
  meId: string;
  /** The invitation just made, with its link to pass on. */
  invited: PendingInvitation | null;
  /** Whether the person is emailed when a task they started or wrote in is finished or needs them. Null while it is read. */
  taskEmails: boolean | null;
  busy: boolean;
  error: string | null;
  onRetry: () => void;
  onRename: (name: string) => void;
  onInvite: (email: string, admin: boolean) => void;
  onDismissInvited: () => void;
  onWithdraw: (invitationId: string) => void;
  onSetAdmin: (userId: string, admin: boolean) => void;
  /** Remove someone, or leave when it is the person themselves. */
  onRemove: (userId: string) => void;
  onTaskEmails: (on: boolean) => void;
};
