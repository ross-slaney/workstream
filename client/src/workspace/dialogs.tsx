import { useState } from "react";
import { Button } from "@/components/kit/button";
import { Dialog } from "@/components/kit/dialog";
import { SelectField, TextArea, TextField } from "@/components/kit/field";
import { ErrorNote } from "@/components/kit/status";
import { useT } from "@/lib/i18n";
import type { Environment } from "@/lib/workstream/api";

export type NewTaskDialogProps = {
  environments: Environment[];
  busy: boolean;
  /** Why the task could not be started, in the server's words: a limit reached, most often. */
  error: string | null;
  /** A task: the agent starts on what was asked. */
  onStart: (prompt: string, environmentId: string) => void;
  /** A desktop with nothing asked of the agent: the person works on it themselves, and the agent is there when they write to it. */
  onOpen: (environmentId: string) => void;
  onClose: () => void;
};

/**
 * Starting work on a new desktop, one of two ways from the same form: a task, where the agent starts on what
 * the person wrote, or the desktop alone, opened for the person to work on.
 */
export function NewTaskDialog({ environments, busy, error, onStart, onOpen, onClose }: NewTaskDialogProps) {
  const t = useT("workspace");
  const common = useT("common");
  const [prompt, setPrompt] = useState("");
  const [environmentId, setEnvironmentId] = useState(environments[0]?.id ?? "");
  const ready = prompt.trim().length > 0 && environmentId.length > 0;
  const start = () => {
    if (ready) onStart(prompt.trim(), environmentId);
  };
  // Which of the two buttons was pressed, so only that one shows it is working.
  const [opening, setOpening] = useState(false);
  return (
    <Dialog
      title={t("newTask.title")}
      onClose={onClose}
      wide
      footer={
        <>
          <Button label={common("actions.cancel")} onClick={onClose} />
          <Button
            label={t("newTask.open")}
            busy={busy && opening}
            disabled={environmentId.length === 0 || (busy && !opening)}
            onClick={() => {
              setOpening(true);
              onOpen(environmentId);
            }}
          />
          <Button
            label={t("newTask.start")}
            tone="primary"
            busy={busy && !opening}
            disabled={!ready || (busy && opening)}
            onClick={() => {
              setOpening(false);
              start();
            }}
          />
        </>
      }
    >
      <TextArea
        label={t("newTask.promptLabel")}
        placeholder={t("newTask.promptPlaceholder")}
        value={prompt}
        onChangeText={setPrompt}
        rows={6}
        autoFocus
        onKeyDown={(event) => {
          if (event.key === "Enter" && (event.metaKey || event.ctrlKey)) start();
        }}
      />
      <p className="text-[13px] leading-relaxed text-muted-foreground">{t("newTask.openHint")}</p>
      {environments.length === 0 ? (
        <ErrorNote message={t("newTask.noEnvironments")} />
      ) : (
        <SelectField
          label={t("newTask.environmentLabel")}
          hint={t("newTask.environmentHint")}
          value={environmentId}
          onChangeValue={setEnvironmentId}
          options={environments.map((environment) => ({ value: environment.id, label: environment.name }))}
        />
      )}
      <ErrorNote message={error} />
    </Dialog>
  );
}

export type CreateTeamDialogProps = {
  busy: boolean;
  error: string | null;
  onCreate: (name: string) => void;
  onClose: () => void;
};

export function CreateTeamDialog({ busy, error, onCreate, onClose }: CreateTeamDialogProps) {
  const t = useT("workspace");
  const common = useT("common");
  const [name, setName] = useState("");
  const create = () => {
    if (name.trim()) onCreate(name.trim());
  };
  return (
    <Dialog
      title={t("createTeam.title")}
      onClose={onClose}
      footer={
        <>
          <Button label={common("actions.cancel")} onClick={onClose} />
          <Button label={t("createTeam.create")} tone="primary" busy={busy} disabled={!name.trim()} onClick={create} />
        </>
      }
    >
      <p className="text-sm text-muted-foreground">{t("createTeam.body")}</p>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          create();
        }}
      >
        <TextField label={t("createTeam.nameLabel")} value={name} onChangeText={setName} maxLength={100} autoFocus />
      </form>
      <ErrorNote message={error} />
    </Dialog>
  );
}
