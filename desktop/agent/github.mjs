#!/usr/bin/env node
// GitHub for the desktop's git and gh. The token is the one GitHub gave the person who started the task when they
// signed in to Workstream with GitHub (GET /api/desktop/github/token, with the desktop's key), so the agent
// pushes and opens pull requests as them. It is asked for again each time it is needed, so nothing here writes
// it anywhere.
//
//   node github.mjs credential get   git's credential helper, for https://github.com (agent.mjs sets it up)
//   node github.mjs token            prints a token, for gh (shims/gh)
//
// A token someone put in the environment's variables as GITHUB_TOKEN or GH_TOKEN is used as it is, without
// asking the API. When the API has none (the person never signed in with GitHub), it says why, and that is
// written where git and gh show it, so the agent can pass it on.

import { pathToFileURL } from "node:url";
import { toldNow } from "./told.mjs";

/** git's credential request: `key=value` lines, ended by a blank line. */
export function parseCredentialRequest(text) {
  const request = {};
  for (const line of String(text ?? "").split("\n")) {
    const at = line.indexOf("=");
    if (at > 0) request[line.slice(0, at)] = line.slice(at + 1).trim();
  }
  return request;
}

/** Whether git is asking about GitHub over https, the only thing this helper answers for. */
export const isGitHub = (request) => request.protocol === "https" && /^(www\.)?github\.com$/i.test(request.host ?? "");

/** What git is told: a GitHub token goes as the password of the user `x-access-token`. */
export const credentialAnswer = (token) => `username=x-access-token\npassword=${token}\n`;

/** A token pasted into the environment's variables. */
export const pastedToken = (env) => env.GITHUB_TOKEN || env.GH_TOKEN || null;

/**
 * A GitHub token, or why there is none: the pasted one, or the one the API has for the person who started the
 * task. `reason` is the API's own words when it has none, or what went wrong reaching it.
 */
export async function askToken({ env = process.env, told = toldNow, fetch: get = fetch } = {}) {
  const pasted = pastedToken(env);
  if (pasted) return { token: pasted, reason: null };
  const { api, key } = await told(env);
  if (!api || !key) return { token: null, reason: null };
  try {
    const response = await get(`${api}/api/desktop/github/token`, { headers: { authorization: `Bearer ${key}` }, signal: AbortSignal.timeout(15_000) });
    const answer = await response.json().catch(() => null);
    if (!response.ok) return { token: null, reason: typeof answer?.title === "string" && answer.title ? answer.title : null };
    return typeof answer?.token === "string" && answer.token ? { token: answer.token, reason: null } : { token: null, reason: null };
  } catch {
    return { token: null, reason: "Workstream could not be reached for a GitHub token. Try again in a moment." };
  }
}

/**
 * Who the desktop's commits are by: the person who started the task, by their GitHub name and the address
 * GitHub gives their account for commits (GET /api/desktop/github/author). Null when the API has none for them
 * (they did not sign in with GitHub) or cannot be reached: the commits then keep the agent's own name.
 */
export async function authorFor({ env = process.env, told = toldNow, fetch: get = fetch } = {}) {
  const { api, key } = await told(env);
  if (!api || !key) return null;
  try {
    const response = await get(`${api}/api/desktop/github/author`, { headers: { authorization: `Bearer ${key}` }, signal: AbortSignal.timeout(15_000) });
    if (!response.ok) return null;
    const answer = await response.json();
    return typeof answer?.name === "string" && answer.name && typeof answer?.email === "string" && answer.email ? { name: answer.name, email: answer.email } : null;
  } catch {
    return null;
  }
}

/** A token for git and gh, or null when there is none. */
export const tokenFor = async (options) => (await askToken(options)).token;

/** Why there is no token, where git and gh show it. */
const explain = (reason) => {
  if (reason) process.stderr.write(`Workstream: ${reason}\n`);
};

async function readStdin() {
  let text = "";
  for await (const chunk of process.stdin) text += chunk;
  return text;
}

async function main([mode, operation]) {
  if (mode === "token") {
    const { token, reason } = await askToken();
    if (token) process.stdout.write(token + "\n");
    else explain(reason);
    return;
  }
  if (mode === "credential") {
    // git also says `store` and `erase`: nothing is kept, so there is nothing to do for either.
    const request = parseCredentialRequest(await readStdin());
    if (operation !== "get" || !isGitHub(request)) return;
    const { token, reason } = await askToken();
    if (token) process.stdout.write(credentialAnswer(token));
    else explain(reason);
    return;
  }
  process.stderr.write("Use: github.mjs credential get | github.mjs token\n");
  process.exitCode = 2;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main(process.argv.slice(2));
