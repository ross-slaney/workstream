import type { DesktopState, TaskAccess } from "@/lib/workstream/api";

/**
 * A desktop that is not running and can come back: paused (its machine released, its disk kept) or archived
 * (its disk kept as a copy, its machine deleted). `resume` is how the person looking at it brings it back:
 * by clicking the pane when they may use the desktop, by writing when they may only write to its agent, or
 * not at all when they only watch. Having the pane open never brings it back.
 */
export type Resting = { kind: "paused" | "archived"; resume: "click" | "message" | "none" };

export function restingOf(state: DesktopState, access: TaskAccess | null): Resting | null {
  const kind = state === "stopped" ? "paused" : state === "archived" ? "archived" : null;
  if (!kind) return null;
  return { kind, resume: access?.drive ? "click" : access?.prompt ? "message" : "none" };
}

/** Where the next message stands with the desktop, for the line above the box: null when the agent simply reads it. */
export type ChatNote = "waitingForDesktop" | "agentStarting" | "desktopPausing" | "desktopPaused" | "desktopArchiving" | "desktopArchived" | "desktopFailed";

export function chatNoteOf(state: DesktopState, step: "machine" | "boot" | "agent"): ChatNote | null {
  switch (state) {
    case "starting":
      return step === "agent" ? "agentStarting" : "waitingForDesktop";
    case "stopping":
      return "desktopPausing";
    case "stopped":
      return "desktopPaused";
    case "archiving":
      return "desktopArchiving";
    case "archived":
      return "desktopArchived";
    case "failed":
      return "desktopFailed";
    default:
      return null;
  }
}
