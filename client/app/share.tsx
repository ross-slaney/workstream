import { Redirect, useLocalSearchParams, useRouter } from "expo-router";
import Head from "expo-router/head";
import type { Metadata } from "expo-router/server";
import { useEffect, useRef, useState } from "react";
import { Button } from "@/components/kit/button";
import { EmptyState, Spinner } from "@/components/kit/status";
import { ApiError } from "@/lib/api/errors";
import { signOut } from "@/lib/auth";
import { translator, useT } from "@/lib/i18n";
import { useSessionStatus } from "@/lib/session";
import { workstream } from "@/lib/workstream/api";

/*
 * /share?token=…: where the link to a task shared with someone who has no account lands (api/Operations/
 * Sharing.cs). Signed out, it sends them to create their account, with this page to come back to: an ordinary
 * sign-up, with a team of their own. Signed in, it takes the invitation, which works only for the address it
 * was sent to, and opens the task. Like sign-in, the server sends an empty frame and the browser does the rest.
 */

export function generateMetadata(): Metadata {
  return { title: translator("workspace")("shareLink.metaTitle"), robots: { index: false, follow: false } };
}

export default function ShareRoute() {
  const t = useT("workspace");
  const params = useLocalSearchParams<{ token?: string }>();
  const token = (Array.isArray(params.token) ? params.token[0] : params.token) ?? "";
  const status = useSessionStatus();
  const here = `/share?token=${encodeURIComponent(token)}`;
  return (
    <>
      <Head>
        <title>{t("shareLink.metaTitle")}</title>
      </Head>
      {status === "loading" ? (
        <div className="h-full bg-canvas" />
      ) : status === "anonymous" ? (
        <Redirect href={`/sign-in?view=signup&returnTo=${encodeURIComponent(here)}`} />
      ) : (
        <Take token={token} />
      )}
    </>
  );
}

function Take({ token }: { token: string }) {
  const t = useT("workspace");
  const common = useT("common");
  const router = useRouter();
  const [refused, setRefused] = useState<string | null>(null);
  const taken = useRef(false);
  useEffect(() => {
    if (taken.current) return;
    taken.current = true;
    workstream.acceptTaskInvitation(token).then(
      ({ taskId }) => router.replace(`/home?open=${encodeURIComponent(taskId)}`),
      (error: unknown) =>
        // Signed in as someone else: the server's words say which address the link is for.
        setRefused(error instanceof ApiError && error.status === 403 ? error.message : t("shareLink.unusable")),
    );
  }, [token, router, t]);
  return (
    <main className="flex h-full items-center justify-center bg-canvas text-foreground">
      {refused ? (
        <EmptyState
          title={t("shareLink.unusableTitle")}
          body={refused}
          action={
            <div className="flex gap-2">
              <Button label={t("shareLink.home")} tone="primary" onClick={() => router.replace("/home")} />
              <Button label={common("actions.signOut")} onClick={() => void signOut().then(() => router.replace(`/sign-in?returnTo=${encodeURIComponent(`/share?token=${encodeURIComponent(token)}`)}`))} />
            </div>
          }
        />
      ) : (
        <div className="flex items-center gap-2 text-sm text-muted-foreground">
          <Spinner label={t("shareLink.opening")} />
          <span>{t("shareLink.opening")}</span>
        </div>
      )}
    </main>
  );
}
