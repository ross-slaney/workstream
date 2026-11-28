import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Task } from "@/lib/workstream/api";

// The icons draw nothing here: each is a component that renders nothing.
vi.mock("lucide-react-native", () => {
  const Icon = () => null;
  return { ArrowUp: Icon, Check: Icon, Maximize2: Icon, Minimize2: Icon, MoreHorizontal: Icon, Square: Icon, X: Icon };
});
vi.mock("@/components/theme/theme", () => ({ useTheme: () => ({ colors: { foreground: "#000000", mutedForeground: "#000000", primaryForeground: "#ffffff" } }) }));

import { Chat, elapsedOf } from "../parts";
import type { TaskScreenProps } from "../props";

const ago = (seconds: number) => new Date(Date.now() - seconds * 1000).toISOString();

const task: Task = {
  id: "t", teamId: "team", environmentId: "e", title: "Build the app", createdBy: "u", createdByName: "Ada", createdAt: "2026-11-21T10:00:00Z",
  state: "ready", step: "agent", failure: null, size: "large", agentWorking: true, stateChangedAt: "2026-11-21T10:00:00Z",
};

const chat = (changes: Partial<Task>, props: Partial<TaskScreenProps> = {}) =>
  renderToStaticMarkup(
    <Chat
      task={{ ...task, ...changes }} access={{ prompt: true, drive: true, manage: true }} setup={null} missing={false} entries={[]} draft="" onDraftChange={() => {}} onSend={() => {}}
      onRetry={() => {}} onInterrupt={() => {}} onStartDesktop={() => {}} onStopDesktop={() => {}} onRename={() => {}} onDelete={() => {}} onShare={() => {}}
      onClose={() => {}} busy={false} error={null} desktop={null} desktopShape={4 / 3} {...props}
    />,
  );

describe("the command the agent is running, on the task's screen", () => {
  it("shows the command and how long it has run, under the working line", () => {
    const markup = chat({ running: { text: "./gradlew assembleDebug", since: ago(4 * 60 + 10) } });
    expect(markup).toContain("The agent is working");
    expect(markup).toContain("./gradlew assembleDebug");
    expect(markup).toContain("4 min");
    expect(markup).toContain("Running ./gradlew assembleDebug for 4 min");
  });

  it("shows nothing for a command that has only just started, or between commands", () => {
    expect(chat({ running: { text: "ls", since: ago(1) } })).not.toContain("<code");
    expect(chat({ running: null })).not.toContain("<code");
    expect(chat({})).toContain("The agent is working");
  });

  it("shows nothing once the agent has finished or the desktop is not up", () => {
    const running = { text: "npm test", since: ago(90) };
    expect(chat({ agentWorking: false, running })).not.toContain("npm test");
    expect(chat({ state: "stopped", running })).not.toContain("npm test");
  });

  it("counts seconds, then minutes, then hours and minutes", () => {
    expect(elapsedOf(42)).toEqual({ key: "chat.runningSeconds", values: { seconds: 42 } });
    expect(elapsedOf(60)).toEqual({ key: "chat.runningMinutes", values: { minutes: 1 } });
    expect(elapsedOf(59 * 60 + 59)).toEqual({ key: "chat.runningMinutes", values: { minutes: 59 } });
    expect(elapsedOf(3 * 3600 + 7 * 60)).toEqual({ key: "chat.runningHours", values: { hours: 3, minutes: 7 } });
  });
});
