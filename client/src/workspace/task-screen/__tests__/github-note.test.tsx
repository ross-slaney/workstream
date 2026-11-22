import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Task } from "@/lib/workstream/api";

vi.mock("lucide-react-native", () => {
  const Icon = () => null;
  return { ArrowUp: Icon, Check: Icon, Maximize2: Icon, Minimize2: Icon, MoreHorizontal: Icon, Square: Icon, X: Icon };
});
vi.mock("@/components/theme/theme", () => ({ useTheme: () => ({ colors: { foreground: "#000000", mutedForeground: "#000000", primaryForeground: "#ffffff" } }) }));

import { Chat } from "../parts";
import type { TaskScreenProps } from "../props";

const task: Task = {
  id: "t", teamId: "team", environmentId: "e", title: "Fix the build", createdBy: "u", createdByName: "Grace", createdAt: "2026-11-21T10:00:00Z",
  state: "ready", step: "agent", failure: null, size: "standard", agentWorking: false, stateChangedAt: "2026-11-21T10:00:00Z",
};

const chat = (changes: Partial<TaskScreenProps>) =>
  renderToStaticMarkup(
    <Chat
      task={task} access={{ prompt: true, drive: true, manage: true }} setup={null} missing={false} entries={[]} draft="" onDraftChange={() => {}} onSend={() => {}}
      onRetry={() => {}} onInterrupt={() => {}} onStartDesktop={() => {}} onStopDesktop={() => {}} onRename={() => {}} onDelete={() => {}} onShare={() => {}}
      onClose={() => {}} busy={false} error={null} desktop={null} desktopShape={4 / 3} onSignInWithGitHub={() => {}} {...changes}
    />,
  );

describe("a task whose desktop cannot reach GitHub", () => {
  it("tells the person who started it, with a way to sign in with GitHub", () => {
    const markup = chat({ github: { signInNeeded: true, startedByYou: true, startedByName: "Grace" } });
    expect(markup).toContain("This desktop can&#x27;t reach GitHub as you, because you haven&#x27;t signed in to Workstream with GitHub.");
    expect(markup).toContain("Sign out and sign in with GitHub");
  });

  it("tells anyone else who started it, with nothing for them to press", () => {
    const markup = chat({ github: { signInNeeded: true, startedByYou: false, startedByName: "Grace Hopper" } });
    expect(markup).toContain("Grace Hopper started this task and hasn&#x27;t signed in to Workstream with GitHub");
    expect(markup).not.toContain("Sign out and sign in with GitHub");
  });

  it("says nothing when the desktop reaches GitHub, or before it is known", () => {
    expect(chat({ github: { signInNeeded: false, startedByYou: true, startedByName: "Grace" } })).not.toContain("GitHub");
    expect(chat({ github: null })).not.toContain("GitHub");
  });
});
