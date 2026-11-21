#!/usr/bin/env node
// GitHub for the desktop's git and gh. The token is one the API mints for the environment's repository from the
// team's GitHub connection (GET /api/desktop/github/token, with the desktop's key): it reaches that repository
// alone, lasts an hour, and is asked for again each time it is needed, so nothing here writes it anywhere.
//
//   node github.mjs credential get   git's credential helper, for https://github.com (agent.mjs sets it up)
//   node github.mjs token            prints a token, for gh (shims/gh)
//
// A token someone put in the environment's variables as GITHUB_TOKEN or GH_TOKEN is used as it is, without
// asking the API, as it was before the team could connect GitHub.

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
 * A token for the environment's repository: the pasted one, or one the API mints now. Null when there is
 * neither: the task has no GitHub repository, the team has not connected GitHub, or the API cannot be reached.
 */
export async function tokenFor({ env = process.env, told = toldNow, fetch: get = fetch } = {}) {
  const pasted = pastedToken(env);
  if (pasted) return pasted;
  const { api, key } = await told(env);
  if (!api || !key) return null;
  try {
    const response = await get(`${api}/api/desktop/github/token`, { headers: { authorization: `Bearer ${key}` }, signal: AbortSignal.timeout(15_000) });
    if (!response.ok) return null;
    const answer = await response.json();
    return typeof answer?.token === "string" && answer.token ? answer.token : null;
  } catch {
    return null;
  }
}

async function readStdin() {
  let text = "";
  for await (const chunk of process.stdin) text += chunk;
  return text;
}

async function main([mode, operation]) {
  if (mode === "token") {
    const token = await tokenFor();
    if (token) process.stdout.write(token + "\n");
    return;
  }
  if (mode === "credential") {
    // git also says `store` and `erase`: nothing is kept, so there is nothing to do for either.
    const request = parseCredentialRequest(await readStdin());
    if (operation !== "get" || !isGitHub(request)) return;
    const token = await tokenFor();
    if (token) process.stdout.write(credentialAnswer(token));
    return;
  }
  process.stderr.write("Use: github.mjs credential get | github.mjs token\n");
  process.exitCode = 2;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main(process.argv.slice(2));
