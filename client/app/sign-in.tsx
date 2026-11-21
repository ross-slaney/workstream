import { Redirect, useLocalSearchParams } from "expo-router";
import Head from "expo-router/head";
import type { Metadata } from "expo-router/server";
import { useSignIn } from "@/hooks/use-sign-in";
import { translator, useT } from "@/lib/i18n";
import { useSessionStatus } from "@/lib/session";
import { SignInScreen } from "@/src/auth/sign-in-screen";

/*
 * /sign-in: signing in, or creating an account with ?view=signup. The server sends an empty frame, as it does
 * for every page that depends on who is signed in; the browser then starts SqlOS's flow and draws the form.
 * `returnTo` is where to go afterwards (an invitation's page), kept to this site's own paths.
 */

export function generateMetadata(): Metadata {
  return { title: translator("auth")("meta.signInTitle"), robots: { index: false, follow: false } };
}

/** Only a path on this site: never somewhere a link chose. */
function ownPath(value: string | string[] | undefined): string {
  const path = Array.isArray(value) ? value[0] : value;
  return path && path.startsWith("/") && !path.startsWith("//") ? path : "/home";
}

export default function SignInRoute() {
  const t = useT("auth");
  const params = useLocalSearchParams<{ view?: string; returnTo?: string }>();
  const status = useSessionStatus();
  const intent = params.view === "signup" ? "signup" : "login";
  const after = ownPath(params.returnTo);
  if (status === "authenticated") return <Redirect href={after} />;
  return (
    <>
      <Head>
        <title>{t("meta.signInTitle")}</title>
      </Head>
      {status === "loading" ? <div className="h-full bg-canvas" /> : <SignIn intent={intent} after={after} />}
    </>
  );
}

function SignIn({ intent, after }: { intent: "login" | "signup"; after: string }) {
  return <SignInScreen {...useSignIn(intent, after)} />;
}
