import { useCallback, useEffect, useRef, useState } from "react";
import { isOfflineError } from "@/lib/api/errors";
import { translator } from "@/lib/i18n";
import type { Online } from "@/lib/online";

const t = translator("common");

export type OnlineReadHandle<T> = Online<T> & {
  /** Reads again from the start: loading, then the value or why not. */
  reload: () => void;
  /** Reads again behind the value already shown: no loading in between, and a failed read keeps what is shown. */
  refresh: () => void;
  /** Takes what an action answered, so the screen need not read again. */
  replace: (value: T) => void;
};

/**
 * One read of the API: loading, then the value, or why not (offline, or the server's words). Re-reads when
 * `key` changes and on `reload()`; `refresh()` reads again behind the value shown, and with `every` the hook
 * does that by itself on that many milliseconds while the page is visible. Nothing is kept on the device.
 */
export function useOnlineRead<T>(read: () => Promise<T>, key: string, every?: number): OnlineReadHandle<T> {
  const [content, setContent] = useState<Online<T>>({ kind: "loading" });
  const [attempt, setAttempt] = useState(0);
  const shown = useRef(content);
  shown.current = content;
  const behind = useRef(false);
  const readKey = useRef(key);

  useEffect(() => {
    let live = true;
    // Only a refresh of the same key over a value already shown stays quiet.
    const quiet = behind.current && readKey.current === key && shown.current.kind === "ready";
    behind.current = false;
    readKey.current = key;
    if (!quiet) setContent({ kind: "loading" });
    read().then(
      (value) => live && setContent({ kind: "ready", value }),
      (error: unknown) => {
        if (!live || quiet) return;
        setContent(isOfflineError(error) ? { kind: "offline" } : { kind: "failed", message: error instanceof Error ? error.message : t("errors.couldNotRead") });
      },
    );
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `key` names what `read` depends on; `attempt` is reload() and refresh().
  }, [key, attempt]);

  const reload = useCallback(() => {
    behind.current = false;
    setAttempt((n) => n + 1);
  }, []);
  const refresh = useCallback(() => {
    behind.current = true;
    setAttempt((n) => n + 1);
  }, []);
  const replace = useCallback((value: T) => setContent({ kind: "ready", value }), []);

  useEffect(() => {
    if (!every) return;
    const timer = setInterval(() => {
      if (typeof document === "undefined" || document.visibilityState === "visible") refresh();
    }, every);
    return () => clearInterval(timer);
  }, [every, refresh]);

  return { ...content, reload, refresh, replace };
}
