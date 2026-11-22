import { Redirect, useLocalSearchParams } from "expo-router";
import Head from "expo-router/head";
import type { Metadata } from "expo-router/server";
import { useState } from "react";
import { useSignIn } from "@/hooks/use-sign-in";
import { readSignIn } from "@/lib/auth";
import { translator, useT } from "@/lib/i18n";
import { useSessionStatus } from "@/lib/session";
import { SignInScreen } from "@/src/auth/sign-in-screen";

/*
 * /sign-in: signing in, or creating an account with ?view=signup. The server sends an empty frame, as it does
 * for every page that depends on who is signed in; the browser then starts SqlOS's flow and draws the form.
 * `returnTo` is where to go afterwards (an invitation's page), kept to this site's own paths.
 *
 * Someone who went to GitHub and was not signed in comes back here: from SqlOS with the `request` they began
 * (api/Workstream.cs SignInUiUrl), or from /auth/callback with `failed`. The form starts again and says that
 * signing in with GitHub did not work. If they had left from the command line's page, they are sent back to it.
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
  const params = useLocalSearchParams<{ view?: string; returnTo?: string; request?: string; failed?: string }>();
  const status = useSessionStatus();
  const intent = params.view === "signup" ? "signup" : "login";
  const after = ownPath(params.returnTo);
  const cameBack = Boolean(params.request) || params.failed === "provider";
  const signedIn = status === "authenticated";
  return (
    <>
      <Head>
        <title>{t("meta.signInTitle")}</title>
      </Head>
      {status === "loading" ? <div className="h-full bg-canvas" />
        : cameBack ? <CameBack intent={intent} after={after} signedIn={signedIn} />
        : signedIn ? <Redirect href={after} />
        : <SignIn intent={intent} after={after} />}
    </>
  );
}

function SignIn({ intent, after, providerFailed }: { intent: "login" | "signup"; after: string; providerFailed?: boolean }) {
  return <SignInScreen {...useSignIn(intent, after, undefined, providerFailed)} />;
}

/**
 * Back from a provider, not signed in by it. The page they left from says so: this one, or the command line's,
 * which a person signed in to the web app may well have come from.
 */
function CameBack({ intent, after, signedIn }: { intent: "login" | "signup"; after: string; signedIn: boolean }) {
  // Read in the browser only: this renders once the session has been read there.
  const [left] = useState(() => readSignIn());
  if (left && left.codeVerifier === null && left.returnTo.startsWith("/device?")) return <Redirect href={`${left.returnTo}&failed=provider`} />;
  if (signedIn) return <Redirect href={after} />;
  return <SignIn intent={intent} after={after} providerFailed />;
}
