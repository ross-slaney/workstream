import { useRouter } from "expo-router";
import Head from "expo-router/head";
import { useEffect, useRef, useState } from "react";
import { useTeamShell } from "@/components/app/team-shell";
import { useAction } from "@/hooks/use-action";
import { useOnlineRead } from "@/hooks/use-online-read";
import { signOut } from "@/lib/auth";
import { useT } from "@/lib/i18n";
import { valueOf } from "@/lib/online";
import { newId } from "@/lib/utils";
import { workstream, type Environment, type GitHubRepository, type SavedRepositoryFile, type WriteEnvironment } from "@/lib/workstream/api";
import { EnvironmentsScreen, type EnvironmentDraft, type RepositoryReading } from "@/src/settings/environments-screen";
import { changesFileSettings, fillFromFile, gitHubName } from "@/src/settings/environments-screen/repository";

const blank = (apps: string[]): EnvironmentDraft => ({
  name: "", apps, size: "standard", repositoryUrl: "", repositoryBranch: "", setupScript: "", instructions: "", variables: [], fromFile: false,
});

const draftOf = (environment: Environment): EnvironmentDraft => ({
  name: environment.name,
  apps: environment.apps,
  size: environment.size,
  repositoryUrl: environment.repositoryUrl ?? "",
  repositoryBranch: environment.repositoryBranch ?? "",
  setupScript: environment.setupScript ?? "",
  instructions: environment.instructions ?? "",
  variables: environment.variableNames.map((name) => ({ name, value: "", saved: true })),
  fromFile: environment.fromFile,
});

/** What is sent: a saved variable left empty keeps its value (null); rows with no name are dropped. */
const bodyOf = (draft: EnvironmentDraft): WriteEnvironment => ({
  name: draft.name.trim(),
  apps: draft.apps,
  size: draft.size,
  repositoryUrl: draft.repositoryUrl.trim() || null,
  repositoryBranch: draft.repositoryBranch.trim() || null,
  setupScript: draft.setupScript.trim() ? draft.setupScript : null,
  instructions: draft.instructions.trim() ? draft.instructions : null,
  variables: draft.variables.filter((variable) => variable.name.trim()).map((variable) => ({ name: variable.name.trim(), value: variable.value === "" && variable.saved ? null : variable.value })),
  fromFile: draft.fromFile,
});

/** How often the list is read again while an environment is being prepared, so its page shows what the setup writes. */
const PREPARING_EVERY = 3000;

type Editing = { id: string | null; draft: EnvironmentDraft; justSaved: boolean };

/**
 * /t/{team}/environments: the team's environments, and the editor for one. Picking a repository from the
 * person's GitHub reads its workstream.yml, which fills the form; without one, the form shows how to
 * write it and the person fills the settings in. A saved environment whose settings are not the file's is
 * offered to the repository as workstream.yml.
 */
export default function EnvironmentsRoute() {
  const t = useT("settings");
  const router = useRouter();
  const { teamId } = useTeamShell();
  const [preparingAny, setPreparingAny] = useState(false);
  const environments = useOnlineRead(async () => (await workstream.environments(teamId)).data, `environments:${teamId}`, preparingAny ? PREPARING_EVERY : undefined);
  const apps = useOnlineRead(() => workstream.apps(), "apps");
  // The person's own repositories, read from GitHub with the token GitHub gave them at sign-in.
  const repositories = useOnlineRead(() => workstream.myRepositories(), "github-repositories");
  const signedIn = valueOf(repositories)?.signedIn ?? false;
  const [editing, setEditing] = useState<Editing | null>(null);
  const [branches, setBranches] = useState<string[] | null>(null);
  const [reading, setReading] = useState<RepositoryReading>({ kind: "none" });
  const [savedFile, setSavedFile] = useState<SavedRepositoryFile | null>(null);
  // Each look at a repository has a number: an answer for a repository or branch no longer picked is dropped.
  const looking = useRef(0);
  const action = useAction();
  const prepare = useAction();
  const saveFile = useAction();
  const catalog = valueOf(apps) ?? [];
  const list = valueOf(environments);
  const shown = editing?.id ? (list?.find((environment) => environment.id === editing.id) ?? null) : null;

  useEffect(() => {
    setPreparingAny(Boolean(list?.some((environment) => environment.setup.state === "preparing")));
  }, [list]);

  const change = (update: (draft: EnvironmentDraft) => EnvironmentDraft) => setEditing((current) => (current ? { ...current, draft: update(current.draft) } : current));

  const done = () => {
    looking.current++;
    setEditing(null);
    environments.refresh();
  };

  const readBranches = (fullName: string) => {
    setBranches(null);
    workstream.branches(fullName).then(
      (answer) => setBranches(answer.branches),
      () => setBranches([]),
    );
  };

  /** The repository's workstream.yml on the branch fills the form; without one, the form shows how to write it. */
  const look = async (repository: { fullName: string }, branch: string) => {
    const ticket = ++looking.current;
    setReading({ kind: "reading", repository: repository.fullName });
    try {
      const file = await workstream.repositoryFile(repository.fullName, branch);
      if (ticket !== looking.current) return;
      if (file.found && file.settings) {
        const settings = file.settings;
        change((draft) => fillFromFile(draft, settings));
        setReading({ kind: "file", branch });
      } else if (file.found) {
        change((draft) => ({ ...draft, fromFile: false }));
        setReading({ kind: "badFile", errors: file.errors });
      } else {
        change((draft) => ({ ...draft, fromFile: false }));
        setReading({ kind: "noFile", branch });
      }
    } catch (failure) {
      if (ticket === looking.current) setReading({ kind: "failed", message: failure instanceof Error ? failure.message : "" });
    }
  };

  const open = (next: Editing) => {
    looking.current++;
    action.clearError();
    prepare.clearError();
    saveFile.clearError();
    setSavedFile(null);
    setReading({ kind: "none" });
    setBranches(null);
    setEditing(next);
    const name = gitHubName(next.draft.repositoryUrl);
    if (name && signedIn) readBranches(name);
  };

  const fullNameOf = (draft: EnvironmentDraft) => gitHubName(draft.repositoryUrl);
  // The offer stands for a saved environment on a GitHub repository whose settings are not its file's, and stays to show what it made.
  const fileRepository = editing?.id ? gitHubName(shown?.repositoryUrl ?? editing.draft.repositoryUrl) : null;
  const offerFile = Boolean(editing?.id && fileRepository && signedIn && (savedFile || !(shown?.fromFile ?? editing?.draft.fromFile)));

  return (
    <>
      <Head>
        <title>{t("environments.metaTitle")}</title>
      </Head>
      <EnvironmentsScreen
        environments={environments}
        apps={catalog}
        picker={{
          repositories,
          branches,
          reading,
          onRetry: repositories.reload,
          onPick: (repository: GitHubRepository | null) => {
            if (!repository) {
              looking.current++;
              setBranches(null);
              setReading({ kind: "none" });
              change((draft) => ({ ...draft, repositoryUrl: "", repositoryBranch: "", fromFile: false }));
              return;
            }
            change((draft) => ({ ...draft, repositoryUrl: repository.url, repositoryBranch: repository.defaultBranch }));
            readBranches(repository.fullName);
            void look(repository, repository.defaultBranch);
          },
          onBranch: (branch) => {
            if (!editing) return;
            const fullName = fullNameOf(editing.draft);
            change((draft) => ({ ...draft, repositoryBranch: branch }));
            if (fullName) void look({ fullName }, branch);
          },
          onReadAgain: () => {
            const fullName = editing ? fullNameOf(editing.draft) : null;
            if (editing && fullName) void look({ fullName }, editing.draft.repositoryBranch);
          },
          onSignInWithGitHub: () => {
            // Signing in with GitHub is a new sign-in: SqlOS keeps GitHub's token from it, and the person comes back here.
            void signOut().then(() => router.replace(`/sign-in?returnTo=${encodeURIComponent(`/t/${teamId}/environments`)}`));
          },
        }}
        onRetry={environments.reload}
        onAdd={() => open({ id: null, draft: blank(catalog.filter((app) => app.default).map((app) => app.id)), justSaved: false })}
        onEdit={(environment) => open({ id: environment.id, draft: draftOf(environment), justSaved: false })}
        editor={
          editing
            ? {
                id: editing.id,
                draft: editing.draft,
                busy: action.busy,
                error: action.error,
                setup: shown?.setup ?? null,
                preparing: prepare.busy,
                prepareError: prepare.error,
                file: offerFile ? { justSaved: editing.justSaved, busy: saveFile.busy, error: saveFile.error, saved: savedFile } : null,
              }
            : null
        }
        onChangeDraft={(next) => {
          if (!editing) return;
          // Settings the file decides, changed here: the environment stops following the file, which would put them back.
          if (editing.draft.fromFile && changesFileSettings(editing.draft, next)) {
            setReading({ kind: "fileChanged" });
            setEditing({ ...editing, draft: { ...next, fromFile: false } });
            return;
          }
          setEditing({ ...editing, draft: next });
        }}
        onCloseEditor={done}
        onSave={() => {
          if (!editing) return;
          void action.run(async () => {
            const saved = editing.id ? await workstream.changeEnvironment(editing.id, bodyOf(editing.draft)) : await workstream.addEnvironment(teamId, newId(), bodyOf(editing.draft));
            // An environment on a GitHub repository that is not the file's: the dialog stays, to offer saving it there.
            if (gitHubName(saved.repositoryUrl ?? "") && !saved.fromFile && signedIn) {
              environments.refresh();
              setSavedFile(null);
              saveFile.clearError();
              setReading({ kind: "none" });
              setEditing({ id: saved.id, draft: draftOf(saved), justSaved: true });
              return;
            }
            done();
          });
        }}
        onSaveFile={() => {
          if (!editing?.id) return;
          const id = editing.id;
          void saveFile.run(async () => {
            setSavedFile(await workstream.saveRepositoryFile(id));
            change((draft) => ({ ...draft, fromFile: true }));
            environments.refresh();
          });
        }}
        onPrepare={() => {
          if (!editing?.id) return;
          const id = editing.id;
          void prepare.run(async () => {
            await workstream.prepareEnvironment(id);
            environments.refresh();
          });
        }}
        onDelete={() => {
          if (!editing?.id) return;
          const id = editing.id;
          void action.run(async () => {
            await workstream.deleteEnvironment(id);
            done();
          });
        }}
      />
    </>
  );
}
