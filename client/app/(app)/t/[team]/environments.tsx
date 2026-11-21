import Head from "expo-router/head";
import { useState } from "react";
import { useTeamShell } from "@/components/app/team-shell";
import { useAction } from "@/hooks/use-action";
import { useOnlineRead } from "@/hooks/use-online-read";
import { useT } from "@/lib/i18n";
import { valueOf } from "@/lib/online";
import { newId } from "@/lib/utils";
import { workstream, type Environment, type WriteEnvironment } from "@/lib/workstream/api";
import { EnvironmentsScreen, type EnvironmentDraft } from "@/src/settings/environments-screen";

const blank = (apps: string[]): EnvironmentDraft => ({ name: "", apps, size: "standard", repositoryUrl: "", repositoryBranch: "", setupScript: "", instructions: "", variables: [] });

const draftOf = (environment: Environment): EnvironmentDraft => ({
  name: environment.name,
  apps: environment.apps,
  size: environment.size,
  repositoryUrl: environment.repositoryUrl ?? "",
  repositoryBranch: environment.repositoryBranch ?? "",
  setupScript: environment.setupScript ?? "",
  instructions: environment.instructions ?? "",
  variables: environment.variableNames.map((name) => ({ name, value: "", saved: true })),
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
});

/** /t/{team}/environments: the team's environments, and the editor for one. */
export default function EnvironmentsRoute() {
  const t = useT("settings");
  const { teamId } = useTeamShell();
  const environments = useOnlineRead(async () => (await workstream.environments(teamId)).data, `environments:${teamId}`);
  const apps = useOnlineRead(() => workstream.apps(), "apps");
  const [editing, setEditing] = useState<{ id: string | null; draft: EnvironmentDraft } | null>(null);
  const action = useAction();
  const catalog = valueOf(apps) ?? [];

  const done = () => {
    setEditing(null);
    environments.refresh();
  };

  return (
    <>
      <Head>
        <title>{t("environments.metaTitle")}</title>
      </Head>
      <EnvironmentsScreen
        environments={environments}
        apps={catalog}
        onRetry={environments.reload}
        onAdd={() => {
          action.clearError();
          setEditing({ id: null, draft: blank(catalog.filter((app) => app.default).map((app) => app.id)) });
        }}
        onEdit={(environment) => {
          action.clearError();
          setEditing({ id: environment.id, draft: draftOf(environment) });
        }}
        editor={editing ? { ...editing, busy: action.busy, error: action.error } : null}
        onChangeDraft={(draft) => setEditing((current) => (current ? { ...current, draft } : current))}
        onCloseEditor={() => setEditing(null)}
        onSave={() => {
          if (!editing) return;
          void action.run(async () => {
            if (editing.id) await workstream.changeEnvironment(editing.id, bodyOf(editing.draft));
            else await workstream.addEnvironment(teamId, newId(), bodyOf(editing.draft));
            done();
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
