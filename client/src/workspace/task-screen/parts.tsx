import { ArrowUp, Check, Maximize2, Minimize2, MoreHorizontal, Square, X } from "lucide-react-native";
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Button, IconButton } from "@/components/kit/button";
import { Dialog } from "@/components/kit/dialog";
import { TextField } from "@/components/kit/field";
import { ErrorNote, Spinner } from "@/components/kit/status";
import { useTheme } from "@/components/theme/theme";
import { useT } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import type { DesktopState, StartStep, Task, TaskGitHub, TaskSetup } from "@/lib/workstream/api";
import { rowsOf } from "./pictures";
import type { ChatEntry, TaskScreenProps } from "./props";
import { chatNoteOf, restingOf, type Resting } from "./resting";
import { RichText } from "./rich-text";
import { lastLines } from "./setup-output";

/** A dot and a word for where a desktop is: green when it is up, amber while it changes, grey when it is paused or archived. */
export function StateBadge({ state, working }: { state: DesktopState; working: boolean }) {
  const t = useT("workspace");
  const tone =
    state === "ready" ? "text-positive"
    : state === "starting" || state === "stopping" || state === "archiving" ? "text-warning"
    : state === "failed" ? "text-destructive"
    : "text-muted-foreground";
  const label = state === "ready" && working ? t("state.working") : t(`state.${state}`);
  return (
    <span className={cn("inline-flex shrink-0 items-center gap-1.5 text-[12px] font-medium", tone)}>
      <span aria-hidden="true" className={cn("size-2 rounded-full bg-current", state === "ready" && working && "animate-pulse motion-reduce:animate-none")} />
      {label}
    </span>
  );
}

/** The bar above a screen: what the task is, where its desktop is, and what can be done to it. */
export function ScreenHeader(p: TaskScreenProps & { filled: boolean; onToggleFill?: () => void }) {
  const t = useT("workspace");
  const { colors } = useTheme();
  const [menu, setMenu] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const task = p.task;
  return (
    <header className="flex h-11 shrink-0 items-center gap-2 border-b border-border bg-card pl-3 pr-1.5">
      <h2 className="min-w-0 flex-1 truncate text-[13.5px] font-semibold text-foreground" title={task?.title}>
        {task?.title ?? " "}
      </h2>
      {task ? <StateBadge state={task.state} working={task.agentWorking} /> : null}
      {task && p.access && !p.access.manage ? <span className="shrink-0 rounded-md bg-muted px-1.5 py-0.5 text-[11.5px] font-medium text-muted-foreground">{t(p.access.drive ? "share.badge.driver" : p.access.prompt ? "share.badge.prompter" : "share.badge.watcher")}</span> : null}
      {task && p.access?.manage ? (
        <div className="relative">
          <IconButton label={t("screens.more", { title: task.title })} aria-haspopup="menu" aria-expanded={menu} icon={<MoreHorizontal size={17} color={colors.mutedForeground} />} onClick={() => setMenu((open) => !open)} />
          {menu ? (
            <Menu onClose={() => setMenu(false)}>
              <MenuItem label={t("share.open")} onSelect={p.onShare} />
              <MenuItem label={t("task.rename")} onSelect={() => setRenaming(true)} />
              {task.state === "ready" || task.state === "starting" ? <MenuItem label={t("task.pause")} onSelect={p.onStopDesktop} /> : null}
              {task.state === "stopped" || task.state === "archived" ? <MenuItem label={t("task.resume")} onSelect={p.onStartDesktop} /> : null}
              {task.state === "failed" ? <MenuItem label={t("task.start")} onSelect={p.onStartDesktop} /> : null}
              <MenuItem label={t("task.delete")} danger onSelect={() => setDeleting(true)} />
            </Menu>
          ) : null}
        </div>
      ) : null}
      {p.onToggleFill ? (
        <IconButton
          label={p.filled ? t("desktop.exitFullscreen") : t("desktop.fullscreen")}
          icon={p.filled ? <Minimize2 size={16} color={colors.mutedForeground} /> : <Maximize2 size={16} color={colors.mutedForeground} />}
          onClick={p.onToggleFill}
        />
      ) : null}
      <IconButton label={t("screens.close")} icon={<X size={17} color={colors.mutedForeground} />} onClick={p.onClose} />
      {renaming && task ? <RenameDialog title={task.title} onClose={() => setRenaming(false)} onRename={p.onRename} /> : null}
      {deleting ? <DeleteDialog onClose={() => setDeleting(false)} onDelete={p.onDelete} /> : null}
    </header>
  );
}

function Menu({ onClose, children }: { onClose: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const away = (event: MouseEvent) => {
      if (!ref.current?.contains(event.target as Node)) onClose();
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    document.addEventListener("mousedown", away);
    document.addEventListener("keydown", escape);
    ref.current?.querySelector("button")?.focus();
    return () => {
      document.removeEventListener("mousedown", away);
      document.removeEventListener("keydown", escape);
    };
  }, [onClose]);
  return (
    <div ref={ref} role="menu" onClick={onClose} className="absolute right-0 top-9 z-20 flex min-w-[190px] flex-col rounded-lg border border-border bg-card p-1 shadow-lg">
      {children}
    </div>
  );
}

function MenuItem({ label, onSelect, danger }: { label: string; onSelect: () => void; danger?: boolean }) {
  return (
    <button type="button" role="menuitem" onClick={onSelect} className={cn("rounded-md px-3 py-2 text-left text-[13px] outline-none hover:bg-muted focus-visible:bg-muted", danger ? "text-destructive" : "text-foreground")}>
      {label}
    </button>
  );
}

function RenameDialog({ title, onClose, onRename }: { title: string; onClose: () => void; onRename: (title: string) => void }) {
  const t = useT("workspace");
  const common = useT("common");
  const [value, setValue] = useState(title);
  const save = () => {
    if (value.trim()) onRename(value.trim());
    onClose();
  };
  return (
    <Dialog
      title={t("task.renameTitle")}
      onClose={onClose}
      footer={
        <>
          <Button label={common("actions.cancel")} onClick={onClose} />
          <Button label={common("actions.save")} tone="primary" onClick={save} />
        </>
      }
    >
      <form
        onSubmit={(event) => {
          event.preventDefault();
          save();
        }}
      >
        <TextField label={t("task.titleLabel")} value={value} onChangeText={setValue} maxLength={120} autoFocus />
      </form>
    </Dialog>
  );
}

function DeleteDialog({ onClose, onDelete }: { onClose: () => void; onDelete: () => void }) {
  const t = useT("workspace");
  const common = useT("common");
  return (
    <Dialog
      title={t("task.deleteTitle")}
      onClose={onClose}
      footer={
        <>
          <Button label={common("actions.cancel")} onClick={onClose} />
          <Button
            label={t("task.delete")}
            tone="danger"
            onClick={() => {
              onDelete();
              onClose();
            }}
          />
        </>
      }
    >
      <p className="text-sm text-muted-foreground">{t("task.deleteBody")}</p>
    </Dialog>
  );
}

/** The conversation: what was said and done, oldest first, and the box to write in. It follows new messages unless the person has scrolled up to read. */
export function Chat(p: TaskScreenProps) {
  const t = useT("workspace");
  const { colors } = useTheme();
  const list = useRef<HTMLDivElement>(null);
  const pinned = useRef(true);
  const task = p.task;

  useLayoutEffect(() => {
    if (pinned.current && list.current) list.current.scrollTop = list.current.scrollHeight;
  }, [p.entries.length, p.entries.at(-1)?.pending]);

  const noteKey = task ? chatNoteOf(task.state, task.step) : null;
  const note = noteKey ? t(`chat.${noteKey}`) : null;
  // The setup script runs before the agent starts on what was asked: while it does, it is what is said here.
  const settingUp = task?.state === "ready" && p.setup?.state === "running";
  const working = task?.state === "ready" && task.agentWorking && !settingUp;
  // Until the first read says otherwise, the box is there: the team, who write, is who opens most screens.
  const prompts = p.access?.prompt ?? true;

  return (
    <section aria-label={t("chat.label")} className="flex min-h-0 min-w-0 flex-1 flex-col bg-background">
      <div
        ref={list}
        onScroll={(event) => {
          const el = event.currentTarget;
          pinned.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
        }}
        className="flex min-h-0 flex-1 flex-col gap-2.5 overflow-y-auto px-3 py-3"
      >
        {p.entries.length === 0 ? <p className="m-auto max-w-[260px] text-center text-[13px] text-muted-foreground">{t("chat.empty")}</p> : null}
        {rowsOf(p.entries).map((row) =>
          row.kind === "pictures" ? <Pictures key={row.id} pictures={row.pictures} /> : <Entry key={row.id} entry={row.entry} onRetry={() => p.onRetry(row.entry.id)} />,
        )}
      </div>
      {p.github?.signInNeeded && task ? <GitHubNote github={p.github} onSignIn={p.onSignInWithGitHub} /> : null}
      {p.setup && task ? <SetupNote setup={p.setup} running={settingUp} /> : null}
      {working ? (
        <div className="flex flex-col gap-0.5 border-t border-border px-3 py-1.5 text-[12.5px] text-muted-foreground">
          <div className="flex items-center gap-2">
            <Spinner label={t("chat.working")} className="size-3" />
            <span className="flex-1">{t("chat.working")}</span>
            {prompts ? <Button label={t("chat.stop")} size="small" tone="quiet" icon={<Square size={11} color={colors.foreground} fill={colors.foreground} />} onClick={p.onInterrupt} /> : null}
          </div>
          {task.running ? <RunningNote running={task.running} /> : null}
        </div>
      ) : note ? (
        <p className="border-t border-border px-3 py-1.5 text-[12.5px] text-muted-foreground">{note}</p>
      ) : null}
      {p.error ? (
        <div className="px-3 pb-1">
          <ErrorNote message={p.error} />
        </div>
      ) : null}
      {prompts ? (
        <form
          className="flex items-end gap-2 border-t border-border p-2.5"
          onSubmit={(event) => {
            event.preventDefault();
            p.onSend();
          }}
        >
          <textarea
            value={p.draft}
            onChange={(event) => p.onDraftChange(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
                event.preventDefault();
                p.onSend();
              }
            }}
            rows={Math.min(6, Math.max(1, p.draft.split("\n").length))}
            placeholder={t("chat.placeholder")}
            aria-label={t("chat.placeholder")}
            disabled={!task || p.missing}
            className="min-h-[38px] flex-1 resize-none rounded-md border border-input bg-background px-3 py-2 text-[13.5px] leading-snug text-foreground outline-none placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-2 focus-visible:ring-ring/30"
          />
          <button
            type="submit"
            aria-label={t("chat.send")}
            title={t("chat.send")}
            disabled={!p.draft.trim() || !task || p.missing}
            className="inline-flex size-[38px] shrink-0 items-center justify-center rounded-md bg-primary text-primary-foreground outline-none hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1 focus-visible:ring-offset-background disabled:opacity-40"
          >
            <ArrowUp size={17} color={colors.primaryForeground} />
          </button>
        </form>
      ) : (
        <p className="border-t border-border px-3 py-3 text-[12.5px] leading-relaxed text-muted-foreground">{t("share.watchOnly")}</p>
      )}
    </section>
  );
}

/**
 * The desktop cannot reach GitHub as the person who started the task, because they have not signed in to
 * Workstream with GitHub. To them it says so with a way to sign in with GitHub; to anyone else it says who.
 */
function GitHubNote({ github, onSignIn }: { github: TaskGitHub; onSignIn?: () => void }) {
  const t = useT("workspace");
  return (
    <div role="status" className="flex flex-wrap items-center gap-x-3 gap-y-1.5 border-t border-border px-3 py-2 text-[12.5px]">
      <p className="min-w-0 flex-1 leading-snug text-muted-foreground">
        {github.startedByYou ? t("github.signInYou") : t("github.signInThem", { name: github.startedByName ?? t("github.someone") })}
      </p>
      {github.startedByYou && onSignIn ? <Button size="small" label={t("github.signIn")} onClick={onSignIn} /> : null}
    </div>
  );
}

/**
 * The environment's setup script on this task's desktop. While it runs: that it is running, how long it has
 * been of how long it may take, and the last lines it wrote, which follow the script. After it failed or was
 * stopped for running too long: that, said plainly, with its last lines, until the person puts it away. A
 * setup that ended well says nothing here; the conversation has its line.
 */
function SetupNote({ setup, running }: { setup: TaskSetup; running: boolean }) {
  const t = useT("workspace");
  const [open, setOpen] = useState(true);
  const [putAway, setPutAway] = useState<string | null>(null);
  const box = useRef<HTMLPreElement>(null);
  const seconds = useSecondsSince(setup.startedAt);
  const output = lastLines(setup.output, 40);
  useLayoutEffect(() => {
    if (box.current) box.current.scrollTop = box.current.scrollHeight;
  }, [output, open]);

  const ended = setup.state === "failed" || setup.state === "timed_out";
  if (!running && !ended) return null;
  if (ended && putAway === setup.startedAt) return null;
  return (
    <div role={ended ? "alert" : "status"} className={cn("flex flex-col gap-1.5 border-t px-3 py-2 text-[12.5px]", ended ? "border-destructive/40 bg-destructive/10" : "border-border")}>
      <div className="flex items-center gap-2">
        {running ? <Spinner label={t("setup.running")} className="size-3" /> : null}
        <span className={cn("min-w-0 flex-1 leading-snug", ended ? "font-medium text-destructive" : "text-muted-foreground")}>
          {running
            ? t("setup.running")
            : setup.state === "timed_out"
              ? t("setup.timedOut", { limit: setup.limitMinutes })
              : t("setup.failed", { exit: setup.exit ?? 1 })}
        </span>
        {running ? <span className="shrink-0 tabular-nums text-muted-foreground">{t("setup.elapsed", { minutes: Math.floor(seconds / 60), limit: setup.limitMinutes })}</span> : null}
      </div>
      {ended ? <p className="leading-snug text-muted-foreground">{t("setup.after")}</p> : null}
      {open ? (
        output ? (
          <pre ref={box} aria-label={t("setup.output")} tabIndex={0} className="max-h-40 overflow-auto whitespace-pre-wrap break-words rounded-md bg-muted px-2 py-1.5 font-mono text-[11.5px] leading-snug text-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring/40">
            {output}
          </pre>
        ) : (
          <p className="text-muted-foreground">{t(running ? "setup.noOutputYet" : "setup.noOutput")}</p>
        )
      ) : null}
      <div className="flex gap-3">
        <button type="button" aria-expanded={open} onClick={() => setOpen((value) => !value)} className="text-[12px] font-medium text-muted-foreground underline-offset-2 outline-none hover:underline focus-visible:underline">
          {t(open ? "setup.hide" : "setup.show")}
        </button>
        {ended ? (
          <button type="button" onClick={() => setPutAway(setup.startedAt)} className="text-[12px] font-medium text-muted-foreground underline-offset-2 outline-none hover:underline focus-visible:underline">
            {t("setup.dismiss")}
          </button>
        ) : null}
      </div>
    </div>
  );
}

/** A command shows once it has run this long: anything shorter is over before its name could be read. */
const RUNNING_SHOWS_AFTER = 5;

/**
 * The command the agent is in the middle of and how long it has run, under "The agent is working". A build
 * or a first start runs for minutes and may print nothing, and this is what says it is still going.
 */
function RunningNote({ running }: { running: { text: string; since: string } }) {
  const t = useT("workspace");
  const seconds = useSecondsSince(running.since);
  if (seconds < RUNNING_SHOWS_AFTER) return null;
  const elapsed = elapsedOf(seconds);
  const time = t(elapsed.key, elapsed.values);
  return (
    <p aria-label={t("chat.running", { command: running.text, time })} className="flex min-w-0 items-baseline gap-2 pl-5">
      <code title={running.text} className="min-w-0 flex-1 truncate font-mono text-[11.5px] text-foreground">{running.text}</code>
      <span className="shrink-0 tabular-nums">{time}</span>
    </p>
  );
}

/** How long something has run, as a person reads it: seconds under a minute, then minutes, then hours and minutes. */
export function elapsedOf(seconds: number) {
  if (seconds < 60) return { key: "chat.runningSeconds", values: { seconds } } as const;
  const minutes = Math.floor(seconds / 60);
  return minutes < 60
    ? ({ key: "chat.runningMinutes", values: { minutes } } as const)
    : ({ key: "chat.runningHours", values: { hours: Math.floor(minutes / 60), minutes: minutes % 60 } } as const);
}

/**
 * The pictures the desktop took when the agent handed work over, side by side: one of each surface, as it
 * was then. Each opens at its full size.
 */
function Pictures({ pictures }: { pictures: ChatEntry[] }) {
  const t = useT("workspace");
  return (
    <div className="flex items-start gap-2">
      {pictures.map((picture) => (
        <figure key={picture.id} className="m-0 flex min-w-0 flex-1 flex-col items-start gap-1">
          {picture.picture ? (
            <a href={picture.picture} target="_blank" rel="noreferrer" title={t("chat.openPicture")} className="block max-w-full overflow-hidden rounded-md border border-border outline-none focus-visible:ring-2 focus-visible:ring-ring/40">
              {/* As wide as its share of the row, and no taller than a phone's picture reads well at. */}
              <img src={picture.picture} alt={picture.text} className="block h-auto max-h-80 w-auto max-w-full" />
            </a>
          ) : (
            <div aria-hidden="true" className="aspect-[9/16] w-full rounded-md border border-border bg-muted" />
          )}
          <figcaption className="max-w-full truncate text-[11.5px] leading-snug text-muted-foreground" title={picture.text}>
            {picture.text}
          </figcaption>
        </figure>
      ))}
    </div>
  );
}

function Entry({ entry, onRetry }: { entry: ChatEntry; onRetry: () => void }) {
  const t = useT("workspace");
  if (entry.kind === "activity") {
    return (
      <p className="flex items-baseline gap-2 text-[12.5px] leading-snug text-muted-foreground" title={entry.text}>
        <span aria-hidden="true" className="relative top-[-1px] size-1.5 shrink-0 rounded-full bg-border" />
        <span className="min-w-0 truncate">{entry.text}</span>
      </p>
    );
  }
  if (entry.author === "agent") {
    return (
      <div className="flex flex-col gap-1.5 text-[13.5px] leading-relaxed text-foreground">
        <RichText text={entry.text} />
      </div>
    );
  }
  return (
    <div className={cn("flex max-w-[92%] flex-col gap-0.5", entry.mine ? "self-end items-end" : "self-start items-start")}>
      {!entry.mine ? <span className="px-1 text-[11.5px] text-muted-foreground">{entry.authorName ?? t("chat.atDesktop")}</span> : null}
      <div className={cn("whitespace-pre-wrap break-words rounded-xl px-3 py-2 text-[13.5px] leading-snug", entry.mine ? "bg-primary text-primary-foreground" : "bg-muted text-foreground", entry.pending === "sending" && "opacity-70")}>
        {entry.text}
      </div>
      {entry.pending === "failed" ? (
        <button type="button" onClick={onRetry} className="px-1 text-[12px] font-medium text-destructive underline-offset-2 hover:underline">
          {t("chat.notSent")}
        </button>
      ) : null}
    </div>
  );
}

/**
 * Where the desktop goes: the live picture from the moment there is one, and otherwise what is happening to it
 * and what to do. A paused or archived desktop says so and comes back when the person clicks the pane; having
 * the pane open does nothing.
 */
export function DesktopPane(p: TaskScreenProps) {
  const t = useT("workspace");
  const task = p.task;
  // Whoever may use the desktop may start it again.
  const starts = p.access?.drive ?? false;
  if (p.missing) return <Stage title={t("task.missing")} />;
  if (!task) return <Stage busy title={t("desktop.connecting")} />;
  const resting = restingOf(task.state, p.access);
  if (resting) return <RestingPane resting={resting} busy={p.busy} onResume={p.onStartDesktop} />;
  // One place for the picture, so it stays connected as a starting desktop becomes ready.
  if (p.desktop && (task.state === "ready" || task.state === "starting")) {
    return <div className="relative min-h-0 min-w-0 flex-1 bg-stage">{p.desktop}</div>;
  }
  switch (task.state) {
    case "ready":
    case "starting":
      return <Starting step={task.step} since={task.stateChangedAt} />;
    case "archiving":
      return <Stage busy title={t("desktop.archiving.title")} body={t("desktop.archiving.body")} />;
    case "failed":
      return <Stage title={t("desktop.failed.title")} body={task.failure ?? undefined} action={starts ? <StageButton label={t("desktop.failed.cta")} busy={p.busy} onClick={p.onStartDesktop} /> : undefined} />;
    default:
      return <Stage busy title={t("desktop.pausing.title")} body={t("desktop.pausing.body")} />;
  }
}

/**
 * A paused or archived desktop. For whoever may use it the whole pane is one button: a click anywhere on it
 * brings the desktop back. Someone who may only write to its agent is told a message does; someone who only
 * watches is told who can.
 */
function RestingPane({ resting, busy, onResume }: { resting: Resting; busy: boolean; onResume: () => void }) {
  const t = useT("workspace");
  const title = t(`desktop.${resting.kind}.title`);
  const body = t(`desktop.${resting.kind}.${resting.resume}`);
  if (resting.resume !== "click") return <Stage title={title} body={body} />;
  return (
    <button
      type="button"
      onClick={onResume}
      disabled={busy}
      className="flex min-h-0 min-w-0 flex-1 cursor-pointer flex-col items-center justify-center gap-3 bg-stage px-6 text-center text-stage-foreground outline-none hover:brightness-110 focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-white/70 disabled:cursor-default disabled:opacity-70"
    >
      <span className="text-[15px] font-semibold text-white">{title}</span>
      <span className="max-w-[380px] text-[13px] leading-relaxed">{body}</span>
      <span className="mt-1 inline-flex h-9 items-center rounded-md bg-white px-4 text-[13px] font-semibold text-black">{t(`desktop.${resting.kind}.cta`)}</span>
    </button>
  );
}

const START_STEPS: StartStep[] = ["machine", "boot", "agent"];

/** Seconds since a moment, counted on screen. */
function useSecondsSince(moment: string) {
  const from = Date.parse(moment);
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  return Number.isFinite(from) ? Math.max(0, Math.round((now - from) / 1000)) : 0;
}

/**
 * A desktop that is starting, before there is a screen to watch: the steps of a start in order, the ones
 * done ticked, the one under way turning, and how long it has been.
 */
function Starting({ step, since }: { step: StartStep; since: string }) {
  const t = useT("workspace");
  const seconds = useSecondsSince(since);
  const at = START_STEPS.indexOf(step);
  return (
    <div className="flex min-h-0 min-w-0 flex-1 flex-col items-center justify-center gap-4 bg-stage px-6 text-center text-stage-foreground">
      <p className="text-[15px] font-semibold text-white">{t("desktop.starting.title")}</p>
      <ol className="flex flex-col gap-2 text-left text-[13px]">
        {START_STEPS.map((name, index) => (
          <li key={name} aria-current={index === at ? "step" : undefined} className={cn("flex items-center gap-2.5", index === at && "text-white", index > at && "opacity-60")}>
            <span className="flex size-4 items-center justify-center">
              {index < at ? <Check size={14} color="currentColor" /> : index === at ? <Spinner label={t(`desktop.starting.steps.${name}`)} className="size-3.5" /> : <span aria-hidden="true" className="size-1.5 rounded-full bg-current" />}
            </span>
            <span className="flex-1">{t(`desktop.starting.steps.${name}`)}</span>
            {index === at ? <span className="tabular-nums opacity-70">{t("desktop.starting.elapsed", { seconds })}</span> : null}
          </li>
        ))}
      </ol>
      <p className="max-w-[380px] text-[13px] leading-relaxed">{t("desktop.starting.body")}</p>
    </div>
  );
}

function Stage({ title, body, busy, action }: { title: string; body?: string; busy?: boolean; action?: ReactNode }) {
  return (
    <div className="flex min-h-0 min-w-0 flex-1 flex-col items-center justify-center gap-3 bg-stage px-6 text-center text-stage-foreground">
      {busy ? <Spinner label={title} className="size-5" /> : null}
      <p className="text-[15px] font-semibold text-white">{title}</p>
      {body ? <p className="max-w-[380px] text-[13px] leading-relaxed">{body}</p> : null}
      {action}
    </div>
  );
}

function StageButton({ label, busy, onClick }: { label: string; busy: boolean; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={busy}
      className="mt-1 inline-flex h-9 items-center rounded-md bg-white px-4 text-[13px] font-semibold text-black outline-none hover:opacity-90 focus-visible:ring-2 focus-visible:ring-white/70 disabled:opacity-50"
    >
      {label}
    </button>
  );
}

export type { Task };
