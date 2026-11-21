import Head from "expo-router/head";
import { useTeamShell } from "@/components/app/team-shell";
import { useOnlineRead } from "@/hooks/use-online-read";
import { useT } from "@/lib/i18n";
import { workstream } from "@/lib/workstream/api";
import { UsageScreen } from "@/src/settings/usage-screen";

/** /t/{team}/usage: what the team has used this month, read again every half minute while it is open. */
export default function UsageRoute() {
  const t = useT("settings");
  const { teamId } = useTeamShell();
  const usage = useOnlineRead(() => workstream.usage(teamId), `usage:${teamId}`, 30_000);
  return (
    <>
      <Head>
        <title>{t("usage.metaTitle")}</title>
      </Head>
      <UsageScreen usage={usage} onRetry={usage.reload} />
    </>
  );
}
