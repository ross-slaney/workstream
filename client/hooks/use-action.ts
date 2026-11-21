import { useCallback, useRef, useState } from "react";
import { translator } from "@/lib/i18n";

const t = translator("common");

/**
 * One thing a person does that calls the API: whether it is under way, and the server's words when it was
 * refused. A second press while the first is under way does nothing.
 */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const running = useRef(false);

  const run = useCallback(async <T,>(action: () => Promise<T>): Promise<T | undefined> => {
    if (running.current) return undefined;
    running.current = true;
    setBusy(true);
    setError(null);
    try {
      return await action();
    } catch (failure) {
      setError(failure instanceof Error && failure.message ? failure.message : t("errors.couldNotSave"));
      return undefined;
    } finally {
      running.current = false;
      setBusy(false);
    }
  }, []);

  return { busy, error, run, clearError: useCallback(() => setError(null), []) };
}
