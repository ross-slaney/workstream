// Workstream's API, called with the desktop's key by the short commands a desktop has (`phone`, `surfaces`).
// The key reaches the desktop's own task and nothing else, and is asked for each time (told.mjs): it is never
// written to the disk.

import { toldNow } from "./told.mjs";

/**
 * One call. `body` goes as JSON. `file` is a blob, which has a length, and goes as `type`.
 * @returns {Promise<Response>}
 */
export async function api(method, route, { body, file, type = "application/gzip", told = toldNow } = {}) {
  const { api: origin, key } = await told();
  if (!origin || !key) throw new Error("This desktop was not told its Workstream API.");
  const headers = { authorization: `Bearer ${key}` };
  if (body) headers["content-type"] = "application/json";
  if (file) headers["content-type"] = type;
  return fetch(`${origin}${route}`, {
    method, headers, body: file ?? (body ? JSON.stringify(body) : undefined),
    signal: AbortSignal.timeout(file ? 30 * 60_000 : 60_000),
  });
}

/** The answer's JSON, or an error with the API's own words. */
export async function answerOf(response) {
  if (response.ok) return response.status === 204 ? null : response.json();
  const problem = await response.json().catch(() => null);
  throw new Error(typeof problem?.title === "string" && problem.title ? problem.title : `Workstream answered ${response.status}.`);
}
