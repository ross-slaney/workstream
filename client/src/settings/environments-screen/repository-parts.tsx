import { useId, useState } from "react";
import { Button } from "@/components/kit/button";
import { SelectField, TextField } from "@/components/kit/field";
import { ErrorNote, Spinner } from "@/components/kit/status";
import { useT } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import type { GitHubRepository } from "@/lib/workstream/api";
import type { EnvironmentDraft, RepositoryFileOffer, RepositoryPicker, RepositoryReading } from "./props";
import { gitHubName, matchingRepositories } from "./repository";

const day = (at: string) => new Date(at).toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" });

/**
 * The environment's repository. Signed in with GitHub, the person finds one of their repositories by name and
 * picks it, then its branch; otherwise, or for an address that is not on GitHub, they type it. Under it, what
 * Workstream found in the picked repository: its workstream.yml, mistakes in it, or how to write one.
 */
export function RepositorySection({ picker, draft, onChange }: { picker: RepositoryPicker; draft: EnvironmentDraft; onChange: (change: Partial<EnvironmentDraft>) => void }) {
  const t = useT("settings");
  const [typing, setTyping] = useState(false);
  const github = picker.repositories.kind === "ready" ? picker.repositories.value : null;
  const signedIn = github?.signedIn ?? false;
  const name = gitHubName(draft.repositoryUrl);
  // A repository already on the draft that is not on GitHub is typed, as it was written.
  const typed = typing || !signedIn || (draft.repositoryUrl.trim() !== "" && !name);

  return (
    <div className="flex flex-col gap-3">
      {picker.repositories.kind === "loading" ? (
        <p className="flex items-center gap-2 text-[13px] text-muted-foreground">
          <span className="font-medium text-foreground">{t("environments.editor.repositorySection")}</span>
          <Spinner label={t("environments.editor.readingRepositories")} className="size-3" />
          {t("environments.editor.readingRepositories")}
        </p>
      ) : picker.repositories.kind === "failed" ? (
        <div className="flex flex-wrap items-center gap-2">
          <p className="text-[13px] text-muted-foreground">{t("environments.editor.repositoriesFailed", { message: picker.repositories.message })}</p>
          <RetryButton onRetry={picker.onRetry} />
        </div>
      ) : github && github.available && !signedIn ? (
        <div className="flex flex-col items-start gap-2 rounded-lg border border-border bg-muted/40 px-3 py-3">
          <p className="text-[13px] text-muted-foreground">{t("environments.editor.signInForRepositories")}</p>
          <Button size="small" label={t("environments.editor.signInWithGitHub")} onClick={picker.onSignInWithGitHub} />
        </div>
      ) : null}

      {typed ? (
        <>
          <div className="grid gap-3 md:grid-cols-[1fr_180px]">
            <TextField
              label={t("environments.editor.repository")}
              placeholder={t("environments.editor.repositoryPlaceholder")}
              hint={t("environments.editor.repositoryHint")}
              value={draft.repositoryUrl}
              onChangeText={(repositoryUrl) => onChange({ repositoryUrl, fromFile: false })}
              inputMode="url"
              spellCheck={false}
            />
            <TextField label={t("environments.editor.branch")} placeholder={t("environments.editor.branchPlaceholder")} value={draft.repositoryBranch} onChangeText={(repositoryBranch) => onChange({ repositoryBranch })} spellCheck={false} />
          </div>
          {signedIn ? <Button size="small" tone="quiet" className="self-start" label={t("environments.editor.pickFromGitHub")} onClick={() => setTyping(false)} /> : null}
        </>
      ) : name ? (
        <Picked
          name={name}
          url={draft.repositoryUrl}
          branch={draft.repositoryBranch}
          branches={picker.branches}
          more={picker.moreBranches}
          onBranch={picker.onBranch}
          onChange={() => picker.onPick(null)}
        />
      ) : (
        <Finder repositories={github?.repositories ?? []} more={github?.more ?? false} onPick={picker.onPick} onType={() => setTyping(true)} />
      )}

      {name && signedIn ? <Reading reading={picker.reading} repository={name} fromFile={draft.fromFile} onReadAgain={picker.onReadAgain} /> : null}
    </div>
  );
}

function RetryButton({ onRetry }: { onRetry: () => void }) {
  const common = useT("common");
  return <Button size="small" tone="quiet" label={common("actions.retry")} onClick={onRetry} />;
}

/** Finding one of the person's repositories by name: a search over what GitHub listed, and the matches to pick. */
function Finder({ repositories, more, onPick, onType }: { repositories: GitHubRepository[]; more: boolean; onPick: (repository: GitHubRepository) => void; onType: () => void }) {
  const t = useT("settings");
  const [query, setQuery] = useState("");
  // Before anything is typed, the few most recently pushed; then whatever matches.
  const matches = matchingRepositories(repositories, query, query.trim() ? 8 : 5);
  return (
    <div className="flex flex-col gap-2">
      <TextField
        label={t("environments.editor.repositorySection")}
        placeholder={t("environments.editor.findRepositoryPlaceholder")}
        value={query}
        onChangeText={setQuery}
        spellCheck={false}
        autoComplete="off"
        hint={t("environments.editor.repositorySectionHint")}
      />
      {repositories.length === 0 ? (
        <p className="text-[13px] text-muted-foreground">{t("environments.editor.noRepositories")}</p>
      ) : matches.length === 0 ? (
        <p className="text-[13px] text-muted-foreground">{t("environments.editor.noRepositoryMatches")}</p>
      ) : (
        <ul aria-label={t("environments.editor.findRepository")} className="flex flex-col divide-y divide-border overflow-hidden rounded-lg border border-border">
          {matches.map((repository) => (
            <li key={repository.fullName}>
              <button
                type="button"
                onClick={() => onPick(repository)}
                className="flex w-full flex-wrap items-baseline gap-x-2 gap-y-0.5 bg-card px-3 py-2 text-left outline-none hover:bg-muted focus-visible:bg-muted focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
              >
                <span className="min-w-0 truncate font-mono text-[13px] text-foreground">{repository.fullName}</span>
                {repository.private ? <span className="text-[12px] text-muted-foreground">{t("environments.editor.privateRepository")}</span> : null}
                {repository.pushedAt ? <span className="ml-auto text-[12px] text-muted-foreground">{t("environments.editor.pushed", { date: day(repository.pushedAt) })}</span> : null}
              </button>
            </li>
          ))}
        </ul>
      )}
      {more ? <p className="text-[12.5px] text-muted-foreground">{t("environments.editor.moreRepositories", { count: repositories.length })}</p> : null}
      <Button size="small" tone="quiet" className="self-start" label={t("environments.editor.typeAddress")} onClick={onType} />
    </div>
  );
}

/** The picked repository, its branch, and changing it. */
function Picked({ name, url, branch, branches, more, onBranch, onChange }: {
  name: string; url: string; branch: string; branches: string[] | null; more: boolean; onBranch: (branch: string) => void; onChange: () => void;
}) {
  const t = useT("settings");
  const [typed, setTyped] = useState<string | null>(null);
  const listId = useId();
  // A branch typed is read when the person leaves the field or presses Enter, not on every key.
  const commit = () => {
    const next = typed?.trim();
    if (next && next !== branch) onBranch(next);
    setTyped(null);
  };
  // The branch on the draft is offered even when GitHub did not list it (more branches than were read, or a typo kept from before).
  const options = branches ? (branch && !branches.includes(branch) ? [branch, ...branches] : branches) : branch ? [branch] : [];
  return (
    <div className="flex flex-col gap-1.5">
      <div className="grid items-end gap-3 md:grid-cols-[1fr_220px]">
        <div className="flex flex-col gap-1.5">
          <span className="text-[13px] font-medium text-foreground">{t("environments.editor.repositorySection")}</span>
          <div className="flex min-h-10 items-center gap-2 rounded-md border border-border bg-card px-3 py-1">
            <a href={url} target="_blank" rel="noreferrer" className="min-w-0 flex-1 truncate font-mono text-[13px] text-foreground underline-offset-2 hover:underline">
              {name}
            </a>
            <Button size="small" tone="quiet" label={t("environments.editor.changeRepository")} onClick={onChange} />
          </div>
        </div>
        {more ? (
          <div>
            <TextField
              label={t("environments.editor.branch")}
              value={typed ?? branch}
              onChangeText={setTyped}
              onBlur={commit}
              onKeyDown={(event) => {
                if (event.key === "Enter") commit();
              }}
              list={listId}
              spellCheck={false}
              autoComplete="off"
            />
            <datalist id={listId}>
              {options.map((value) => (
                <option key={value} value={value} />
              ))}
            </datalist>
          </div>
        ) : options.length > 0 ? (
          <SelectField label={t("environments.editor.branch")} value={branch} onChangeValue={onBranch} options={options.map((value) => ({ value, label: value }))} />
        ) : (
          <p className="flex h-10 items-center gap-2 text-[13px] text-muted-foreground">
            <Spinner label={t("environments.editor.branch")} className="size-3" />
            {t("environments.editor.branch")}
          </p>
        )}
      </div>
      {/* Under the whole row, so the branch field stays level with the repository. */}
      {more ? <p className="text-[13px] text-muted-foreground">{t("environments.editor.moreBranches")}</p> : null}
    </div>
  );
}

/** What Workstream found in the picked repository, said plainly, with what can be done next. */
export function Reading({ reading, repository, fromFile, onReadAgain }: { reading: RepositoryReading; repository: string; fromFile: boolean; onReadAgain: () => void }) {
  const t = useT("settings");
  const box = "flex flex-col items-start gap-2 rounded-lg border px-3 py-2.5 text-[13px]";
  switch (reading.kind) {
    case "reading":
      return (
        <p role="status" className="flex items-center gap-2 text-[13px] text-muted-foreground">
          <Spinner label={t("environments.editor.reading", { repository: reading.repository })} className="size-3" />
          {t("environments.editor.reading", { repository: reading.repository })}
        </p>
      );
    case "file":
      return (
        <p role="status" className={cn(box, "border-primary/40 bg-primary-soft text-foreground")}>
          {t("environments.editor.fromFile", { branch: reading.branch })}
        </p>
      );
    case "fileChanged":
      return <p role="status" className={cn(box, "border-border text-muted-foreground")}>{t("environments.editor.fileChanged")}</p>;
    case "badFile":
      return (
        <div role="alert" className={cn(box, "border-destructive/40 bg-destructive/10")}>
          <p className="font-medium text-destructive">{t("environments.editor.badFile")}</p>
          <ul className="flex list-disc flex-col gap-0.5 pl-5 font-mono text-[12px] text-foreground">
            {reading.errors.map((error) => (
              <li key={error}>{error}</li>
            ))}
          </ul>
          <Button size="small" label={t("environments.editor.readAgain")} onClick={onReadAgain} />
        </div>
      );
    case "noFile":
      return <FileGuide repository={repository} branch={reading.branch} onReadAgain={onReadAgain} />;
    case "failed":
      return (
        <div className="flex flex-col items-start gap-2">
          <ErrorNote message={t("environments.editor.readFailed", { message: reading.message })} />
          <Button size="small" label={t("environments.editor.readAgain")} onClick={onReadAgain} />
        </div>
      );
    case "none":
      return fromFile ? <p role="status" className={cn(box, "border-primary/40 bg-primary-soft text-foreground")}>{t("environments.editor.followsFile")}</p> : null;
  }
}

/** The workstream.yml every setting can go in, as an example to copy. Not translated: it is the file's own syntax. */
const EXAMPLE_FILE = `size: standard              # small | standard | large
setup: |                    # runs once on a new desktop, in the repository's folder
  npm ci
  docker pull postgres:17
surfaces: [chrome]          # where your product runs: chrome, android, iphone
apps: [vscode, terminal]    # what you and the agent work with
variables: [DATABASE_URL]   # names only; set their values here in Workstream
instructions: |             # added to what the agent is told
  Run npm test before you say you are done.`;

/** The repository has no workstream.yml on the branch: how to write one, and that the form works without it. */
function FileGuide({ repository, branch, onReadAgain }: { repository: string; branch: string; onReadAgain: () => void }) {
  const t = useT("settings");
  return (
    <section aria-label={t("environments.editor.noFileTitle")} className="flex flex-col items-start gap-2 rounded-lg border border-border px-3 py-3">
      <p className="text-[13px] text-foreground">{t("environments.editor.noFile", { repository, branch })}</p>
      <pre className="w-full overflow-x-auto rounded-md bg-muted px-3 py-2 font-mono text-[12px] leading-5 text-foreground">{EXAMPLE_FILE}</pre>
      <p className="text-[12.5px] text-muted-foreground">
        {t("environments.editor.noFileOr")}{" "}
        <a href="/docs/environments#workstreamyml" target="_blank" rel="noreferrer" className="font-medium text-primary underline-offset-2 hover:underline">
          {t("environments.editor.fileReference")}
        </a>
      </p>
      <Button size="small" tone="quiet" label={t("environments.editor.readAgain")} onClick={onReadAgain} />
    </section>
  );
}

/** Saving the saved environment to its repository as workstream.yml, and what that made on GitHub. */
export function FileOffer({ offer, repository, onSave, plain }: { offer: RepositoryFileOffer; repository: string; onSave: () => void; plain?: boolean }) {
  const t = useT("settings");
  const link = "text-[13px] font-medium text-primary underline-offset-2 hover:underline";
  return (
    <section aria-label={t("environments.editor.fileTitle")} className={cn("flex flex-col items-start gap-2", !plain && "rounded-lg border border-border px-4 py-3")}>
      {plain ? null : <h3 className="text-sm font-semibold text-foreground">{t("environments.editor.fileTitle")}</h3>}
      {offer.saved ? (
        <p role="status" className="text-[13px] text-foreground">
          {offer.saved.kind === "commit" ? t("environments.editor.fileCommitted", { branch: offer.saved.branch }) : t("environments.editor.filePullRequest", { branch: offer.saved.branch })}{" "}
          <a href={offer.saved.url} target="_blank" rel="noreferrer" className={link}>
            {offer.saved.kind === "commit" ? t("environments.editor.seeCommit") : t("environments.editor.seePullRequest")}
          </a>
        </p>
      ) : (
        <>
          <p className="text-[13px] text-foreground">{t("environments.editor.fileOffer", { repository })}</p>
          <p className="text-[12.5px] text-muted-foreground">{t("environments.editor.fileOfferKept")}</p>
          <Button size="small" tone="primary" label={t("environments.editor.saveFile")} busy={offer.busy} onClick={onSave} />
        </>
      )}
      <ErrorNote message={offer.error} />
    </section>
  );
}
