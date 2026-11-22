import { generatePkce, randomState, type HeadlessAuthorization } from "@sqlos/headless";
import { jwtDecode } from "jwt-decode";
import { AuthError } from "@/lib/api/errors";
import { getApiUrl, getAuthServerUrl, getRedirectUri, getSqlOsClientId } from "@/lib/config";
import { getSession, setSession, type AppSession } from "@/lib/session";

/*
 * Division of labour:
 *   - `@sqlos/headless` drives SqlOS's sign-in steps in the app and stops at an authorization code.
 *   - This module is the OAuth client half: it exchanges the code, refreshes the tokens, and signs out.
 *
 * Signing in with a provider (GitHub) leaves the page: SqlOS sends the browser to the provider, takes it back
 * at its own callback and then sends it to /auth/callback here with the code. The page that comes back is a
 * new one, so what the sign-in began with (its state, its PKCE verifier, where to go afterwards) is kept in
 * the tab's storage for the trip (`leaveForProvider`), and the callback page finishes with it
 * (`completeProviderReturn`).
 */

/**
 * Requested on every sign-in. SqlOS grants what is requested and allowed; leaving a scope out leaves it out
 * of the token. Keep in sync with `WorkstreamApp.Scopes` in api/Workstream.cs.
 */
export const AUTH_SCOPE = "openid profile email offline_access workstream.read workstream.write";

type Claims = { exp?: number; sub?: string; email?: string; name?: string };
type TokenResponse = { access_token?: string; refresh_token?: string; error?: string };

const REFRESH_BUFFER_MS = 60_000;
let refreshing: Promise<AppSession | null> | null = null;

async function token(form: Record<string, string>): Promise<{ response: Response; body: TokenResponse }> {
  const response = await fetch(`${getAuthServerUrl()}/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" },
    body: new URLSearchParams({ client_id: getSqlOsClientId(), ...form }),
  });
  let body: TokenResponse = {};
  try {
    body = (await response.json()) as TokenResponse;
  } catch {
    // Not JSON: the status says what happened.
  }
  return { response, body };
}

function toSession(tokens: TokenResponse, previous?: AppSession | null): AppSession {
  if (!tokens.access_token || !tokens.refresh_token) throw new Error("Sign-in did not return tokens.");
  const claims = jwtDecode<Claims>(tokens.access_token);
  const userId = claims.sub ?? previous?.userId;
  if (!userId) throw new Error("The access token names nobody.");
  return {
    accessToken: tokens.access_token,
    refreshToken: tokens.refresh_token,
    expiresAt: (claims.exp ?? 0) * 1000,
    userId,
    email: claims.email ?? previous?.email ?? null,
    name: claims.name ?? previous?.name ?? null,
    systemAdmin: previous?.systemAdmin ?? false,
  };
}

/**
 * What a sign-in keeps while the browser is away at a provider. A page that only approves something inside a
 * request SqlOS began (the command line's, app/device.tsx) has no code of its own to exchange: it keeps just
 * where it was, so the browser can be sent back there if the provider does not sign the person in.
 */
export type PendingSignIn = {
  /** The `state` the flow began with, which the code must come back with. Null where no code is expected. */
  state: string | null;
  codeVerifier: string | null;
  /** A path on this site: where to go once signed in, or the page that sent the browser away. */
  returnTo: string;
};

const PENDING_KEY = "workstream.sign-in";

/** The state and PKCE pair for a flow, made here rather than inside the flow so they can outlive the page. */
export async function newSignIn(): Promise<{ state: string; codeVerifier: string; codeChallenge: string; codeChallengeMethod: "S256" }> {
  const pkce = await generatePkce();
  return { state: randomState(), codeVerifier: pkce.codeVerifier, codeChallenge: pkce.codeChallenge, codeChallengeMethod: "S256" };
}

function rememberSignIn(pending: PendingSignIn) {
  try {
    window.sessionStorage.setItem(PENDING_KEY, JSON.stringify({ state: pending.state, codeVerifier: pending.codeVerifier, returnTo: pending.returnTo }));
  } catch {
    // No storage for this tab: a provider's return then has nothing to finish with and says sign-in did not work.
  }
}

export function readSignIn(): PendingSignIn | null {
  try {
    const stored = window.sessionStorage.getItem(PENDING_KEY);
    const pending = stored ? (JSON.parse(stored) as Partial<PendingSignIn>) : null;
    return pending && typeof pending.returnTo === "string" ? { state: pending.state ?? null, codeVerifier: pending.codeVerifier ?? null, returnTo: pending.returnTo } : null;
  } catch {
    return null;
  }
}

export function forgetSignIn() {
  try {
    window.sessionStorage.removeItem(PENDING_KEY);
  } catch {
    // Nothing was kept.
  }
}

/** Send the browser to a provider's own page (GitHub's), which sends it back to SqlOS and then here. */
export function leaveForProvider(url: string, pending: PendingSignIn) {
  rememberSignIn(pending);
  window.location.assign(url);
}

/**
 * Back from a provider on the sign-in page with SqlOS's question of which team the session is for: the address
 * to read the request again with, when this tab began that sign-in (it kept a state and a PKCE verifier, so the
 * code SqlOS ends with is this tab's to redeem). Null for anything else, which the page treats as a sign-in
 * that did not work.
 */
export function teamChoiceFrom(params: { request?: string; view?: string; pendingToken?: string }, pending: PendingSignIn | null): string | null {
  if (params.view !== "organization" || !params.request || !params.pendingToken || !pending?.codeVerifier || !pending.state) return null;
  return `?request=${encodeURIComponent(params.request)}&view=organization&pendingToken=${encodeURIComponent(params.pendingToken)}`;
}

/**
 * The code to exchange when the browser lands on /auth/callback, or null when there is nothing to trust: no
 * code, nothing remembered, or a `state` that is not the one this tab's sign-in began with.
 */
export function providerReturn(params: { code?: string; state?: string }, pending: PendingSignIn | null): { code: string; codeVerifier: string; returnTo: string } | null {
  if (!params.code || !pending?.state || !pending.codeVerifier || params.state !== pending.state) return null;
  return { code: params.code, codeVerifier: pending.codeVerifier, returnTo: pending.returnTo };
}

/** Finish a sign-in that came back from a provider. Answers where to go, or null when it did not work. */
export async function completeProviderReturn(params: { code?: string; state?: string }): Promise<string | null> {
  const found = providerReturn(params, readSignIn());
  // A sign-in is finished once.
  forgetSignIn();
  if (!found) return null;
  // A session this browser already holds (someone else's, or this person's from before) ends first.
  if (getSession()) await signOut();
  await completeAuthorization({ code: found.code, codeVerifier: found.codeVerifier, redirectUri: getRedirectUri(), state: params.state ?? null });
  return found.returnTo;
}

/** Finish a sign-in: exchange the code SqlOS's flow ended with, learn who it is, and keep the session. */
export async function completeAuthorization(authorization: HeadlessAuthorization): Promise<AppSession> {
  if (!authorization.codeVerifier) throw new Error("The sign-in flow kept no verifier for its code.");
  const { response, body } = await token({
    grant_type: "authorization_code",
    code: authorization.code,
    redirect_uri: authorization.redirectUri,
    code_verifier: authorization.codeVerifier,
  });
  if (!response.ok) throw new Error(body.error ?? "Sign-in could not be completed.");
  const session = toSession(body);
  try {
    const me = await fetch(`${getApiUrl()}/api/me`, { headers: { Accept: "application/json", Authorization: `Bearer ${session.accessToken}` } });
    if (me.ok) {
      const person = (await me.json()) as { name?: string; email?: string | null; systemAdmin?: boolean };
      session.name = person.name || session.name;
      session.email = person.email ?? session.email;
      session.systemAdmin = person.systemAdmin === true;
    }
  } catch {
    // Who they are comes from the token; the rest is read again when a page needs it.
  }
  setSession(session);
  return session;
}

/**
 * Refresh the tokens, one refresh at a time for every caller, so a rotated token is never spent twice.
 * A refusal (`invalid_grant`: expired, revoked, signed out elsewhere) signs out and answers null.
 */
export function refreshSession(): Promise<AppSession | null> {
  refreshing ??= refreshOnce().finally(() => {
    refreshing = null;
  });
  return refreshing;
}

async function refreshOnce(): Promise<AppSession | null> {
  const current = getSession();
  if (!current?.refreshToken) return null;
  const { response, body } = await token({ grant_type: "refresh_token", refresh_token: current.refreshToken });
  if (response.ok) {
    const refreshed = toSession(body, current);
    setSession(refreshed);
    return refreshed;
  }
  if (response.status === 400 || response.status === 401) {
    setSession(null);
    return null;
  }
  throw new Error("The sign-in server did not answer.");
}

/** A token that is good for at least another minute, refreshing first when it is not. */
export async function resolveAccessToken(): Promise<string> {
  const session = getSession();
  if (!session) throw new AuthError();
  if (session.expiresAt > Date.now() + REFRESH_BUFFER_MS) return session.accessToken;
  const refreshed = await refreshSession();
  if (!refreshed) throw new AuthError();
  return refreshed.accessToken;
}

export async function signOut() {
  const current = getSession();
  if (current?.refreshToken) {
    try {
      await fetch(`${getAuthServerUrl()}/logout`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refreshToken: current.refreshToken }),
      });
    } catch {
      // Best effort. Signing out here must still complete.
    }
  }
  setSession(null);
}
