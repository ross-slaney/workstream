import { useSyncExternalStore } from "react";

/*
 * The signed-in session, held in one place: who the person is and the tokens that say so. `lib/auth.ts`
 * writes it, `lib/api/client.ts` reads it, and views follow it through the hooks below. It is kept in the
 * browser's storage so a reload stays signed in; the server never has it, so every signed-in page renders
 * only in the browser.
 */

export type AppSession = {
  accessToken: string;
  refreshToken: string;
  /** When the access token stops working, in milliseconds since the epoch. */
  expiresAt: number;
  userId: string;
  email: string | null;
  name: string | null;
  /** Holds the system administrator's dashboard (`ADMIN_VIEW` on root), as `GET /api/me` said at sign-in. */
  systemAdmin: boolean;
};

export type SessionStatus = "loading" | "anonymous" | "authenticated";

const STORAGE_KEY = "workstream.session";

type State = { hydrated: boolean; session: AppSession | null };

let state: State = { hydrated: false, session: null };
const listeners = new Set<() => void>();

function set(next: State) {
  state = next;
  for (const listener of listeners) listener();
}

export function getSession(): AppSession | null {
  return state.session;
}

/** Keep the session, here and in the browser's storage. Null signs out. */
export function setSession(session: AppSession | null) {
  try {
    if (session) window.localStorage.setItem(STORAGE_KEY, JSON.stringify(session));
    else window.localStorage.removeItem(STORAGE_KEY);
  } catch {
    // Storage is unavailable (a private window, blocked site data): the session lasts until the tab closes.
  }
  set({ hydrated: true, session });
}

/** Read the stored session once, in the browser, after the first render. */
export function hydrateSession() {
  if (state.hydrated) return;
  let session: AppSession | null = null;
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    session = stored ? (JSON.parse(stored) as AppSession) : null;
  } catch {
    session = null;
  }
  set({ hydrated: true, session });
}

const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};
const snapshot = () => state;
// The server knows no session: every page renders there as "loading".
const serverState: State = { hydrated: false, session: null };
const serverSnapshot = () => serverState;

export function useSession(): AppSession | null {
  return useSyncExternalStore(subscribe, snapshot, serverSnapshot).session;
}

export function useSessionStatus(): SessionStatus {
  const current = useSyncExternalStore(subscribe, snapshot, serverSnapshot);
  return !current.hydrated ? "loading" : current.session ? "authenticated" : "anonymous";
}
