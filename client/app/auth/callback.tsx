import { useLocalSearchParams, useRouter } from "expo-router";
import Head from "expo-router/head";
import type { Metadata } from "expo-router/server";
import { useEffect, useRef } from "react";
import { completeProviderReturn } from "@/lib/auth";
import { translator, useT } from "@/lib/i18n";
import { useSessionStatus } from "@/lib/session";
import { SignInScreen, type SignInScreenProps } from "@/src/auth/sign-in-screen";

/*
 * /auth/callback?code=…&state=…: where SqlOS sends the browser when someone has signed in with GitHub
 * (hooks/use-sign-in.ts). It is the redirect address of the web app's client (api/Workstream.cs
 * WebRedirectPath). The page exchanges the code with what the sign-in page kept for the trip, keeps the
 * session and goes on to where the sign-in was headed. A code nobody here asked for (no sign-in remembered in
 * this tab, or another `state`) is not exchanged: the browser goes back to the sign-in page, which says that
 * signing in with GitHub did not work.
 */

export function generateMetadata(): Metadata {
  return { title: translator("auth")("meta.signInTitle"), robots: { index: false, follow: false } };
}

const nothing = () => {};
/** The sign-in screen at its last step: the brand and "Signing you in". */
const FINISHING: SignInScreenProps = {
  mode: "finishing", method: "code", busy: true, invitation: null, email: "", name: "", password: "", code: "", error: null, providers: [],
  onProvider: nothing, onEmailChange: nothing, onNameChange: nothing, onPasswordChange: nothing, onCodeChange: nothing,
  onSubmit: nothing, onResend: nothing, onRestart: nothing, onSwitchMode: nothing, homeHref: "/",
};

const first = (value: string | string[] | undefined) => (Array.isArray(value) ? value[0] : value);

export default function AuthCallbackRoute() {
  const t = useT("auth");
  const params = useLocalSearchParams<{ code?: string; state?: string }>();
  const router = useRouter();
  const status = useSessionStatus();
  const code = first(params.code);
  const state = first(params.state);
  const finished = useRef(false);

  useEffect(() => {
    // Once the browser has read the session it holds, so one from before is ended and not written over.
    if (status === "loading" || finished.current) return;
    finished.current = true;
    const failed = () => router.replace("/sign-in?failed=provider");
    void completeProviderReturn({ code, state }).then((returnTo) => (returnTo ? router.replace(returnTo) : failed()), failed);
  }, [status, code, state, router]);

  return (
    <>
      <Head>
        <title>{t("meta.signInTitle")}</title>
      </Head>
      {status === "loading" ? <div className="h-full bg-canvas" /> : <SignInScreen {...FINISHING} />}
    </>
  );
}
