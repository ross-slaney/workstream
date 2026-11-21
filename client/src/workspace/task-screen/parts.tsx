import { ArrowUp, Check, Maximize2, Minimize2, MoreHorizontal, Square, X } from "lucide-react-native";
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Button, IconButton } from "@/components/kit/button";
import { Dialog } from "@/components/kit/dialog";
import { TextField } from "@/components/kit/field";
import { ErrorNote, Spinner } from "@/components/kit/status";
import { useTheme } from "@/components/theme/theme";
import { useT } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import type { DesktopState, StartStep, Task } from "@/lib/workstream/api";
import type { ChatEntry, TaskScreenProps } from "./props";
import { RichText } from "./rich-text";

/** A dot and a word for where a desktop is: green when it is up, amber while it changes, grey when it is off. */
export function StateBadge({ state, working }: { state: DesktopState; working: boolean }) {
  const t = useT("workspace");
  const tone =
    state === "ready" ? "text-positive"
    : state === "starting" || state === "stopping" ? "text-warning"
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
      {task ? (
        <div className="relative">
          <IconButton label={t("screens.more", { title: task.title })} aria-haspopup="menu" aria-expanded={menu} icon={<MoreHorizontal size={17} color={colors.mutedForeground} />} onClick={() => setMenu((open) => !open)} />
          {menu ? (
            <Menu onClose={() => setMenu(false)}>
              <MenuItem label={t("task.rename")} onSelect={() => setRenaming(true)} />
              {task.state === "ready" || task.state === "starting" ? <MenuItem label={t("task.stop")} onSelect={p.onStopDesktop} /> : null}
              {task.state === "stopped" || task.state === "failed" ? <MenuItem label={t("task.start")} onSelect={p.onStartDesktop} /> : null}
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

  const note =
    !task ? null
    : task.state === "starting" ? (task.step === "agent" ? t("chat.agentStarting") : t("chat.waitingForDesktop"))
    : task.state === "stopped" || task.state === "stopping" || task.state === "failed" ? t("chat.desktopStopped")
    : null;
  const working = task?.state === "ready" && task.agentWorking;

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
        {p.entries.map((entry) => (
          <Entry key={entry.id} entry={entry} onRetry={() => p.onRetry(entry.id)} />
        ))}
      </div>
      {working ? (
        <div className="flex items-center gap-2 border-t border-border px-3 py-1.5 text-[12.5px] text-muted-foreground">
          <Spinner label={t("chat.working")} className="size-3" />
          <span className="flex-1">{t("chat.working")}</span>
          <Button label={t("chat.stop")} size="small" tone="quiet" icon={<Square size={11} color={colors.foreground} fill={colors.foreground} />} onClick={p.onInterrupt} />
        </div>
      ) : note ? (
        <p className="border-t border-border px-3 py-1.5 text-[12.5px] text-muted-foreground">{note}</p>
      ) : null}
      {p.error ? (
        <div className="px-3 pb-1">
          <ErrorNote message={p.error} />
        </div>
      ) : null}
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
    </section>
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

/** Where the desktop goes: the live picture from the moment there is one, and otherwise what is happening to it and what to do. */
export function DesktopPane(p: TaskScreenProps) {
  const t = useT("workspace");
  const task = p.task;
  if (p.missing) return <Stage title={t("task.missing")} />;
  if (!task) return <Stage busy title={t("desktop.connecting")} />;
  // One place for the picture, so it stays connected as a starting desktop becomes ready.
  if (p.desktop && (task.state === "ready" || task.state === "starting")) {
    return <div className="relative min-h-0 min-w-0 flex-1 bg-stage">{p.desktop}</div>;
  }
  switch (task.state) {
    case "ready":
    case "starting":
      return <Starting step={task.step} since={task.stateChangedAt} />;
    case "stopping":
    case "deleting":
      return <Stage busy title={t("desktop.stopping.title")} body={t("desktop.stopping.body")} />;
    case "failed":
      return <Stage title={t("desktop.failed.title")} body={task.failure ?? undefined} action={<StageButton label={t("desktop.failed.cta")} busy={p.busy} onClick={p.onStartDesktop} />} />;
    default:
      return <Stage title={t("desktop.stopped.title")} body={t("desktop.stopped.body")} action={<StageButton label={t("desktop.stopped.cta")} busy={p.busy} onClick={p.onStartDesktop} />} />;
  }
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
