import { Plus, X } from "lucide-react-native";
import { useState } from "react";
import { Button, IconButton } from "@/components/kit/button";
import { Dialog } from "@/components/kit/dialog";
import { CheckField, SelectField, TextArea, TextField } from "@/components/kit/field";
import { EmptyState, ErrorNote, ReadState } from "@/components/kit/status";
import { useTheme } from "@/components/theme/theme";
import { useT } from "@/lib/i18n";
import type { DesktopSize, Environment, EnvironmentSetup } from "@/lib/workstream/api";
import { SettingsPage } from "../page";
import type { EnvironmentDraft, EnvironmentsScreenProps } from "./props";
import { gitHubName } from "./repository";
import { FileOffer, RepositorySection } from "./repository-parts";

/** The page, the same on every surface: the list, and the editor over it. */
export function EnvironmentsPage(p: EnvironmentsScreenProps) {
  const t = useT("settings");
  const { colors } = useTheme();
  return (
    <SettingsPage
      title={t("environments.title")}
      subtitle={t("environments.subtitle")}
      action={<Button label={t("environments.add")} tone="primary" icon={<Plus size={16} color={colors.primaryForeground} />} onClick={p.onAdd} />}
    >
      {p.environments.kind !== "ready" ? (
        <ReadState read={p.environments} onRetry={p.onRetry} />
      ) : p.environments.value.length === 0 ? (
        <EmptyState title={t("environments.empty")} />
      ) : (
        <ul className="flex flex-col gap-2">
          {p.environments.value.map((environment) => (
            <li key={environment.id}>
              <EnvironmentRow environment={environment} onEdit={() => p.onEdit(environment)} />
            </li>
          ))}
        </ul>
      )}
      {p.editor ? <Editor {...p} editor={p.editor} /> : null}
    </SettingsPage>
  );
}

function EnvironmentRow({ environment, onEdit }: { environment: Environment; onEdit: () => void }) {
  const t = useT("settings");
  const facts = [
    t("environments.appsCount", { count: environment.apps.length }),
    environment.repositoryUrl ? t("environments.hasRepository", { repository: environment.repositoryUrl.replace(/^https:\/\/(www\.)?/, "") }) : null,
    environment.setupScript ? t("environments.hasSetup") : null,
    environment.variableNames.length > 0 ? t("environments.variablesCount", { count: environment.variableNames.length }) : null,
    environment.setup.state === "nothing" ? null : t(`environments.setupFact.${environment.setup.state}`),
  ].filter(Boolean);
  return (
    <button type="button" onClick={onEdit} className="flex w-full flex-col gap-1 rounded-lg border border-border bg-card px-4 py-3 text-left outline-none hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring">
      <span className="text-[15px] font-semibold text-foreground">{environment.name}</span>
      <span className="text-[13px] text-muted-foreground">{facts.join(" · ")}</span>
    </button>
  );
}

function Editor(p: EnvironmentsScreenProps & { editor: NonNullable<EnvironmentsScreenProps["editor"]> }) {
  const t = useT("settings");
  const common = useT("common");
  const { colors } = useTheme();
  const { draft, id, busy, error } = p.editor;
  const [confirming, setConfirming] = useState(false);
  const set = (change: Partial<EnvironmentDraft>) => p.onChangeDraft({ ...draft, ...change });
  const sizes: DesktopSize[] = ["small", "standard", "large"];
  const advanced = Boolean(draft.setupScript || draft.instructions || draft.variables.length > 0);
  const repository = gitHubName(draft.repositoryUrl);

  if (confirming) {
    return (
      <Dialog
        title={t("environments.editor.deleteTitle")}
        onClose={() => setConfirming(false)}
        footer={
          <>
            <Button label={common("actions.cancel")} onClick={() => setConfirming(false)} />
            <Button label={t("environments.editor.delete")} tone="danger" busy={busy} onClick={p.onDelete} />
          </>
        }
      >
        <p className="text-sm text-muted-foreground">{t("environments.editor.deleteBody")}</p>
        <ErrorNote message={error} />
      </Dialog>
    );
  }

  return (
    <Dialog
      title={id ? t("environments.editor.editTitle") : t("environments.editor.newTitle")}
      onClose={p.onCloseEditor}
      wide
      footer={
        <>
          {id ? <Button label={t("environments.editor.delete")} tone="quiet" className="mr-auto text-destructive" onClick={() => setConfirming(true)} /> : null}
          {p.editor.file?.justSaved ? (
            <Button label={t("environments.editor.done")} tone="primary" onClick={p.onCloseEditor} />
          ) : (
            <>
              <Button label={common("actions.cancel")} onClick={p.onCloseEditor} />
              <Button label={t("environments.editor.save")} tone="primary" busy={busy} disabled={!draft.name.trim()} onClick={p.onSave} />
            </>
          )}
        </>
      }
    >
      {p.editor.file?.justSaved && repository ? <FileOffer offer={p.editor.file} repository={repository} onSave={p.onSaveFile} /> : null}

      <TextField label={t("environments.editor.name")} placeholder={t("environments.editor.namePlaceholder")} value={draft.name} onChangeText={(name) => set({ name })} maxLength={100} autoFocus={!id} />

      <RepositorySection picker={p.picker} draft={draft} onChange={set} />

      <fieldset className="flex flex-col gap-2">
        <legend className="text-[13px] font-medium text-foreground">{t("environments.editor.apps")}</legend>
        <p className="text-[13px] text-muted-foreground">{t("environments.editor.appsHint")}</p>
        <div className="grid gap-2 md:grid-cols-2">
          {p.apps.map((app) => (
            <CheckField
              key={app.id}
              label={app.name}
              description={app.description}
              checked={draft.apps.includes(app.id)}
              onChange={(checked) => set({ apps: checked ? [...draft.apps, app.id] : draft.apps.filter((other) => other !== app.id) })}
            />
          ))}
        </div>
      </fieldset>

      <SelectField
        label={t("environments.editor.size")}
        value={draft.size}
        onChangeValue={(size) => set({ size: size as DesktopSize })}
        options={sizes.map((size) => ({ value: size, label: t(`environments.editor.sizes.${size}`) }))}
      />

      <details open={advanced} className="rounded-lg border border-border">
        <summary className="cursor-pointer select-none rounded-lg px-4 py-3 text-sm font-semibold text-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring">
          {t("environments.editor.advanced")}
          <span className="ml-2 font-normal text-muted-foreground">{t("environments.editor.advancedHint")}</span>
        </summary>
        <div className="flex flex-col gap-4 border-t border-border px-4 py-4">
          <TextArea label={t("environments.editor.setupScript")} hint={t("environments.editor.setupScriptHint")} placeholder={t("environments.editor.setupScriptPlaceholder")} value={draft.setupScript} onChangeText={(setupScript) => set({ setupScript })} rows={6} code />
          <fieldset className="flex flex-col gap-2">
            <legend className="text-[13px] font-medium text-foreground">{t("environments.editor.variables")}</legend>
            <p className="text-[13px] text-muted-foreground">{t("environments.editor.variablesHint")}</p>
            {draft.variables.map((variable, index) => (
              <div key={index} className="flex items-center gap-2">
                <input
                  aria-label={t("environments.editor.variableName")}
                  placeholder={t("environments.editor.variableName")}
                  value={variable.name}
                  onChange={(event) => set({ variables: draft.variables.map((other, at) => (at === index ? { ...other, name: event.target.value } : other)) })}
                  spellCheck={false}
                  className="h-10 w-2/5 rounded-md border border-input bg-background px-3 font-mono text-[13px] text-foreground outline-none focus-visible:border-ring focus-visible:ring-2 focus-visible:ring-ring/30"
                />
                <input
                  aria-label={t("environments.editor.variableValue")}
                  placeholder={variable.saved ? t("environments.editor.variableKept") : t("environments.editor.variableValue")}
                  value={variable.value}
                  onChange={(event) => set({ variables: draft.variables.map((other, at) => (at === index ? { ...other, value: event.target.value } : other)) })}
                  type="password"
                  autoComplete="off"
                  spellCheck={false}
                  className="h-10 min-w-0 flex-1 rounded-md border border-input bg-background px-3 font-mono text-[13px] text-foreground outline-none placeholder:font-sans focus-visible:border-ring focus-visible:ring-2 focus-visible:ring-ring/30"
                />
                <IconButton
                  label={t("environments.editor.removeVariable", { name: variable.name || t("environments.editor.variableName") })}
                  icon={<X size={16} color={colors.mutedForeground} />}
                  onClick={() => set({ variables: draft.variables.filter((_, at) => at !== index) })}
                />
              </div>
            ))}
            <Button label={t("environments.editor.addVariable")} size="small" className="self-start" onClick={() => set({ variables: [...draft.variables, { name: "", value: "", saved: false }] })} />
          </fieldset>
          <TextArea label={t("environments.editor.instructions")} hint={t("environments.editor.instructionsHint")} placeholder={t("environments.editor.instructionsPlaceholder")} value={draft.instructions} onChangeText={(instructions) => set({ instructions })} rows={4} />
        </div>
      </details>
      {p.editor.file && !p.editor.file.justSaved && repository ? <FileOffer offer={p.editor.file} repository={repository} onSave={p.onSaveFile} /> : null}
      {p.editor.setup ? <SetupSection setup={p.editor.setup} preparing={p.editor.preparing} error={p.editor.prepareError} onPrepare={p.onPrepare} /> : null}
      <ErrorNote message={error} />
    </Dialog>
  );
}

const moment = (at: string) => new Date(at).toLocaleString(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
const dollars = (value: number) => value.toLocaleString("en-US", { style: "currency", currency: "USD" });
const gigabytes = (bytes: number) => (Math.round((bytes / 1024 ** 3) * 10) / 10).toLocaleString();

/** What the Setup section says, from how the setup stands. */
function setupWords(t: ReturnType<typeof useT<"settings">>, setup: EnvironmentSetup): string {
  switch (setup.state) {
    case "nothing":
      return t("environments.setup.nothing");
    case "not_prepared":
      return t("environments.setup.notPrepared");
    case "preparing":
      return setup.startedAt ? t("environments.setup.preparing", { since: moment(setup.startedAt) }) : t("environments.setup.waiting");
    case "prepared":
      return setup.commit
        ? t("environments.setup.preparedAt", { when: moment(setup.preparedAt ?? ""), commit: setup.commit.slice(0, 7) })
        : t("environments.setup.prepared", { when: moment(setup.preparedAt ?? "") });
    case "out_of_date":
      return setup.because === "desktop" ? t("environments.setup.outOfDateDesktop") : t("environments.setup.outOfDateSetup");
    case "failed":
      return t("environments.setup.failed", { failure: setup.failure ?? "" });
  }
}

/**
 * The environment's prepared setup, as it stands: the repository cloned and the setup script run once on a
 * desktop of its own, whose disk its tasks start from. What it says is the saved environment's, not the draft's.
 */
function SetupSection({ setup, preparing, error, onPrepare }: { setup: EnvironmentSetup; preparing: boolean; error: string | null; onPrepare: () => void }) {
  const t = useT("settings");
  const output = setup.state === "preparing" || setup.state === "failed" ? setup.output : null;
  const canPrepare = setup.state !== "nothing" && setup.state !== "preparing";
  return (
    <section aria-labelledby="environment-setup" className="flex flex-col gap-2 rounded-lg border border-border px-4 py-3">
      <h3 id="environment-setup" className="text-sm font-semibold text-foreground">
        {t("environments.setup.title")}
      </h3>
      <p className="text-[13px] text-muted-foreground">{t("environments.setup.hint")}</p>
      <p className="text-sm text-foreground" role="status">
        {setupWords(t, setup)}
      </p>
      {setup.state === "prepared" && setup.bytes > 0 ? (
        <p className="text-[13px] text-muted-foreground">{t("environments.setup.kept", { gigabytes: gigabytes(setup.bytes), cost: dollars(setup.monthlyUsd) })}</p>
      ) : null}
      {output ? (
        <pre aria-label={t("environments.setup.output")} className="max-h-48 overflow-auto whitespace-pre-wrap rounded-md bg-muted px-3 py-2 font-mono text-[12px] leading-relaxed text-foreground">
          {output.split("\n").slice(-12).join("\n")}
        </pre>
      ) : null}
      {canPrepare ? (
        <Button
          label={setup.state === "not_prepared" ? t("environments.setup.prepare") : t("environments.setup.prepareAgain")}
          size="small"
          className="self-start"
          busy={preparing}
          onClick={onPrepare}
        />
      ) : null}
      <ErrorNote message={error} />
    </section>
  );
}
