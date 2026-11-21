import type { ReactNode } from "react";
import type { Online } from "@/lib/online";
import type { DesktopState, Team } from "@/lib/workstream/api";

/** A task in the sidebar's list. */
export type ShellTask = {
  id: string;
  title: string;
  state: DesktopState;
  working: boolean;
  /** Who started it, when it was someone else. */
  by: string | null;
  /** On screen now. */
  open: boolean;
};

export type ShellPlace = "tasks" | "environments" | "team" | "usage";

/**
 * The frame around every signed-in page of a team: its tasks down the side, the ways to the team's other
 * pages, and the page itself. A task in the list can be opened by itself, opened beside the others, or
 * dragged onto the screens.
 */
export type AppShellProps = {
  /** Null while the team is being read. */
  team: Team | null;
  teams: Team[];
  person: { name: string; email: string | null; systemAdmin: boolean };
  place: ShellPlace;
  tasks: Online<ShellTask[]>;
  onRetryTasks: () => void;
  /** Show this task by itself. */
  onOpenTask: (taskId: string) => void;
  /** Add this task beside the ones on screen. */
  onOpenBeside: (taskId: string) => void;
  onNewTask: () => void;
  onSwitchTeam: (teamId: string) => void;
  onCreateTeam: () => void;
  onGo: (place: ShellPlace) => void;
  onAdmin: () => void;
  onSignOut: () => void;
  /** The page. */
  children: ReactNode;
};
