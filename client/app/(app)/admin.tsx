import { useRouter } from "expo-router";
import Head from "expo-router/head";
import { useAction } from "@/hooks/use-action";
import { useOnlineRead } from "@/hooks/use-online-read";
import { workstream } from "@/lib/workstream/api";
import { AdminScreen } from "@/src/admin/admin-screen";

/**
 * /admin: the system administrator's page, read again every fifteen seconds while it is open. The API decides
 * who may read it (`ADMIN_VIEW` on root); anyone else is shown the API's refusal.
 */
export default function AdminRoute() {
  const router = useRouter();
  const overview = useOnlineRead(() => workstream.adminOverview(), "admin:overview", 15_000);
  const action = useAction();
  return (
    <>
      <Head>
        <title>Administration · Workstream</title>
      </Head>
      <AdminScreen
        overview={overview}
        busy={action.busy}
        error={action.error}
        onRetry={overview.reload}
        onBack={() => router.navigate("/home")}
        onSetLimits={async (teamId, modelAllowanceUsd, maxRunningDesktops) => {
          const saved = await action.run(() => workstream.setTeamLimits(teamId, modelAllowanceUsd, maxRunningDesktops));
          if (saved) overview.refresh();
          return saved !== undefined;
        }}
      />
    </>
  );
}
