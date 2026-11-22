import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { GitHubRepositories } from "@/lib/workstream/api";

vi.mock("lucide-react-native", () => {
  const Icon = () => null;
  return { Plus: Icon, X: Icon };
});
vi.mock("@/components/theme/theme", () => ({ useTheme: () => ({ colors: new Proxy({}, { get: () => "#000000" }) }) }));

import { EnvironmentsPage } from "../parts";
import type { EnvironmentDraft, EnvironmentsScreenProps, RepositoryFileOffer, RepositoryReading } from "../props";

/* The environment form's repository: picked from the person's GitHub, read for workstream.yml, and saved back as workstream.yml. */

const nothing = () => {};
const draft = (changes: Partial<EnvironmentDraft> = {}): EnvironmentDraft => ({
  name: "Web app", apps: ["chrome", "vscode", "terminal"], size: "standard", repositoryUrl: "", repositoryBranch: "", setupScript: "", instructions: "",
  variables: [], fromFile: false, ...changes,
});
const github = (changes: Partial<GitHubRepositories> = {}): GitHubRepositories => ({
  available: true, signedIn: true, more: false,
  repositories: [
    { fullName: "acme/web", url: "https://github.com/acme/web", defaultBranch: "main", private: true, pushedAt: "2026-11-20T10:00:00Z" },
    { fullName: "acme/api", url: "https://github.com/acme/api", defaultBranch: "main", private: false, pushedAt: null },
  ],
  ...changes,
});

const page = (options: {
  draft?: EnvironmentDraft; repositories?: EnvironmentsScreenProps["picker"]["repositories"]; reading?: RepositoryReading; branches?: string[] | null;
  moreBranches?: boolean; file?: RepositoryFileOffer | null; id?: string | null;
}) =>
  renderToStaticMarkup(
    <EnvironmentsPage
      environments={{ kind: "ready", value: [] }}
      apps={[{ id: "chrome", name: "Chrome", description: "The web browser.", launcher: "chromium.desktop", default: true }]}
      picker={{
        repositories: options.repositories ?? { kind: "ready", value: github() }, branches: options.branches ?? null, moreBranches: options.moreBranches ?? false, reading: options.reading ?? { kind: "none" },
        onPick: nothing, onBranch: nothing, onReadAgain: nothing, onSignInWithGitHub: nothing, onRetry: nothing,
      }}
      onRetry={nothing} onAdd={nothing} onEdit={nothing}
      editor={{ id: options.id ?? null, draft: options.draft ?? draft(), busy: false, error: null, setup: null, preparing: false, prepareError: null, file: options.file ?? null }}
      onChangeDraft={nothing} onSave={nothing} onDelete={nothing} onPrepare={nothing} onSaveFile={nothing} onCloseEditor={nothing}
    />,
  );

const picked = draft({ repositoryUrl: "https://github.com/acme/web", repositoryBranch: "main" });

describe("picking the repository", () => {
  it("lists the person's repositories to find one by name", () => {
    const drawn = page({});
    expect(drawn).toContain("Find a repository");
    expect(drawn).toContain("acme/web");
    expect(drawn).toContain("acme/api");
    expect(drawn).toContain("Type an address instead");
    expect(drawn).not.toContain('placeholder="https://github.com/acme/app"');
  });

  it("says it is reading them, and when there are more than it read", () => {
    expect(page({ repositories: { kind: "loading" } })).toContain("Reading your repositories on GitHub");
    expect(page({ repositories: { kind: "ready", value: github({ more: true }) } })).toContain("Only your 2 most recently pushed repositories are listed.");
  });

  it("asks someone who did not sign in with GitHub to, and lets them type an address", () => {
    const drawn = page({ repositories: { kind: "ready", value: github({ signedIn: false, repositories: [] }) } });
    expect(drawn).toContain("Sign in with GitHub to pick from your repositories.");
    expect(drawn).toContain("Sign out and sign in with GitHub");
    expect(drawn).toContain('placeholder="https://github.com/acme/app"');
  });

  it("is only an address where this Workstream has no sign-in with GitHub", () => {
    const drawn = page({ repositories: { kind: "ready", value: github({ available: false, signedIn: false, repositories: [] }) } });
    expect(drawn).not.toContain("Sign in with GitHub");
    expect(drawn).toContain('placeholder="https://github.com/acme/app"');
  });

  it("shows the picked repository with its branches", () => {
    const drawn = page({ draft: picked, branches: ["main", "develop"] });
    expect(drawn).toContain("acme/web");
    expect(drawn).toContain("Change");
    expect(drawn).toMatch(/<option value="develop"/);
    expect(drawn).not.toContain("Find a repository");
  });

  it("takes a typed branch, offering the ones it read, when the repository has more than it lists", () => {
    const drawn = page({ draft: { ...picked, repositoryBranch: "workstream-yml" }, branches: ["main", "develop"], moreBranches: true });
    expect(drawn).toContain("This repository has more branches than Workstream lists.");
    expect(drawn).toMatch(/<input[^>]*value="workstream-yml"/);
    expect(drawn).toMatch(/<datalist[^>]*><option value="workstream-yml"><\/option><option value="main">/);
  });
});

describe("what Workstream does with the picked repository", () => {
  it("looks for workstream.yml, then says the form was filled from it", () => {
    expect(page({ draft: picked, reading: { kind: "reading", repository: "acme/web" } })).toContain("Looking for workstream.yml in acme/web");
    expect(page({ draft: { ...picked, fromFile: true }, reading: { kind: "file", branch: "main" } })).toContain("Filled from workstream.yml on main.");
    expect(page({ draft: { ...picked, fromFile: true } })).toContain("Its settings are workstream.yml in the repository.");
    expect(page({ draft: picked, reading: { kind: "fileChanged" } })).toContain("no longer follows the file");
  });

  it("names the file's mistakes, to read it again once they are fixed", () => {
    const drawn = page({ draft: picked, reading: { kind: "badFile", errors: ["Line 1: size is small, standard or large, not huge."] } });
    expect(drawn).toContain("has mistakes, so nothing was filled from it");
    expect(drawn).toContain("Line 1: size is small, standard or large, not huge.");
    expect(drawn).toContain("Read workstream.yml again");
  });

  it("shows how to write the file when the repository has none, and never guesses a setup", () => {
    const drawn = page({ draft: picked, reading: { kind: "noFile", branch: "main" } });
    expect(drawn).toContain("acme/web has no workstream.yml on main.");
    expect(drawn).toContain("apps: [chrome, vscode, terminal]");
    expect(drawn).toContain("Or fill in the settings below.");
    expect(drawn).toContain('href="/docs/environments#workstreamyml"');
    expect(drawn).not.toContain("Suggest");
  });

  it("says why the file could not be read", () => {
    const failed = page({ draft: picked, reading: { kind: "failed", message: "GitHub did not answer." } });
    expect(failed).toContain("workstream.yml could not be read. GitHub did not answer.");
    expect(failed).toContain("Read workstream.yml again");
  });

  it("shows nothing more for a repository it has not read", () => {
    expect(page({ draft: picked, id: "e" })).not.toContain("workstream.yml on");
  });
});

describe("saving the environment to its repository", () => {
  const offer = (changes: Partial<RepositoryFileOffer>): RepositoryFileOffer => ({ justSaved: true, busy: false, error: null, saved: null, ...changes });

  it("is what the dialog offers once the environment is saved, with Done in place of the form", () => {
    const drawn = page({ draft: picked, id: "e", file: offer({}) });
    expect(drawn).toContain("Web app is saved");
    expect(drawn).toContain("Save these settings as workstream.yml in acme/web");
    expect(drawn).toContain("Save as workstream.yml in the repository");
    expect(drawn).toContain(">Done<");
    expect(drawn).not.toContain("Save environment");
    expect(drawn).not.toContain("Apps on the desktop");
  });

  it("says what it made: a commit, or a pull request where the branch is protected", () => {
    const commit = page({ draft: picked, id: "e", file: offer({ saved: { kind: "commit", url: "https://github.com/acme/web/commit/abc", branch: "main" } }) });
    expect(commit).toContain("Committed workstream.yml to main.");
    expect(commit).toContain('href="https://github.com/acme/web/commit/abc"');
    const pull = page({ draft: picked, id: "e", file: offer({ saved: { kind: "pull_request", url: "https://github.com/acme/web/pull/7", branch: "workstream-yml-1a2b3c4d" } }) });
    expect(pull).toContain("so Workstream opened one from workstream-yml-1a2b3c4d");
    expect(pull).toContain("See the pull request");
  });

  it("is in the editor of a saved environment that is not the file's, and refused in GitHub's words", () => {
    const drawn = page({ draft: picked, id: "e", file: offer({ justSaved: false, error: "GitHub did not let you write to acme/web." }) });
    expect(drawn).toContain("Save as workstream.yml in the repository");
    expect(drawn).toContain("GitHub did not let you write to acme/web.");
    expect(drawn).toContain("Save environment");
  });
});
