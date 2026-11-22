import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Task, TaskSetup } from "@/lib/workstream/api";

// The icons draw nothing here: each is a component that renders nothing.
vi.mock("lucide-react-native", () => {
  const Icon = () => null;
  return { ArrowUp: Icon, Check: Icon, Maximize2: Icon, Minimize2: Icon, MoreHorizontal: Icon, Square: Icon, X: Icon };
});
vi.mock("@/components/theme/theme", () => ({ useTheme: () => ({ colors: { foreground: "#000000", mutedForeground: "#000000", primaryForeground: "#ffffff" } }) }));

import { Chat } from "../parts";
import type { TaskScreenProps } from "../props";

const task: Task = {
  id: "t", teamId: "team", environmentId: "e", title: "Run the stack", createdBy: "u", createdByName: "Ada", createdAt: "2026-11-21T10:00:00Z",
  state: "ready", step: "agent", failure: null, size: "large", agentWorking: true, stateChangedAt: "2026-11-21T10:00:00Z",
};

const setup = (changes: Partial<TaskSetup>): TaskSetup => ({
  state: "running", exit: null, output: "npm ci\nadded 2041 packages in 3m", startedAt: new Date(Date.now() - 4 * 60_000).toISOString(), endedAt: null, limitMinutes: 45, ...changes,
});

const chat = (changes: Partial<TaskScreenProps>) =>
  renderToStaticMarkup(
    <Chat
      task={task} access={{ prompt: true, drive: true, manage: true }} setup={null} missing={false} entries={[]} draft="" onDraftChange={() => {}} onSend={() => {}}
      onRetry={() => {}} onInterrupt={() => {}} onStartDesktop={() => {}} onStopDesktop={() => {}} onRename={() => {}} onDelete={() => {}} onShare={() => {}}
      onClose={() => {}} busy={false} error={null} desktop={null} desktopShape={4 / 3} {...changes}
    />,
  );

describe("the setup script on the task's screen", () => {
  it("says nothing when the desktop ran none, or it ended well", () => {
    expect(chat({})).not.toContain("setup script");
    expect(chat({ setup: setup({ state: "done", exit: 0 }) })).not.toContain("setup script");
  });

  it("says it is running, for how long of how long, with its last lines, in place of the agent's working line", () => {
    const markup = chat({ setup: setup({}) });
    expect(markup).toContain("Running the setup script");
    expect(markup).toContain("4 of 45 min");
    expect(markup).toContain("added 2041 packages in 3m");
    expect(markup).not.toContain("The agent is working");
  });

  it("says so when it has written nothing yet", () => {
    expect(chat({ setup: setup({ output: null }) })).toContain("It has written nothing yet.");
  });

  it("says plainly that it failed, with its exit code and its last lines", () => {
    const markup = chat({ task: { ...task, agentWorking: false }, setup: setup({ state: "failed", exit: 2, output: "npm ERR! code ERESOLVE", endedAt: new Date().toISOString() }) });
    expect(markup).toContain("The setup script stopped with an error (exit 2).");
    expect(markup).toContain("npm ERR! code ERESOLVE");
    expect(markup).toContain('role="alert"');
  });

  it("says plainly that it was stopped for running too long, even after the desktop has stopped", () => {
    const markup = chat({ task: { ...task, state: "stopped", agentWorking: false }, setup: setup({ state: "timed_out", exit: 124, endedAt: new Date().toISOString() }) });
    expect(markup).toContain("The setup script did not finish in 45 minutes and was stopped.");
  });

  it("does not say it is running once the desktop is not", () => {
    expect(chat({ task: { ...task, state: "stopped", agentWorking: false }, setup: setup({}) })).not.toContain("Running the setup script");
  });
});
