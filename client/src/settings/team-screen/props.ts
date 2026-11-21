import type { Online } from "@/lib/online";
import type { PendingInvitation, Team, TeamPerson } from "@/lib/workstream/api";

/**
 * A team: its name, who is on it, and inviting someone. Everyone sees the people; an administrator can also
 * rename the team, invite, change who administers it and remove someone. Anyone can leave.
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
};
