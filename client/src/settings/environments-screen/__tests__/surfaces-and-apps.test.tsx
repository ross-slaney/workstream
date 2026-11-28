import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { DesktopApp, Environment } from "@/lib/workstream/api";

vi.mock("lucide-react-native", () => {
  const Icon = () => null;
  return { Plus: Icon, X: Icon };
});
vi.mock("@/components/theme/theme", () => ({ useTheme: () => ({ colors: new Proxy({}, { get: () => "#000000" }) }) }));

import { countKinds, EnvironmentsPage } from "../parts";
import type { EnvironmentDraft, EnvironmentsScreenProps } from "../props";

/* What a desktop comes with is two lists on the screen: the surfaces its product runs on, and the applications people work with. */

const nothing = () => {};
const catalog: DesktopApp[] = [
  { id: "chrome", name: "Chrome", description: "The web browser.", launcher: "chromium.desktop", default: true, kind: "surface" },
  { id: "vscode", name: "VS Code", description: "The code editor.", launcher: "code.desktop", default: true, kind: "app" },
  { id: "terminal", name: "Terminal", description: "A command line.", launcher: "terminal.desktop", default: true, kind: "app" },
  { id: "android", name: "Android phone", description: "An Android phone on the desktop.", launcher: "android.desktop", default: false, kind: "surface" },
  { id: "iphone", name: "iPhone", description: "An iPhone simulator on the desktop.", launcher: "iphone.desktop", default: false, kind: "surface" },
];
const environment = (apps: string[], name = "Web app"): Environment => ({
  id: name, teamId: "team", name, apps, size: "standard", repositoryUrl: null, repositoryBranch: null, setupScript: null, instructions: null, variableNames: [],
  updatedAt: "2026-11-27T10:00:00Z", setup: { state: "nothing" } as Environment["setup"], fromFile: false,
});
const draft: EnvironmentDraft = {
  name: "Web app", apps: ["chrome", "android", "vscode"], size: "standard", repositoryUrl: "", repositoryBranch: "", setupScript: "", instructions: "", variables: [], fromFile: false,
};

const page = (options: { environments?: Environment[]; editor?: boolean; apps?: DesktopApp[] }) =>
  renderToStaticMarkup(
    <EnvironmentsPage
      environments={{ kind: "ready", value: options.environments ?? [] }}
      apps={options.apps ?? catalog}
      picker={{
        repositories: { kind: "loading" }, branches: null, moreBranches: false, reading: { kind: "none" },
        onPick: nothing, onBranch: nothing, onReadAgain: nothing, onSignInWithGitHub: nothing, onRetry: nothing,
      }}
      onRetry={nothing} onAdd={nothing} onEdit={nothing}
      editor={options.editor ? ({ id: null, draft, busy: false, error: null, setup: null, preparing: false, prepareError: null, file: null } satisfies EnvironmentsScreenProps["editor"]) : null}
      onChangeDraft={nothing} onSave={nothing} onDelete={nothing} onPrepare={nothing} onSaveFile={nothing} onCloseEditor={nothing}
    />,
  );

/** The checkboxes drawn inside the group with this legend, by their label, with whether each is ticked. */
function group(drawn: string, legend: string) {
  const start = drawn.indexOf(`>${legend}</legend>`);
  expect(start).toBeGreaterThan(-1);
  const section = drawn.slice(start, drawn.indexOf("</fieldset>", start));
  return catalog.filter((app) => section.includes(app.name)).map((app) => app.id);
}

describe("an environment's row", () => {
  it("counts its surfaces and its apps apart", () => {
    expect(countKinds(["chrome", "android", "iphone", "vscode", "terminal"], catalog)).toEqual({ surfaces: 3, apps: 2 });
    const drawn = page({ environments: [environment(["chrome", "android", "iphone", "vscode", "terminal"])] });
    expect(drawn).toContain("3 surfaces · 2 apps");
  });

  it("says one of each in the singular, and leaves out a kind it has none of", () => {
    // The row's line of facts, whole: nothing before or after the counts.
    expect(page({ environments: [environment(["chrome", "terminal"])] })).toContain(">1 surface · 1 app</span>");
    expect(page({ environments: [environment(["vscode", "terminal"])] })).toContain(">2 apps</span>");
    expect(page({ environments: [environment(["chrome"])] })).toContain(">1 surface</span>");
  });

  it("counts everything as apps until the catalog is read", () => {
    expect(countKinds(["chrome", "vscode"], [])).toEqual({ surfaces: 0, apps: 2 });
  });
});

describe("the environment form", () => {
  it("lists surfaces and applications as two groups, each saying what it is for", () => {
    const drawn = page({ editor: true });
    expect(drawn).toContain("Where your product runs. You and the agent see it and use it there.");
    expect(drawn).toContain("What you and the agent work with. Each one gets a button on the desktop");
    expect(group(drawn, "Surfaces")).toEqual(["chrome", "android", "iphone"]);
    expect(group(drawn, "Applications")).toEqual(["vscode", "terminal"]);
    expect(drawn.indexOf(">Surfaces</legend>")).toBeLessThan(drawn.indexOf(">Applications</legend>"));
  });

  it("leaves out a group the catalog has nothing for", () => {
    const drawn = page({ editor: true, apps: catalog.filter((app) => app.kind === "app") });
    expect(drawn).not.toContain(">Surfaces</legend>");
    expect(drawn).toContain(">Applications</legend>");
  });
});
