import type { Online } from "@/lib/online";
import type { PendingInvitation, Team, TeamPerson } from "@/lib/workstream/api";

/**
 * A team: its name, who is on it, inviting someone, and when its desktops are paused and archived. Everyone
 * sees the people and the two times; an administrator can also rename the team, invite, change who
 * administers it, remove someone, and change the two times. Anyone can leave. Below, the person's own switch
 * for the emails about tasks.
 */
export type TeamScreenProps = {
  team: Team | null;
  people: Online<TeamPerson[]>;
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
  /** Change when the team's desktops are paused (minutes without use) and archived (days paused). */
  onDesktopTimes: (pauseAfterMinutes: number, archiveAfterDays: number) => void;
  onInvite: (email: string, admin: boolean) => void;
  onDismissInvited: () => void;
  onWithdraw: (invitationId: string) => void;
  onSetAdmin: (userId: string, admin: boolean) => void;
  /** Remove someone, or leave when it is the person themselves. */
  onRemove: (userId: string) => void;
  onTaskEmails: (on: boolean) => void;
};
