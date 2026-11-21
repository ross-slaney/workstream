import type { ReactNode } from "react";

/**
 * The screens: the tasks a person has open, side by side, four at most. Each screen is drawn by its own
 * controller, handed in as a slot, so this view only decides where the screens go and what a task dragged
 * from the sidebar does.
 */
export type WorkspaceScreenProps = {
  /** The tasks on screen, in order. */
  open: string[];
  /** Draws the screen for a task: its conversation and its desktop. */
  renderScreen: (taskId: string) => ReactNode;
  /** A task was dropped: on the screen at `onto`, or between screens (null). */
  onDropTask: (taskId: string, onto: number | null) => void;
  onNewTask: () => void;
};
