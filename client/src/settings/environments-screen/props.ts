import type { Online } from "@/lib/online";
import type { DesktopApp, DesktopSize, Environment, GitHubRepository } from "@/lib/workstream/api";

/** A variable in the editor. One that is already saved shows no value; typing one replaces it. */
export type VariableDraft = { name: string; value: string; saved: boolean };

/** An environment as it is being written. */
export type EnvironmentDraft = {
  name: string;
  apps: string[];
  size: DesktopSize;
  repositoryUrl: string;
  repositoryBranch: string;
  setupScript: string;
  instructions: string;
  variables: VariableDraft[];
};

/**
 * A team's environments, and the editor for one. The simple part of an environment is its apps and size;
 * the rest sits under Advanced and is what makes a desktop ready for a particular project.
 */
export type EnvironmentsScreenProps = {
  environments: Online<Environment[]>;
  /** The apps a desktop can come with. */
  apps: DesktopApp[];
  /**
   * The repositories the team's GitHub connection reaches, which the repository can be picked from. Null when
   * the team is not connected (or they could not be read): the address is typed.
   */
  repositories: GitHubRepository[] | null;
  onRetry: () => void;
  onAdd: () => void;
  onEdit: (environment: Environment) => void;
  /** The environment being written: a new one when `id` is null. Null when the editor is closed. */
  editor: { id: string | null; draft: EnvironmentDraft; busy: boolean; error: string | null } | null;
  onChangeDraft: (draft: EnvironmentDraft) => void;
  onSave: () => void;
  onDelete: () => void;
  onCloseEditor: () => void;
};
