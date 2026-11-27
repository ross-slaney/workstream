import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Task } from "@/lib/workstream/api";

// The icons draw nothing here: each is a component that renders nothing.
vi.mock("lucide-react-native", () => {
  const Icon = () => null;
  return { ArrowUp: Icon, Check: Icon, Maximize2: Icon, Minimize2: Icon, MoreHorizontal: Icon, Square: Icon, X: Icon };
});
vi.mock("@/components/theme/theme", () => ({ useTheme: () => ({ colors: { foreground: "#000000", mutedForeground: "#000000", primaryForeground: "#ffffff" } }) }));

import { Chat } from "../parts";
import { rowsOf } from "../pictures";
import type { ChatEntry, TaskScreenProps } from "../props";

const task: Task = {
  id: "t", teamId: "team", environmentId: "e", title: "Change the heading", createdBy: "u", createdByName: "Ada", createdAt: "2026-11-21T10:00:00Z",
  state: "ready", step: "agent", failure: null, size: "large", agentWorking: false, stateChangedAt: "2026-11-21T10:00:00Z",
};

const entry = (id: string, kind: ChatEntry["kind"], text: string, picture?: string | null): ChatEntry => ({ id, author: "agent", authorName: null, kind, text, mine: false, picture });

const chat = (changes: Partial<TaskScreenProps>) =>
  renderToStaticMarkup(
    <Chat
      task={task} access={{ prompt: true, drive: true, manage: true }} setup={null} missing={false} entries={[]} draft="" onDraftChange={() => {}} onSend={() => {}}
      onRetry={() => {}} onInterrupt={() => {}} onStartDesktop={() => {}} onStopDesktop={() => {}} onRename={() => {}} onDelete={() => {}} onShare={() => {}}
      onClose={() => {}} busy={false} error={null} desktop={null} desktopShape={4 / 3} {...changes}
    />,
  );

describe("pictures in the conversation", () => {
  it("puts pictures that follow each other in one row", () => {
    const rows = rowsOf([
      entry("1", "activity", "Ran surfaces show"),
      entry("2", "picture", "The browser"),
      entry("3", "picture", "The iPhone"),
      entry("4", "text", "The heading is changed on both."),
      entry("5", "picture", "The browser"),
    ]);
    expect(rows.map((row) => (row.kind === "pictures" ? row.pictures.map((picture) => picture.id) : row.entry.id))).toEqual(["1", ["2", "3"], "4", ["5"]]);
  });

  it("draws each picture with what it shows, and a place for one still being read", () => {
    const markup = chat({ entries: [entry("2", "picture", "The browser", "blob:browser"), entry("3", "picture", "The iPhone", null)] });
    expect(markup).toContain('src="blob:browser"');
    expect(markup).toContain('alt="The browser"');
    expect(markup).toContain('href="blob:browser"');
    expect(markup).toContain("The iPhone");
    expect(markup.match(/<img/g)).toHaveLength(1);
  });
});
