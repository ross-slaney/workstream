import { Button } from "@/components/kit/button";
import { EmptyState, ErrorNote, Spinner } from "@/components/kit/status";
import { useT } from "@/lib/i18n";
import type { GitHubScreenProps } from "./props";

/** The page, the same on every surface: one message in the middle of the window. */
export function GitHubPage(p: GitHubScreenProps) {
  const t = useT("settings");
  const back = <Button label={t("github.backToTeam")} tone="primary" onClick={p.onBack} />;
  return (
    <main className="flex h-full items-center justify-center bg-canvas px-5 text-foreground">
      {p.phase === "connecting" ? (
        <div className="flex items-center gap-2 text-sm text-muted-foreground">
          <Spinner label={t("github.connecting")} />
          <span>{t("github.connecting")}</span>
        </div>
      ) : p.phase === "requested" ? (
        <EmptyState title={t("github.requestedTitle")} body={t("github.requestedBody")} action={back} />
      ) : (
        <div className="flex max-w-[460px] flex-col items-center gap-3">
          <EmptyState title={t("github.failedTitle")} action={back} />
          <ErrorNote message={p.error} />
        </div>
      )}
    </main>
  );
}
