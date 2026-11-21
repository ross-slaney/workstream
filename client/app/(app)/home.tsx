import { Redirect, useRouter } from "expo-router";
import Head from "expo-router/head";
import { useState } from "react";
import { Button } from "@/components/kit/button";
import { EmptyState, ReadState } from "@/components/kit/status";
import { useAction } from "@/hooks/use-action";
import { useOnlineRead } from "@/hooks/use-online-read";
import { signOut } from "@/lib/auth";
import { useT } from "@/lib/i18n";
import { lastTeam } from "@/lib/last-team";
import { workstream } from "@/lib/workstream/api";
import { CreateTeamDialog } from "@/src/workspace/dialogs";

/**
 * /home: where signing in lands. It goes straight to the team the person was last in, or their first; someone
 * on no team (they left their last one) is offered to create one.
 */
export default function HomeRoute() {
  const t = useT("workspace");
  const common = useT("common");
  const router = useRouter();
  const teams = useOnlineRead(() => workstream.teams(), "teams");
  const [creating, setCreating] = useState(false);
  const action = useAction();
  if (teams.kind === "ready" && teams.value.data.length > 0) {
    const remembered = lastTeam();
    const team = teams.value.data.find((candidate) => candidate.id === remembered) ?? teams.value.data[0];
    return <Redirect href={`/t/${encodeURIComponent(team.id)}`} />;
  }
  return (
    <main className="flex h-full items-center justify-center bg-canvas text-foreground">
      <Head>
        <title>{t("meta.title")}</title>
      </Head>
      {teams.kind === "ready" ? (
        <EmptyState
          title={t("noTeam.title")}
          body={t("noTeam.body")}
          action={
            <div className="flex gap-2">
              <Button label={t("noTeam.cta")} tone="primary" onClick={() => setCreating(true)} />
              <Button label={common("actions.signOut")} onClick={() => void signOut().then(() => router.replace("/"))} />
            </div>
          }
        />
      ) : (
        <ReadState read={teams} onRetry={teams.reload} />
      )}
      {creating ? (
        <CreateTeamDialog
          busy={action.busy}
          error={action.error}
          onClose={() => setCreating(false)}
          onCreate={(name) =>
            void action.run(async () => {
              const team = await workstream.createTeam(name);
              router.replace(`/t/${encodeURIComponent(team.id)}`);
            })
          }
        />
      ) : null}
    </main>
  );
}
