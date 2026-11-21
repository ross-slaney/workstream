import type { ReactNode } from "react";
import type { Task } from "@/lib/workstream/api";

/** One entry in the conversation as the screen draws it. */
export type ChatEntry = {
  /** Stable across reads: the message's id, or the key of one still being sent. */
  id: string;
  author: "person" | "agent";
  /** Who wrote it, when the server knows a name: a teammate. Null for the agent and for someone typing at the desktop. */
  authorName: string | null;
  kind: "text" | "activity";
  text: string;
  /** Written by the person looking at the screen. */
  mine: boolean;
  /** Not on the server yet, or refused. */
  pending?: "sending" | "failed";
};

/**
 * One screen: a task's conversation beside its desktop. The view draws what it is given and says what the
 * person did; the controller (components/app/task-screen.tsx) reads the conversation and calls the API.
 */
export type TaskScreenProps = {
  /** Null until the first read answers. */
  task: Task | null;
  /** The task is not there any more: deleted here or by a teammate. */
  missing: boolean;
  entries: ChatEntry[];
  draft: string;
  onDraftChange: (text: string) => void;
  onSend: () => void;
  /** Send again a message that was refused. */
  onRetry: (entryId: string) => void;
  /** Ask the agent to stop what it is doing. */
  onInterrupt: () => void;
  onStartDesktop: () => void;
  onStopDesktop: () => void;
  onRename: (title: string) => void;
  onDelete: () => void;
  /** Take this screen off the screens. */
  onClose: () => void;
  /** An action on the task is under way. */
  busy: boolean;
  /** Why the last action was refused, in the server's words. */
  error: string | null;
  /** The live desktop, which the controller draws once the desktop is ready. */
  desktop: ReactNode;
};
