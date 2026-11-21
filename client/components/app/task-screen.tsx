import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { DesktopStream } from "@/components/app/desktop-stream";
import { useAction } from "@/hooks/use-action";
import { ApiError } from "@/lib/api/errors";
import { useT } from "@/lib/i18n";
import { useSession } from "@/lib/session";
import { newId } from "@/lib/utils";
import { workstream, type Message, type Task } from "@/lib/workstream/api";
import { TaskScreen, type ChatEntry } from "@/src/workspace/task-screen";

/** How often an open screen reads its conversation, in milliseconds. */
const READ_EVERY = 1500;

type Unsent = { key: string; text: string; failed: boolean };

/**
 * The controller for one screen. It reads the task's conversation every second and a half (only what is new
 * since the last message it has), sends what the person writes, and does what they ask of the task. The view
 * and the live desktop below it know nothing of the API.
 */
export function TaskScreenController({ taskId, onClose, onChanged }: { taskId: string; onClose: () => void; onChanged: () => void }) {
  const t = useT("workspace");
  const session = useSession();
  const [task, setTask] = useState<Task | null>(null);
  const [missing, setMissing] = useState(false);
  const [messages, setMessages] = useState<Message[]>([]);
  const [unsent, setUnsent] = useState<Unsent[]>([]);
  const [draft, setDraft] = useState("");
  const last = useRef(0);
  const action = useAction();

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
    last.current = 0;
    setTask(null);
    setMissing(false);
    setMessages([]);
    setUnsent([]);
    const read = async () => {
      if (reading || (typeof document !== "undefined" && document.visibilityState === "hidden")) return;
      reading = true;
      try {
        const conversation = await workstream.conversation(taskId, last.current);
        if (!live) return;
        setTask(conversation.task);
        take(conversation.messages);
      } catch (error) {
        // Gone: deleted here or by a teammate. Anything else is a read that will be tried again.
        if (live && error instanceof ApiError && error.status === 404) setMissing(true);
      } finally {
        reading = false;
      }
    };
    void read();
    const timer = setInterval(read, READ_EVERY);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, [taskId, take]);

  const send = useCallback(
    async (key: string, text: string) => {
      try {
        take([await workstream.say(taskId, text, key)]);
        setUnsent((current) => current.filter((entry) => entry.key !== key));
      } catch (error) {
        // 409: an earlier try already wrote it, and the next read brings it.
        if (error instanceof ApiError && error.status === 409) setUnsent((current) => current.filter((entry) => entry.key !== key));
        else setUnsent((current) => current.map((entry) => (entry.key === key ? { ...entry, failed: true } : entry)));
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

  return (
    <TaskScreen
      task={task}
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
      onClose={onClose}
      busy={action.busy}
      error={action.error}
      desktop={task?.state === "ready" ? <DesktopStream taskId={taskId} label={t("desktop.label", { title: task.title })} /> : null}
    />
  );
}
