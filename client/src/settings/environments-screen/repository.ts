import type { GitHubRepository, WorkstreamFileSettings } from "@/lib/workstream/api";
import type { EnvironmentDraft, VariableDraft } from "./props";

/** Whether two addresses name the same repository: GitHub's own, and one typed with `.git` or a slash at its end. */
export function sameRepository(a: string, b: string) {
  const plain = (url: string) => url.trim().replace(/\/+$/, "").replace(/\.git$/, "").toLowerCase();
  return plain(a) !== "" && plain(a) === plain(b);
}

/** `owner/name` of a repository on github.com, from its address; null for any other address. */
export function gitHubName(url: string): string | null {
  const match = /^https:\/\/(?:www\.)?github\.com\/([A-Za-z0-9][A-Za-z0-9-]{0,38})\/([A-Za-z0-9._-]+?)(?:\.git)?\/?$/i.exec(url.trim());
  return match ? `${match[1]}/${match[2]}` : null;
}

/** The repositories whose name holds what was typed, most recently pushed first as GitHub listed them, at most `limit`. */
export function matchingRepositories(repositories: GitHubRepository[], query: string, limit = 8): GitHubRepository[] {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  return repositories.filter((repository) => words.every((word) => repository.fullName.toLowerCase().includes(word))).slice(0, limit);
}

/** The draft's variables with these names: one kept as it is, with its value; a new one empty, for its value to be typed. */
function withVariables(current: VariableDraft[], names: string[]): VariableDraft[] {
  const byName = new Map(current.filter((variable) => variable.name.trim()).map((variable) => [variable.name.trim(), variable]));
  const listed = names.map((name) => byName.get(name) ?? { name, value: "", saved: false });
  // Variables already in the draft that the file does not name stay: their values are the person's.
  const rest = current.filter((variable) => variable.name.trim() && !names.includes(variable.name.trim()));
  return [...listed, ...rest];
}

/** The draft filled from the repository's workstream.yml. The environment then follows the file. */
export function fillFromFile(draft: EnvironmentDraft, file: WorkstreamFileSettings): EnvironmentDraft {
  return {
    ...draft,
    size: file.size,
    apps: file.apps,
    setupScript: file.setup ?? "",
    instructions: file.instructions ?? "",
    variables: withVariables(draft.variables, file.variables),
    fromFile: true,
  };
}

/**
 * Whether a change to the draft changed what workstream.yml decides: its size, apps, setup script or
 * instructions. A draft that did no longer follows the file, since each task's start would put it back.
 */
export function changesFileSettings(before: EnvironmentDraft, after: EnvironmentDraft) {
  return (
    before.size !== after.size ||
    before.setupScript !== after.setupScript ||
    before.instructions !== after.instructions ||
    before.apps.length !== after.apps.length ||
    before.apps.some((app, index) => after.apps[index] !== app)
  );
}
