import { useLocalSearchParams, useRouter } from "expo-router";
import Head from "expo-router/head";
import { useEffect, useRef, useState } from "react";
import { forgetGitHubConnect, pendingGitHubConnect } from "@/lib/github-connect";
import { useT } from "@/lib/i18n";
import { workstream } from "@/lib/workstream/api";
import { GitHubScreen, type GitHubScreenProps } from "@/src/settings/github-screen";

type Returned = { code?: string; installation_id?: string; setup_action?: string; state?: string };

/**
 * /github: where GitHub sends an administrator back after installing Workstream's app (through the API's
 * callback, which adds nothing). The connection is finished here, as the person signed in on this browser,
 * which is what ties it to them; then they are back on the team's page.
 */
export default function GitHubRoute() {
  const t = useT("settings");
  const router = useRouter();
  const returned = useLocalSearchParams<Returned>();
  const pending = pendingGitHubConnect();
  const [phase, setPhase] = useState<GitHubScreenProps["phase"]>("connecting");
  const [error, setError] = useState<string | null>(null);
  // GitHub's code works once: the connection is asked for once, however often this renders.
  const asked = useRef(false);

  useEffect(() => {
    if (asked.current) return;
    asked.current = true;
    const installationId = Number(returned.installation_id);
    // An organization's member asked its owners to install the app: nothing to connect until one of them does.
    if (returned.setup_action === "request" || !Number.isFinite(installationId) || installationId <= 0) {
      setPhase("requested");
      return;
    }
    workstream
      .connectGitHub({ state: returned.state || pending?.state || "", code: returned.code ?? null, installationId })
      .then(({ teamId: connected }) => {
        forgetGitHubConnect();
        router.replace(`/t/${encodeURIComponent(connected)}/team`);
      })
      .catch((failure: unknown) => {
        setError(failure instanceof Error ? failure.message : null);
        setPhase("failed");
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once, with what GitHub handed back.
  }, []);

  // The team it was set off from on this browser; without one, the start, which opens the last team.
  const back = () => router.replace(pending ? `/t/${encodeURIComponent(pending.teamId)}/team` : "/home");

  return (
    <>
      <Head>
        <title>{t("github.metaTitle")}</title>
      </Head>
      <GitHubScreen phase={phase} error={error} onBack={back} />
    </>
  );
}
