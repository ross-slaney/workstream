import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Environment } from "@/lib/workstream/api";

// The icons draw nothing here: each is a component that renders nothing.
vi.mock("lucide-react-native", () => {
  const Icon = () => null;
  return { ArrowUp: Icon, Check: Icon, ChevronDown: Icon, Maximize2: Icon, Minimize2: Icon, MoreHorizontal: Icon, Square: Icon, X: Icon };
});
vi.mock("@/components/theme/theme", () => ({ useTheme: () => ({ colors: new Proxy({}, { get: () => "#000000" }) }) }));

import { NewTaskDialog } from "../dialogs";

/* One form, two ways to start: a task the agent starts on, or the desktop alone for the person to work on. */

const environment = { id: "e", name: "Shop" } as unknown as Environment;
const dialog = (environments: Environment[] = [environment]) =>
  renderToStaticMarkup(<NewTaskDialog environments={environments} busy={false} error={null} onStart={() => {}} onOpen={() => {}} onClose={() => {}} />);

/** The one button with this label, as it was drawn. */
const button = (markup: string, label: string) =>
  markup.split("<button").map((piece) => piece.slice(0, piece.indexOf("</button>"))).find((piece) => piece.includes(label)) ?? "";

describe("starting work on a new desktop", () => {
  it("offers a task and the desktop alone", () => {
    const markup = dialog();
    expect(markup).toContain("Start task");
    expect(markup).toContain("Open desktop");
    expect(markup).toContain("Or open the desktop with nothing asked");
  });

  it("opens a desktop with nothing written, where a task needs to say what the agent should do", () => {
    const markup = dialog();
    expect(button(markup, "Open desktop")).not.toContain(" disabled=\"\"");
    expect(button(markup, "Start task")).toContain(" disabled=\"\"");
  });

  it("opens nothing while the team has no environment", () => {
    const markup = dialog([]);
    expect(button(markup, "Open desktop")).toContain(" disabled=\"\"");
    expect(markup).toContain("Add an environment first.");
  });
});
