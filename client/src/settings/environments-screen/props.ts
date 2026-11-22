import type { Online } from "@/lib/online";
import type { DesktopApp, DesktopSize, Environment, EnvironmentSetup, GitHubRepositories, GitHubRepository, SavedRepositoryFile } from "@/lib/workstream/api";

/** A variable in the editor. One that is already saved shows no value; typing one replaces it. */
export type VariableDraft = { name: string; value: string; saved: boolean };

/** An environment as it is being written. `fromFile`: its settings are the repository's workstream.yml. */
export type EnvironmentDraft = {
  name: string;
  apps: string[];
  size: DesktopSize;
  repositoryUrl: string;
  repositoryBranch: string;
  setupScript: string;
  instructions: string;
  variables: VariableDraft[];
  fromFile: boolean;
};

/**
 * What Workstream found in the repository picked for the draft: nothing read yet; looking for its
 * workstream.yml; filled from the file (and whether the person has since changed what the file decides); a
 * file with mistakes; no file on the branch, so the form shows how to write one; or the read failed, in the
 * server's words.
 */
export type RepositoryReading =
  | { kind: "none" }
  | { kind: "reading"; repository: string }
  | { kind: "file"; branch: string }
  | { kind: "fileChanged" }
  | { kind: "badFile"; errors: string[] }
  | { kind: "noFile"; branch: string }
  | { kind: "failed"; message: string };

/**
 * The person's GitHub, for picking the repository: their repositories (read with the token GitHub gave them
 * when they signed in with GitHub), the picked one's branches, and what is being read from it.
 */
export type RepositoryPicker = {
  repositories: Online<GitHubRepositories>;
  /** The picked repository's branches, its default one first. Null while they are read, or when there is no GitHub repository. */
  branches: string[] | null;
  reading: RepositoryReading;
  /** Pick a repository (null: none). Its default branch comes with it, and its workstream.yml is read. */
  onPick: (repository: GitHubRepository | null) => void;
  /** Another branch of the picked repository, whose workstream.yml is read again. */
  onBranch: (branch: string) => void;
  /** Read the repository's workstream.yml again, after a mistake in it is fixed or a read failed. */
  onReadAgain: () => void;
  /** Sign out and sign in again with GitHub, so Workstream has the person's GitHub token. */
  onSignInWithGitHub: () => void;
  onRetry: () => void;
};

/**
 * Saving the environment to its repository as workstream.yml, offered for a saved environment whose
 * repository is on GitHub and whose settings are not the file's. `justSaved`: it was offered because the
 * environment was just saved. `saved` is the commit or pull request it made.
 */
export type RepositoryFileOffer = { justSaved: boolean; busy: boolean; error: string | null; saved: SavedRepositoryFile | null };

/**
 * A team's environments, and the editor for one. The editor starts with a name and the repository, picked
 * from the person's GitHub repositories; the repository's workstream.yml, when it has one, fills the rest, which the person changes as they like. The setup script, variables and
 * instructions sit under Advanced.
 */
export type EnvironmentsScreenProps = {
  environments: Online<Environment[]>;
  /** The apps a desktop can come with. */
  apps: DesktopApp[];
  picker: RepositoryPicker;
  onRetry: () => void;
  onAdd: () => void;
  onEdit: (environment: Environment) => void;
  /**
   * The environment being written: a new one when `id` is null. Null when the editor is closed. `setup` is
   * how the saved environment's prepared setup stands now (null for a new one); `preparing` and
   * `prepareError` are "Prepare again" under way and its refusal. `file` is the offer to save it to its
   * repository as workstream.yml, or null when there is none to make.
   */
  editor: {
    id: string | null;
    draft: EnvironmentDraft;
    busy: boolean;
    error: string | null;
    setup: EnvironmentSetup | null;
    preparing: boolean;
    prepareError: string | null;
    file: RepositoryFileOffer | null;
  } | null;
  onChangeDraft: (draft: EnvironmentDraft) => void;
  onSave: () => void;
  onDelete: () => void;
  /** "Prepare again": do the saved environment's setup once more and save it for its tasks. */
  onPrepare: () => void;
  /** Save the saved environment to its repository as workstream.yml. */
  onSaveFile: () => void;
  onCloseEditor: () => void;
};
