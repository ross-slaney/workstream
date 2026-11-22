import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { DesktopState, Task, TaskAccess } from "@/lib/workstream/api";
import { Chat, DesktopPane } from "../parts";
import type { TaskScreenProps } from "../props";

// The icons and the theme come from React Native packages, which Node cannot load; the views only need them to be there.
vi.mock("lucide-react-native", () => {
  const Icon = () => null;
  return { ArrowUp: Icon, Check: Icon, Maximize2: Icon, Minimize2: Icon, MoreHorizontal: Icon, Square: Icon, X: Icon };
});
vi.mock("@/components/theme/theme", () => ({ useTheme: () => ({ colors: new Proxy({}, { get: () => "#000000" }) }) }));

/*
 * What the pane shows for a desktop that is not running. A paused or archived one says so, with no stream and
 * no error, and is one button for whoever may use the desktop: clicking it is what brings the desktop back.
 */

const nothing = () => {};
const task = (state: DesktopState): Task => ({
  id: "t", teamId: "team", environmentId: "e", title: "Check the build", createdBy: "u", createdByName: "Ada", createdAt: "2026-11-21T10:00:00Z",
  state, step: "agent", failure: null, size: "standard", agentWorking: false, stateChangedAt: "2026-11-21T10:00:00Z",
});
const team: TaskAccess = { prompt: true, drive: true, manage: true };
const prompter: TaskAccess = { prompt: true, drive: false, manage: false };
const watcher: TaskAccess = { prompt: false, drive: false, manage: false };
const screen = (state: DesktopState, access: TaskAccess, more: Partial<TaskScreenProps> = {}): TaskScreenProps => ({
  task: task(state), access, missing: false, entries: [], draft: "", onDraftChange: nothing, onSend: nothing, onRetry: nothing, onInterrupt: nothing,
  onStartDesktop: nothing, onStopDesktop: nothing, onRename: nothing, onDelete: nothing, onShare: nothing, onClose: nothing, busy: false, error: null,
  desktop: null, desktopShape: 4 / 3, ...more,
});
const pane = (state: DesktopState, access: TaskAccess, more?: Partial<TaskScreenProps>) => renderToStaticMarkup(<DesktopPane {...screen(state, access, more)} />);
const chat = (state: DesktopState, access: TaskAccess, more?: Partial<TaskScreenProps>) => renderToStaticMarkup(<Chat {...screen(state, access, more)} />);

describe("a paused desktop's pane", () => {
  it("is one button that resumes it, for whoever may use the desktop", () => {
    const page = pane("stopped", team);
    expect(page).toMatch(/^<button type="button"/);
    expect(page).toContain("The desktop is paused");
    expect(page).toContain("Click here or send a message");
    expect(page).toContain("Resume");
    expect(page).not.toContain("Connecting");
  });

  it("tells someone who may only write to its agent that a message resumes it, and is no button", () => {
    const page = pane("stopped", prompter);
    expect(page).not.toContain("<button");
    expect(page).toContain("Send a message, and it resumes");
  });

  it("tells someone who only watches who can resume it", () => {
    const page = pane("stopped", watcher);
    expect(page).not.toContain("<button");
    expect(page).toContain("Someone who can write to its agent or use its desktop can resume it");
  });

  it("does not answer a click while the start is under way", () => {
    expect(pane("stopped", team, { busy: true })).toContain("disabled");
  });
});

describe("an archived desktop's pane", () => {
  it("says nothing is lost and brings it back at a click", () => {
    const page = pane("archived", team);
    expect(page).toMatch(/^<button type="button"/);
    expect(page).toContain("The desktop is archived");
    expect(page).toContain("Nothing is lost");
    expect(page).toContain("Bring it back");
  });

  it("says it is being archived while its disk is copied, with nothing to click", () => {
    const page = pane("archiving", team);
    expect(page).toContain("Archiving the desktop");
    expect(page).not.toContain("<button");
  });
});

describe("the other states of the pane", () => {
  it("draws the live desktop while there is one", () => {
    expect(pane("ready", team, { desktop: <i>the stream</i> })).toContain("<i>the stream</i>");
  });

  it("says it is pausing while the machine is released", () => {
    expect(pane("stopping", team)).toContain("Pausing the desktop");
  });

  it("lets whoever may use a failed desktop start it again", () => {
    expect(pane("failed", team)).toContain("Start it again");
    expect(pane("failed", prompter)).not.toContain("Start it again");
  });
});

describe("the line above the message box", () => {
  it("says a message resumes a paused desktop and brings back an archived one", () => {
    expect(chat("stopped", team)).toContain("The desktop is paused. Send a message and it resumes");
    expect(chat("archived", team)).toContain("The desktop is archived. Send a message and it comes back");
  });

  it("shows why a message to a paused task was refused, in the server's words", () => {
    expect(chat("stopped", team, { error: "Your team can run 2 desktops at once. Pause one to start another." })).toContain("Pause one to start another.");
  });
});
