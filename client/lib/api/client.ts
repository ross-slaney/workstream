import { ApiError, AuthError } from "@/lib/api/errors";
import { refreshSession, resolveAccessToken } from "@/lib/auth";
import { getApiUrl } from "@/lib/config";
import { translator } from "@/lib/i18n";

/*
 * The only place the app talks to /api. Controllers never call fetch: they pass a function from
 * lib/workstream/api.ts to a read hook, or call one for an action.
 *
 * Every request carries a fresh SqlOS access token. A 401 means the server refused a token the app thought
 * was good (revoked, or a clock apart), so it refreshes once and tries again.
 */

const t = translator("common");

export async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  let response = await send(path, init, await resolveAccessToken());
  if (response.status === 401) {
    const refreshed = await refreshSession();
    if (!refreshed) throw new AuthError();
    response = await send(path, init, refreshed.accessToken);
  }
  if (!response.ok) {
    const failure = await describeFailure(response);
    throw new ApiError(response.status, failure.message, failure.code);
  }
  return response;
}

export async function apiJson<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await apiFetch(path, { ...init, headers: { Accept: "application/json", ...init.headers } });
  return (response.status === 204 ? undefined : await response.json()) as T;
}

/** A write with a JSON body. */
export function apiSend<T>(method: "POST" | "PUT" | "DELETE", path: string, body?: unknown): Promise<T> {
  return apiJson<T>(path, {
    method,
    headers: body === undefined ? {} : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

function send(path: string, init: RequestInit, token: string) {
  return fetch(`${getApiUrl()}${path}`, { ...init, headers: { ...init.headers, Authorization: `Bearer ${token}` } });
}

async function describeFailure(response: Response) {
  const fallback = t("errors.requestFailed", { status: response.status });
  try {
    const body = (await response.json()) as { title?: string; detail?: string; code?: unknown } | null;
    return { message: body?.detail ?? body?.title ?? fallback, code: typeof body?.code === "string" ? body.code : undefined };
  } catch {
    return { message: fallback, code: undefined };
  }
}
