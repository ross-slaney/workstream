import type { ReactNode } from "react";
import type { Online } from "@/lib/online";
import { useT } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import { Button } from "./button";

/** A spinner that says what is happening to a screen reader. */
export function Spinner({ label, className }: { label: string; className?: string }) {
  return (
    <span role="status" aria-label={label} className={cn("inline-block size-4 animate-spin rounded-full border-2 border-current border-t-transparent motion-reduce:animate-none", className)} />
  );
}

/** Something refused, in the server's words. */
export function ErrorNote({ message }: { message: string | null | undefined }) {
  if (!message) return null;
  return (
    <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-[13px] text-destructive">
      {message}
    </p>
  );
}

export function EmptyState({ title, body, action }: { title: string; body?: string; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center gap-3 px-6 py-10 text-center">
      <p className="text-base font-semibold text-foreground">{title}</p>
      {body ? <p className="max-w-[420px] text-sm text-muted-foreground">{body}</p> : null}
      {action}
    </div>
  );
}

/** What a read that is not ready shows: loading, offline, or the server's words with a way to try again. */
export function ReadState<T>({ read, onRetry }: { read: Online<T>; onRetry: () => void }) {
  const t = useT("common");
  if (read.kind === "ready") return null;
  if (read.kind === "loading") {
    return (
      <div className="flex items-center gap-2 px-1 py-6 text-sm text-muted-foreground">
        <Spinner label={t("state.loading")} />
        <span>{t("state.loading")}</span>
      </div>
    );
  }
  return (
    <div className="flex flex-col items-start gap-3 py-6">
      <ErrorNote message={read.kind === "offline" ? t("state.offline") : read.message} />
      <Button label={t("actions.retry")} onClick={onRetry} size="small" />
    </div>
  );
}
