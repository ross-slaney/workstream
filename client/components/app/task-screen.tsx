import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { DesktopStream } from "@/components/app/desktop-stream";
import { useAction } from "@/hooks/use-action";
import { ApiError } from "@/lib/api/errors";
import { useT } from "@/lib/i18n";
import { useSession } from "@/lib/session";
import { newId } from "@/lib/utils";
import { useOnlineRead } from "@/hooks/use-online-read";
import { workstream, type Message, type Task, type TaskAccess, type TaskSetup, type TaskShared } from "@/lib/workstream/api";
import { ShareDialog } from "@/src/workspace/share-dialog";
import { TaskScreen, type ChatEntry } from "@/src/workspace/task-screen";

/** How often an open screen reads its conversation, in milliseconds: sooner while the desktop is starting, when each step is news. */
const READ_EVERY = 1500;
const READ_EVERY_STARTING = 500;

type Unsent = { key: string; text: string; failed: boolean };

/**
 * The controller for one screen. It reads the task's conversation every second and a half, and twice a second
 * while the desktop is starting (only what is new since the last message it has), sends what the person
 * writes, and does what they ask of the task. Each read also says what the person may do with it, so a share
 * that changes or ends shows on the screen at the next read; the team shares the task from here. The view
 * and the live desktop below it know nothing of the API.
 *
 * A paused or archived desktop comes back when the person clicks its pane (`onStartDesktop`) or sends a
 * message, which the API starts it for. Reading the conversation, which this does for as long as the screen
 * is open, starts nothing.
 */
export function TaskScreenController({ taskId, onClose, onChanged }: { taskId: string; onClose: () => void; onChanged: () => void }) {
  const t = useT("workspace");
  const session = useSession();
  const [task, setTask] = useState<Task | null>(null);
  const [access, setAccess] = useState<TaskAccess | null>(null);
  const [setup, setSetup] = useState<TaskSetup | null>(null);
  const [sharing, setSharing] = useState(false);
  const [shared, setShared] = useState<TaskShared | null>(null);
  const shareAction = useAction();
  const people = useOnlineRead(() => (sharing ? workstream.taskAccess(taskId) : Promise.resolve(null)), `access:${taskId}:${sharing}`);
  const [missing, setMissing] = useState(false);
  const [messages, setMessages] = useState<Message[]>([]);
  const [unsent, setUnsent] = useState<Unsent[]>([]);
  const [draft, setDraft] = useState("");
  // Four by three until the stream says what the desktop's screen really is (desktop/start.sh).
  const [desktopShape, setDesktopShape] = useState(4 / 3);
  const last = useRef(0);
  const action = useAction();
  // Why the last message was refused, when the server said: the team's limit of running desktops, most often.
  const [sendError, setSendError] = useState<string | null>(null);

  const take = useCallback((incoming: Message[]) => {
    if (incoming.length === 0) return;
    last.current = Math.max(last.current, ...incoming.map((message) => message.id));
    setMessages((current) => {
      const known = new Set(current.map((message) => message.id));
      const fresh = incoming.filter((message) => !known.has(message.id));
      return fresh.length === 0 ? current : [...current, ...fresh].sort((a, b) => a.id - b.id);
    });
  }, []);

  useEffect(() => {
    let live = true;
    let reading = false;
    let starting = true;
    last.current = 0;
    setTask(null);
    setAccess(null);
    setSetup(null);
    setMissing(false);
    setMessages([]);
    setUnsent([]);
    setSendError(null);
    const read = async () => {
      if (reading || (typeof document !== "undefined" && document.visibilityState === "hidden")) return;
      reading = true;
      try {
        const conversation = await workstream.conversation(taskId, last.current);
        if (!live) return;
        starting = conversation.task.state === "starting";
        setTask(conversation.task);
        setAccess(conversation.access);
        setSetup(conversation.setup ?? null);
        take(conversation.messages);
      } catch (error) {
        // Gone: deleted here or by a teammate. Anything else is a read that will be tried again.
        if (live && error instanceof ApiError && error.status === 404) setMissing(true);
      } finally {
        reading = false;
      }
    };
    let timer: ReturnType<typeof setTimeout> | undefined;
    const readOn = async () => {
      await read();
      if (live) timer = setTimeout(readOn, starting ? READ_EVERY_STARTING : READ_EVERY);
    };
    void readOn();
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [taskId, take]);

  const send = useCallback(
    async (key: string, text: string) => {
      setSendError(null);
      try {
        take([await workstream.say(taskId, text, key)]);
        setUnsent((current) => current.filter((entry) => entry.key !== key));
      } catch (error) {
        // 409: an earlier try already wrote it, and the next read brings it.
        if (error instanceof ApiError && error.status === 409) setUnsent((current) => current.filter((entry) => entry.key !== key));
        else {
          setUnsent((current) => current.map((entry) => (entry.key === key ? { ...entry, failed: true } : entry)));
          // A paused desktop the team has no room to resume: the message is not kept, and the server says why.
          if (error instanceof ApiError && (error.status === 402 || error.status === 400) && error.message) setSendError(error.message);
        }
      }
    },
    [taskId, take],
  );

  const entries = useMemo<ChatEntry[]>(
    () => [
      ...messages.map((message) => ({
        id: String(message.id),
        author: message.author,
        authorName: message.authorName,
        kind: message.kind,
        text: message.text,
        mine: message.mine,
      })),
      ...unsent.map((entry) => ({
        id: entry.key, author: "person" as const, authorName: session?.name ?? null, kind: "text" as const, text: entry.text, mine: true,
        pending: entry.failed ? ("failed" as const) : ("sending" as const),
      })),
    ],
    [messages, unsent, session?.name],
  );

  const act = (run: () => Promise<Task | void>) =>
    void action.run(async () => {
      const changed = await run();
      if (changed) setTask(changed);
      onChanged();
    });

  const changeSharing = (run: () => Promise<void>) =>
    void shareAction.run(async () => {
      await run();
      people.refresh();
    });

  return (
    <>
      <TaskScreen
        task={task}
        access={access}
        setup={setup}
        missing={missing}
        entries={entries}
        draft={draft}
        onDraftChange={setDraft}
        onSend={() => {
          const text = draft.trim();
          if (!text) return;
          const key = newId();
          setDraft("");
          setUnsent((current) => [...current, { key, text, failed: false }]);
          void send(key, text);
        }}
        onRetry={(key) => {
          const entry = unsent.find((candidate) => candidate.key === key);
          if (!entry) return;
          setUnsent((current) => current.map((candidate) => (candidate.key === key ? { ...candidate, failed: false } : candidate)));
          void send(key, entry.text);
        }}
        onInterrupt={() => act(() => workstream.interrupt(taskId))}
        onStartDesktop={() => act(() => workstream.startDesktop(taskId))}
        onStopDesktop={() => act(() => workstream.stopDesktop(taskId))}
        onRename={(title) => act(() => workstream.renameTask(taskId, title))}
        onDelete={() =>
          void action.run(async () => {
            await workstream.deleteTask(taskId);
            onChanged();
            onClose();
          })
        }
        onShare={() => {
          shareAction.clearError();
          setShared(null);
          setSharing(true);
        }}
        onClose={onClose}
        busy={action.busy}
        error={action.error ?? sendError}
        // There is a screen to watch as soon as the display answers, which is before the agent has joined.
        desktop={
          task && (task.state === "ready" || (task.state === "starting" && task.step === "agent")) ? (
            <DesktopStream taskId={taskId} label={t("desktop.label", { title: task.title })} viewOnly={!(access?.drive ?? false)} onShape={setDesktopShape} />
          ) : null
        }
        desktopShape={desktopShape}
      />
      {sharing && task ? (
        <ShareDialog
          title={task.title}
          access={people.kind === "ready" ? (people.value ? { kind: "ready", value: people.value } : { kind: "loading" }) : people}
          busy={shareAction.busy}
          error={shareAction.error}
          shared={shared}
          onShare={(email, role, length) =>
            changeSharing(async () => {
              setShared(await workstream.shareTask(taskId, email, role, length));
            })
          }
          onRevoke={(userId) => changeSharing(() => workstream.revokeShare(taskId, userId))}
          onWithdraw={(invitationId) => changeSharing(() => workstream.withdrawTaskInvitation(taskId, invitationId))}
          onRetry={people.reload}
          onClose={() => {
            setSharing(false);
            setShared(null);
          }}
        />
      ) : null}
    </>
  );
}
