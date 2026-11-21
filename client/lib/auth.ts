import type { HeadlessAuthorization } from "@sqlos/headless";
import { jwtDecode } from "jwt-decode";
import { AuthError } from "@/lib/api/errors";
import { getApiUrl, getAuthServerUrl, getSqlOsClientId } from "@/lib/config";
import { getSession, setSession, type AppSession } from "@/lib/session";

/*
 * Division of labour:
 *   - `@sqlos/headless` drives SqlOS's sign-in steps in the app and stops at an authorization code.
 *   - This module is the OAuth client half: it exchanges the code, refreshes the tokens, and signs out.
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
