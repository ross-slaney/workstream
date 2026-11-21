import { credentialEnabled } from "@sqlos/headless";
import { useHeadlessAuth } from "@sqlos/headless/react-native";
import { useRouter } from "expo-router";
import { useEffect, useRef, useState } from "react";
import { AUTH_SCOPE, completeAuthorization } from "@/lib/auth";
import { getAuthServerUrl, getRedirectUri, getSqlOsClientId } from "@/lib/config";
import { translator } from "@/lib/i18n";
import type { SignInScreenProps } from "@/src/auth/sign-in-screen";

/*
 * Signing in with an emailed code, or with an email and a password: the engine behind the sign-in screen. It
 * drives SqlOS's headless flow (start; send a code and verify it, or sign in or up with a password),
 * exchanges the authorization code for a session, keeps it and goes on to `after`. It returns exactly what
 * the view needs, so the route passes it straight through and the view knows nothing about the flow.
 *
 * Which method is the server's choice. SqlOS's settings arrive with the flow; where the API enables a
 * password (development, api/Workstream.cs PasswordSignIn) the method is `password`, else the code.
 *
 * A person on several teams is asked by SqlOS which one the session is for. The app does not care: every
 * request names its team in its address and is checked against it, so the first is chosen without asking.
 */

const words = translator("auth");

type Intent = "login" | "signup";

export function useSignIn(initialIntent: Intent, after: string): SignInScreenProps {
  const router = useRouter();
  const { flow, status, view, viewModel, error, fieldErrors, authorization } = useHeadlessAuth({
    issuer: getAuthServerUrl(),
    clientId: getSqlOsClientId(),
    redirectUri: getRedirectUri(),
  });
  const [intent, setIntent] = useState<Intent>(initialIntent);
  const [email, setEmail] = useState("");
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [localError, setLocalError] = useState<string | null>(null);
  const [finishing, setFinishing] = useState(false);
  const started = useRef(false);
  const exchanging = useRef(false);
  const choosing = useRef(false);

  const begin = (next: Intent) => flow.start({ scope: AUTH_SCOPE, view: next });
  const restart = async (next: Intent) => {
    setIntent(next);
    setCode("");
    setPassword("");
    setLocalError(null);
    setFinishing(false);
    exchanging.current = false;
    choosing.current = false;
    await begin(next);
  };

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    void begin(intent);
    // The flow starts once per mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [flow]);

  // SqlOS ended the flow with a code: exchange it, keep the session, go on.
  useEffect(() => {
    if (status !== "redirect" || !authorization || exchanging.current) return;
    exchanging.current = true;
    setFinishing(true);
    void completeAuthorization(authorization)
      .then(() => router.replace(after))
      .catch(() => {
        setLocalError(words("errors.failed"));
        setFinishing(false);
        exchanging.current = false;
      });
  }, [status, authorization, router, after]);

  // On several teams: the session is for the first; the app asks for a team by its address, not by the session.
  useEffect(() => {
    const first = viewModel?.organizationSelection?.[0];
    if (view !== "organization" || !first || choosing.current) return;
    choosing.current = true;
    void flow.organization.select({ organizationId: first.id });
  }, [view, viewModel, flow]);

  const signupVerify = view === "email-otp-signup-verify";
  const verifying = signupVerify || view === "email-otp-verify";
  // Signed in with an address that has no account: the code proved it, so SqlOS asks for a name.
  const naming = view === "email-otp-signup-complete";
  const method = credentialEnabled(viewModel?.settings, "password") ? "password" : "code";
  const busy = status === "idle" || status === "loading" || finishing || view === "organization";

  const send = () =>
    intent === "signup" ? flow.emailOtp.signupStart({ email: email.trim(), displayName: name.trim() }) : flow.emailOtp.start({ email: email.trim() });
  const withPassword = () =>
    intent === "signup" ? flow.signup({ email: email.trim(), displayName: name.trim(), password }) : flow.password.login({ email: email.trim(), password });

  return {
    mode: finishing || view === "organization" ? "finishing" : verifying ? "verify" : naming ? "name" : intent,
    method,
    busy,
    email,
    name,
    password,
    code,
    error: localError ?? error ?? (Object.values(fieldErrors).join(" ") || null),
    onEmailChange: setEmail,
    onNameChange: setName,
    onPasswordChange: setPassword,
    onCodeChange: setCode,
    onSubmit: () => {
      if (busy) return;
      setLocalError(null);
      void (naming
        ? flow.emailOtp.signupComplete({ displayName: name.trim() })
        : verifying
          ? signupVerify ? flow.emailOtp.signupVerify({ code: code.trim() }) : flow.emailOtp.verify({ code: code.trim() })
          : method === "password" ? withPassword() : send());
    },
    onResend: () => {
      setCode("");
      void send();
    },
    onRestart: () => void restart("login"),
    onSwitchMode: () => void restart(intent === "signup" ? "login" : "signup"),
    homeHref: "/",
  };
}
