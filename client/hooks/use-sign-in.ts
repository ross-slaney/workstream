import { credentialEnabled, type HeadlessAuthorization, type UseHeadlessAuthResult } from "@sqlos/headless";
import { useHeadlessAuth } from "@sqlos/headless/react-native";
import { useRouter } from "expo-router";
import { useEffect, useRef, useState } from "react";
import { AUTH_SCOPE, completeAuthorization, leaveForProvider, newSignIn, signOut, type PendingSignIn } from "@/lib/auth";
import { getAuthServerUrl, getRedirectUri, getSqlOsClientId } from "@/lib/config";
import { translator } from "@/lib/i18n";
import { getSession } from "@/lib/session";
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
 * An invitation is the same flow started with the invitation's token (app/invite.tsx). SqlOS answers with the
 * team and the address it was sent to, every step after that carries the token, and signing in or creating
 * the account accepts it: the link proves the mailbox, so someone new only gives their name. The person
 * lands in the team that invited them.
 *
 * Where the API has a GitHub OAuth client (api/Workstream.cs SeedGitHubSignIn), SqlOS lists GitHub with the
 * flow and the form shows "Continue with GitHub" beside the emailed code. Pressing it asks SqlOS where to go
 * and leaves for GitHub; SqlOS takes the browser back and sends it to /auth/callback with the code
 * (app/auth/callback.tsx), or to /sign-in again when GitHub did not sign the person in. An invitation is for
 * one address, so its form offers no provider.
 *
 * A person on several teams is asked by SqlOS which one the session is for. The app does not care: every
 * request names its team in its address and is checked against it, so the first is chosen without asking.
 *
 * The steps themselves (`useSignInSteps`) are also how the command line's sign-in is approved
 * (hooks/use-device-sign-in.ts): the same form over a flow that SqlOS began for the command line.
 */

const words = translator("auth");

type Intent = "login" | "signup";
type Invitation = { teamId: string; teamName: string; email: string };

const VERIFY_VIEWS = ["email-otp-verify", "invite-email-otp-verify"];

export function useSignIn(initialIntent: Intent, after: string, invitationToken?: string, providerFailed?: boolean): SignInScreenProps {
  const router = useRouter();
  const auth = useHeadlessAuth({
    issuer: getAuthServerUrl(),
    clientId: getSqlOsClientId(),
    redirectUri: getRedirectUri(),
  });
  // What the flow on screen began with, kept so a trip to a provider can be finished by the page that comes back.
  const began = useRef<PendingSignIn | null>(null);
  return useSignInSteps(auth, initialIntent, {
    invitationToken,
    providerFailed,
    begin: async (next) => {
      const pkce = await newSignIn();
      began.current = { state: pkce.state, codeVerifier: pkce.codeVerifier, returnTo: after };
      // Given a token and no view, SqlOS answers with the invitation.
      return auth.flow.start(invitationToken ? { scope: AUTH_SCOPE, invitationToken, ...pkce } : { scope: AUTH_SCOPE, view: next, ...pkce });
    },
    leave: (url) => {
      if (began.current) leaveForProvider(url, began.current);
    },
    // SqlOS ended the flow with a code: exchange it, keep the session, go on.
    finish: async (authorization, invitation) => {
      if (!authorization) throw new Error("Sign-in ended without a code.");
      // A session this browser already holds (someone else's, or this person's from before) ends first.
      if (getSession()) await signOut();
      await completeAuthorization(authorization);
      router.replace(invitation ? `/t/${encodeURIComponent(invitation.teamId)}` : after);
    },
  });
}

export type SignInSteps = {
  /** Starts the flow the form steps through, and starts it again for another address or the other form. */
  begin: (intent: Intent) => Promise<unknown>;
  /** SqlOS ended the flow with a redirect, with a code or without one. A throw shows that sign-in did not work. */
  finish: (authorization: HeadlessAuthorization | null, invitation: Invitation | null) => Promise<void>;
  /** SqlOS answered "Continue with GitHub" with GitHub's address: go there, remembering what coming back needs. */
  leave: (url: string) => void;
  invitationToken?: string;
  /** The browser came back from a provider without being signed in: the form says so. */
  providerFailed?: boolean;
};

/** The sign-in form's steps over a headless flow: which fields are on screen, what submitting does, and when it is done. */
export function useSignInSteps(auth: UseHeadlessAuthResult, initialIntent: Intent, steps: SignInSteps): SignInScreenProps {
  const { flow, status, view, viewModel, error, fieldErrors, authorization, redirectUrl } = auth;
  const { invitationToken } = steps;
  const latest = useRef(steps);
  latest.current = steps;
  const [intent, setIntent] = useState<Intent>(initialIntent);
  const [email, setEmail] = useState("");
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [localError, setLocalError] = useState<string | null>(steps.providerFailed ? words("errors.provider") : null);
  const [finishing, setFinishing] = useState(false);
  // The invitation SqlOS resolved from the token, kept once known: later views do not repeat it.
  const [invitation, setInvitation] = useState<Invitation | null>(null);
  const started = useRef(false);
  const exchanging = useRef(false);
  const choosing = useRef(false);
  const leaving = useRef(false);

  const invited = invitationToken ? { invitationToken } : {};
  const restart = async (next: Intent) => {
    setIntent(next);
    setCode("");
    setPassword("");
    setLocalError(null);
    setFinishing(false);
    exchanging.current = false;
    choosing.current = false;
    await latest.current.begin(next);
  };

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    void latest.current.begin(intent);
    // The flow starts once per mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [flow]);

  useEffect(() => {
    const found = viewModel?.invitation;
    if (found) setInvitation({ teamId: found.organizationId, teamName: found.organizationName, email: found.email });
  }, [viewModel?.invitation]);

  // SqlOS ended the flow: whoever began it says what that means.
  useEffect(() => {
    if (status !== "redirect") return;
    // A redirect with no code, after the provider's button: SqlOS answered with the provider's own address.
    // Nothing here waits on it, so a browser that comes back with its Back button finds the form as it was.
    if (leaving.current && !authorization && redirectUrl) {
      leaving.current = false;
      latest.current.leave(redirectUrl);
      return;
    }
    if (exchanging.current) return;
    exchanging.current = true;
    setFinishing(true);
    void latest.current.finish(authorization, invitation).catch(() => {
      setLocalError(words("errors.failed"));
      setFinishing(false);
      exchanging.current = false;
    });
  }, [status, authorization, invitation, redirectUrl]);

  // On several teams: the session is for the one that invited them, else the first. The app asks for a team
  // by its address, not by the session.
  useEffect(() => {
    const choice = viewModel?.organizationSelection?.find((candidate) => candidate.id === invitation?.teamId) ?? viewModel?.organizationSelection?.[0];
    if (view !== "organization" || !choice || choosing.current) return;
    choosing.current = true;
    void flow.organization.select({ organizationId: choice.id });
  }, [view, viewModel, flow, invitation]);

  const signupVerify = view === "email-otp-signup-verify";
  const verifying = signupVerify || (view !== null && VERIFY_VIEWS.includes(view));
  // Signed in with an address that has no account: the code proved it, so SqlOS asks for a name.
  const naming = view === "email-otp-signup-complete";
  const method = credentialEnabled(viewModel?.settings, "password") ? "password" : "code";
  const busy = status === "idle" || status === "loading" || finishing || view === "organization";
  // A link with no token, or one SqlOS would not resolve: expired, withdrawn or already used.
  const unusable = invitationToken !== undefined && !invitation && (invitationToken === "" || status === "error");
  const address = invitation?.email ?? email.trim();
  const choosingMethod = !unusable && !finishing && !verifying && !naming && view !== "organization";

  const send = () =>
    intent === "login" ? flow.emailOtp.start({ email: address, ...invited })
    // An invitation's link proved the mailbox, so someone new needs no code.
    : invitationToken ? flow.invitation.signup({ displayName: name.trim(), email: address, invitationToken })
    : flow.emailOtp.signupStart({ displayName: name.trim(), email: address });
  const withPassword = () =>
    intent === "signup" ? flow.signup({ email: address, displayName: name.trim(), password, ...invited }) : flow.password.login({ email: address, password, ...invited });

  return {
    mode: unusable ? "unusable" : finishing || view === "organization" ? "finishing" : verifying ? "verify" : naming ? "name" : invitationToken !== undefined && !invitation ? "opening" : intent,
    method,
    busy,
    invitation: invitation ? { teamName: invitation.teamName, email: invitation.email } : null,
    email: address,
    name,
    password,
    code,
    error: unusable ? null : localError ?? error ?? (Object.values(fieldErrors).join(" ") || null),
    onEmailChange: setEmail,
    onNameChange: setName,
    onPasswordChange: setPassword,
    onCodeChange: setCode,
    // An invitation is for one address, which a provider may not answer with: it is accepted by code or password.
    providers: choosingMethod && invitationToken === undefined ? (viewModel?.providers ?? []).map((provider) => ({ id: provider.connectionId, name: provider.displayName, kind: provider.providerType.toLowerCase() })) : [],
    onProvider: (id) => {
      if (busy) return;
      setLocalError(null);
      leaving.current = true;
      void flow.provider.start({ connectionId: id });
    },
    onSubmit: () => {
      if (busy) return;
      leaving.current = false;
      setLocalError(null);
      void (naming
        ? flow.emailOtp.signupComplete({ displayName: name.trim(), ...invited })
        : verifying
          ? signupVerify ? flow.emailOtp.signupVerify({ code: code.trim(), ...invited }) : flow.emailOtp.verify({ code: code.trim(), ...invited })
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
