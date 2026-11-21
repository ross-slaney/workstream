import { useLocalSearchParams } from "expo-router";
import Head from "expo-router/head";
import type { Metadata } from "expo-router/server";
import { useSignIn } from "@/hooks/use-sign-in";
import { translator, useT } from "@/lib/i18n";
import { useSessionStatus } from "@/lib/session";
import { SignInScreen } from "@/src/auth/sign-in-screen";

/*
 * /invite?token=…: where an invitation's link lands. SqlOS resolves the emailed link and hands the browser
 * here (api/Workstream.cs BuildUiUrl). Accepting is signing in with the token attached (hooks/use-sign-in.ts):
 * someone new gives their name, someone with an account proves the address, and either way they land in the
 * team that invited them. Like sign-in, the server sends an empty frame and the browser runs the flow.
 */

export function generateMetadata(): Metadata {
  return { title: translator("auth")("invite.metaTitle"), robots: { index: false, follow: false } };
}

export default function InviteRoute() {
  const t = useT("auth");
  const params = useLocalSearchParams<{ token?: string }>();
  const token = (Array.isArray(params.token) ? params.token[0] : params.token) ?? "";
  const status = useSessionStatus();
  return (
    <>
      <Head>
        <title>{t("invite.metaTitle")}</title>
      </Head>
      {status === "loading" ? <div className="h-full bg-canvas" /> : <Accept token={token} />}
    </>
  );
}

function Accept({ token }: { token: string }) {
  return <SignInScreen {...useSignIn("signup", "/home", token)} />;
}
