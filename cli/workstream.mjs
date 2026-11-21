#!/usr/bin/env node
// The Workstream command line: sign in through the browser, start a task, follow it. Node 22, nothing else.
//
//   node cli/workstream.mjs login --api https://<the API's address>
//   node cli/workstream.mjs start --team "My team" --env Default "Fix the failing test, then open a pull request"
//
// Signing in is SqlOS's device flow (docs/core-model.md, "The command line"): the API's /cli says which
// issuer, client and scopes; the person opens the printed link, signs in on the web app's device page and
// allows it; this program's poll then gets tokens for a session of the person's own. The session can read
// what the person can and start tasks, and nothing else. It is kept in ~/.config/workstream/ (or
// $XDG_CONFIG_HOME/workstream, or $WORKSTREAM_CONFIG_DIR), readable by this user alone.

import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { chmod, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";
import { USAGE, UsageError, choose, describe, parse, taskLink } from "./args.mjs";

const REFRESH_EARLY_MS = 60_000;
const FOLLOW_EVERY_MS = 2_000;

class Failure extends Error {}

const configDir = () =>
  process.env.WORKSTREAM_CONFIG_DIR || join(process.env.XDG_CONFIG_HOME || join(homedir(), ".config"), "workstream");
const configFile = () => join(configDir(), "config.json");
const credentialsFile = () => join(configDir(), "credentials.json");

async function readJson(path) {
  try {
    return JSON.parse(await readFile(path, "utf8"));
  } catch (error) {
    if (error.code === "ENOENT") return null;
    throw new Failure(`${path} could not be read: ${error.message}`);
  }
}

async function writePrivate(path, value) {
  await mkdir(configDir(), { recursive: true, mode: 0o700 });
  await writeFile(path, `${JSON.stringify(value, null, 2)}\n`, { mode: 0o600 });
  await chmod(path, 0o600);
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const say = (text) => process.stderr.write(`${text}\n`);

async function form(url, fields) {
  const response = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" },
    body: new URLSearchParams(fields),
  });
  let body = {};
  try {
    body = await response.json();
  } catch {
    // The status says what happened.
  }
  return { response, body };
}

/** What this deployment says the command line needs, checked against the address the person gave. */
async function settingsOf(api) {
  let response;
  try {
    response = await fetch(`${api}/cli`, { headers: { Accept: "application/json" } });
  } catch (error) {
    throw new Failure(`${api} did not answer: ${error.cause?.message ?? error.message}`);
  }
  if (!response.ok) throw new Failure(`${api}/cli answered ${response.status}. Is ${api} a Workstream API?`);
  const settings = await response.json();
  // The issuer and the audience are this API's own: never somewhere an answer points to.
  if (settings.issuer !== `${api}/sqlos/auth` || settings.resource !== `${api}/api`) {
    throw new Failure(`${api}/cli names another server (${settings.issuer}). Sign in with the API's own address.`);
  }
  return settings;
}

function openBrowser(url) {
  const [command, args] =
    process.platform === "darwin" ? ["open", [url]]
    : process.platform === "win32" ? ["cmd", ["/c", "start", "", url]]
    : ["xdg-open", [url]];
  try {
    const child = spawn(command, args, { stdio: "ignore", detached: true });
    child.on("error", () => {});
    child.unref();
  } catch {
    // The link is printed; opening it is a convenience.
  }
}

function sessionFrom(tokens, previous) {
  if (!tokens.access_token) throw new Failure("Signing in returned no token.");
  return {
    accessToken: tokens.access_token,
    refreshToken: tokens.refresh_token ?? previous?.refreshToken ?? null,
    expiresAt: Date.now() + (Number(tokens.expires_in) || 300) * 1000,
  };
}

async function login(options) {
  const saved = await readJson(configFile());
  const api = options.api ?? (process.env.WORKSTREAM_API ? process.env.WORKSTREAM_API.replace(/\/+$/, "") : saved?.api);
  if (!api) throw new UsageError("Say which Workstream to sign in to: workstream login --api https://<the API's address>");
  const settings = await settingsOf(api);

  const { response, body: device } = await form(`${settings.issuer}/device_authorization`, {
    client_id: settings.clientId,
    scope: settings.scope,
    resource: settings.resource,
  });
  if (!response.ok) throw new Failure(`Signing in could not start: ${device.error_description ?? device.error ?? response.status}`);

  const link = device.verification_uri_complete ?? device.verification_uri;
  say(`Open this link to sign in, and check that the page shows the code ${device.user_code}:\n\n  ${link}\n`);
  if (!options["no-browser"]) openBrowser(link);
  say("Waiting for you to allow it in the browser…");

  let interval = Math.max(1, Number(device.interval) || 5) * 1000;
  const until = Date.now() + (Number(device.expires_in) || 900) * 1000;
  while (Date.now() < until) {
    await sleep(interval);
    const poll = await form(`${settings.issuer}/token`, {
      grant_type: "urn:ietf:params:oauth:grant-type:device_code",
      client_id: settings.clientId,
      device_code: device.device_code,
      resource: settings.resource,
    });
    if (poll.response.ok) {
      await writePrivate(configFile(), { api, web: settings.web, issuer: settings.issuer, clientId: settings.clientId, resource: settings.resource });
      await writePrivate(credentialsFile(), sessionFrom(poll.body));
      const me = await call("GET", "/api/me");
      say(`Signed in as ${me.name || me.email}${me.name && me.email ? ` (${me.email})` : ""}.`);
      return;
    }
    switch (poll.body.error) {
      case "authorization_pending":
        continue;
      case "slow_down":
        interval += 5000;
        continue;
      case "access_denied":
        throw new Failure("Signing in was turned down in the browser.");
      case "expired_token":
        throw new Failure("The sign-in link expired. Run workstream login again.");
      default:
        throw new Failure(`Signing in failed: ${poll.body.error_description ?? poll.body.error ?? poll.response.status}`);
    }
  }
  throw new Failure("The sign-in link expired. Run workstream login again.");
}

async function logout() {
  const config = await readJson(configFile());
  const session = await readJson(credentialsFile());
  if (config && session?.refreshToken) {
    try {
      // SqlOS ends the session itself, so the tokens stop working everywhere, not only on this disk.
      await fetch(`${config.issuer}/logout`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refreshToken: session.refreshToken }),
      });
    } catch {
      say("The server did not answer; this computer is signed out anyway.");
    }
  }
  await rm(credentialsFile(), { force: true });
  say("Signed out.");
}

async function signedIn() {
  const config = await readJson(configFile());
  const session = await readJson(credentialsFile());
  if (!config || !session) throw new Failure("Not signed in. Run workstream login --api https://<the API's address>");
  return { config, session };
}

/** A token good for at least another minute, refreshing first (and keeping the rotated refresh token) when it is not. */
async function accessToken(force = false) {
  const { config, session } = await signedIn();
  if (!force && session.expiresAt > Date.now() + REFRESH_EARLY_MS) return session.accessToken;
  if (!session.refreshToken) throw new Failure("Your sign-in has ended. Run workstream login.");
  const { response, body } = await form(`${config.issuer}/token`, {
    grant_type: "refresh_token",
    client_id: config.clientId,
    refresh_token: session.refreshToken,
    resource: config.resource,
  });
  if (response.status === 400 || response.status === 401) {
    await rm(credentialsFile(), { force: true });
    throw new Failure("Your sign-in has ended. Run workstream login.");
  }
  if (!response.ok) throw new Failure(`The sign-in server answered ${response.status}.`);
  const refreshed = sessionFrom(body, session);
  await writePrivate(credentialsFile(), refreshed);
  return refreshed.accessToken;
}

async function call(method, path, body, retried = false) {
  const { config } = await signedIn();
  const token = await accessToken(retried);
  let response;
  try {
    response = await fetch(`${config.api}${path}`, {
      method,
      headers: { Accept: "application/json", Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch (error) {
    throw new Failure(`${config.api} did not answer: ${error.cause?.message ?? error.message}`);
  }
  if (response.status === 401 && !retried) return call(method, path, body, true);
  const text = await response.text();
  let answer = null;
  try {
    answer = text ? JSON.parse(text) : null;
  } catch {
    answer = text;
  }
  if (response.status === 401) {
    await rm(credentialsFile(), { force: true });
    throw new Failure("Your sign-in has ended. Run workstream login.");
  }
  if (response.status === 404) throw new Failure("Not found, or not yours to see.");
  if (!response.ok) throw new Failure(answer?.title ?? `${method} ${path} answered ${response.status}.`);
  return answer;
}

const teams = async () => (await call("GET", "/api/teams?pageSize=500")).data;

async function readStdin() {
  if (process.stdin.isTTY) say("Type the prompt, then press Ctrl-D.");
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString("utf8").trim();
}

function print(task, web, json) {
  const link = taskLink(web, task);
  if (json) {
    process.stdout.write(`${JSON.stringify({ id: task.id, teamId: task.teamId, title: task.title, state: task.state, step: task.step, failure: task.failure, link })}\n`);
  } else {
    process.stdout.write(`${link}\n`);
  }
  return link;
}

async function start(options) {
  const prompt = options.prompt ?? (await readStdin());
  if (!prompt) throw new UsageError("The prompt is empty.");
  const { config } = await signedIn();
  const team = choose(await teams(), options.team, "team");
  const environments = (await call("GET", `/api/teams/${encodeURIComponent(team.id)}/environments?pageSize=500`)).data;
  const environment = choose(environments, options.env, "environment");

  let task = await call("POST", `/api/teams/${encodeURIComponent(team.id)}/tasks`, {
    id: randomUUID(),
    environmentId: environment.id,
    title: options.title ?? null,
    prompt,
    promptKey: randomUUID(),
  });
  if (!options.json) print(task, config.web, false);
  say(`Started "${task.title}" in ${team.name}, on ${environment.name}.`);
  if (!options.wait) {
    if (options.json) print(task, config.web, true);
    return;
  }

  let last = "";
  const until = Date.now() + options.timeout * 1000;
  while (true) {
    const words = describe(task);
    if (words !== last) say(task.state === "starting" ? `${words}…` : `${words}.`);
    last = words;
    if (task.state === "ready") break;
    if (task.state !== "starting") {
      if (options.json) print(task, config.web, true);
      throw new Failure("The desktop did not start.");
    }
    if (Date.now() > until) {
      if (options.json) print(task, config.web, true);
      throw new Failure(`Still starting after ${options.timeout} seconds. The link shows it when it is ready.`);
    }
    await sleep(FOLLOW_EVERY_MS);
    task = await call("GET", `/api/tasks/${encodeURIComponent(task.id)}`);
  }
  if (options.json) print(task, config.web, true);
}

async function status(options, [id]) {
  const { config } = await signedIn();
  const task = await call("GET", `/api/tasks/${encodeURIComponent(id)}`);
  if (options.json) {
    print(task, config.web, true);
    return;
  }
  process.stdout.write(`${task.title}\n${describe(task)}\n${taskLink(config.web, task)}\n`);
}

async function tasks(options) {
  const { config } = await signedIn();
  const all = await teams();
  const chosen = options.team === undefined ? all : [choose(all, options.team, "team")];
  const rows = [];
  for (const team of chosen) {
    const page = await call("GET", `/api/teams/${encodeURIComponent(team.id)}/tasks?pageSize=20`);
    for (const task of page.data) rows.push({ team, task });
  }
  if (options.json) {
    process.stdout.write(`${JSON.stringify(rows.map(({ task }) => ({ id: task.id, teamId: task.teamId, title: task.title, state: task.state, step: task.step, link: taskLink(config.web, task) })))}\n`);
    return;
  }
  if (!rows.length) {
    say("No tasks yet.");
    return;
  }
  let team = null;
  for (const row of rows) {
    if (row.team !== team) {
      team = row.team;
      process.stdout.write(`${team.name}\n`);
    }
    process.stdout.write(`  ${describe(row.task).padEnd(28)} ${row.task.title}\n  ${"".padEnd(28)} ${taskLink(config.web, row.task)}\n`);
  }
}

async function envs(options) {
  const all = await teams();
  const chosen = options.team === undefined ? all : [choose(all, options.team, "team")];
  const rows = [];
  for (const team of chosen) {
    const page = await call("GET", `/api/teams/${encodeURIComponent(team.id)}/environments?pageSize=500`);
    for (const environment of page.data) rows.push({ team, environment });
  }
  if (options.json) {
    process.stdout.write(`${JSON.stringify(rows.map(({ team, environment }) => ({
      teamId: team.id, team: team.name, id: environment.id, name: environment.name,
      repositoryUrl: environment.repositoryUrl ?? null, repositoryBranch: environment.repositoryBranch ?? null,
    })))}\n`);
    return;
  }
  let team = null;
  for (const row of rows) {
    if (row.team !== team) {
      team = row.team;
      process.stdout.write(`${team.name} (${team.id})\n`);
    }
    const repository = row.environment.repositoryUrl ? `  ${row.environment.repositoryUrl}${row.environment.repositoryBranch ? ` ${row.environment.repositoryBranch}` : ""}` : "";
    process.stdout.write(`  ${row.environment.name} (${row.environment.id})${repository}\n`);
  }
}

async function main() {
  const { command, options, positionals } = parse(process.argv.slice(2));
  switch (command) {
    case "help":
      process.stdout.write(USAGE);
      return;
    case "version": {
      const { version } = JSON.parse(await readFile(new URL("./package.json", import.meta.url), "utf8"));
      process.stdout.write(`${version}\n`);
      return;
    }
    case "login":
      return login(options);
    case "logout":
      return logout();
    case "start":
      return start(options);
    case "status":
      return status(options, positionals);
    case "tasks":
      return tasks(options);
    case "envs":
      return envs(options);
  }
}

main().catch((error) => {
  if (error instanceof UsageError) {
    say(error.message);
    process.exit(2);
  }
  say(error instanceof Failure ? error.message : `Something went wrong: ${error?.stack ?? error}`);
  process.exit(1);
});
