#!/usr/bin/env node
// The agent on a Workstream desktop. One process, started with the desktop's session (desktop/session.sh)
// while XFCE is still drawing itself, that:
//
//   1. finds out what the desktop is told: its API's address and its key (told.mjs);
//   2. exchanges with the API about once a second, with the key: it hands over what the agent said and did
//      and takes what people wrote. The first exchange tells the API the desktop is up;
//   3. prepares the desktop from its environment, where the person can see it: the variables, the
//      repository, the apps on the panel, the setup script in a terminal window;
//   4. runs the agent (harness.mjs) and feeds it what people write;
//   5. serves the agent's own window on the desktop (window.mjs), which shows the same conversation.
//
// Everything but the two things it is told comes from the API.

import { spawn, execFile } from "node:child_process";
import { randomUUID } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { appendFile, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import { panelFor } from "./panel.mjs";
import { told } from "./told.mjs";
import { startWindow } from "./window.mjs";

const run = promisify(execFile);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const home = os.homedir();
const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const dir = path.join(home, ".workstream");
const stateFile = path.join(dir, "state.json");
const conversationFile = path.join(dir, "conversation.jsonl");
const log = (...args) => console.log(new Date().toISOString(), ...args);

const { api, key } = await told({ log });
log(`Starting. The API is ${api}.`);

await mkdir(dir, { recursive: true });
/** Kept across restarts of the desktop: where it is in the conversation, and what it has already prepared. */
const state = { after: 0, interrupts: 0, sessionId: null, prepared: false, ...(existsSync(stateFile) ? JSON.parse(readFileSync(stateFile, "utf8")) : {}) };
const saveState = () => writeFile(stateFile, JSON.stringify(state));

/** The conversation as this desktop has seen it, for the agent's window. */
const conversation = existsSync(conversationFile)
  ? readFileSync(conversationFile, "utf8").split("\n").filter(Boolean).slice(-500).map((line) => JSON.parse(line))
  : [];
/** What the agent said and did that the API does not have yet. */
const outbox = [];
let working = false;
let preparing = false;
let title = "";
let harness = null;

function remember(author, kind, text) {
  const entry = { id: (conversation.at(-1)?.id ?? 0) + 1, author, kind, text, at: new Date().toISOString() };
  conversation.push(entry);
  if (conversation.length > 1000) conversation.splice(0, conversation.length - 1000);
  appendFile(conversationFile, JSON.stringify(entry) + "\n").catch(() => {});
}

/** Something for the conversation: the agent's words or one line about what it did. */
function say(kind, text, author = "agent") {
  outbox.push({ key: randomUUID(), author, kind, text });
  remember(author, kind, text);
}

/** What people wrote, waiting for the agent. `take` gives up its place when the session that asked ends. */
const prompts = {
  waiting: [],
  taker: null,
  get size() {
    return this.waiting.length;
  },
  push(text) {
    if (this.taker) {
      const give = this.taker;
      this.taker = null;
      give(text);
    } else {
      this.waiting.push(text);
    }
  },
  take(signal) {
    if (this.waiting.length > 0) return Promise.resolve(this.waiting.shift());
    return new Promise((resolve, reject) => {
      this.taker = resolve;
      signal.addEventListener("abort", () => {
        if (this.taker === resolve) this.taker = null;
        reject(new Error("The session ended."));
      }, { once: true });
    });
  },
};

async function call(method, route, body) {
  const response = await fetch(`${api}${route}`, {
    method,
    headers: { authorization: `Bearer ${key}`, ...(body ? { "content-type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
    signal: AbortSignal.timeout(30_000),
  });
  if (!response.ok) throw Object.assign(new Error(`${method} ${route} answered ${response.status}`), { status: response.status });
  return response.json();
}

/** One exchange: hand over what is new, take what people wrote, hear whether they asked the agent to stop. */
async function exchange() {
  const sending = outbox.slice(0, 50);
  const reply = await call("POST", "/api/desktop/exchange", { working: working || preparing, after: state.after, messages: sending });
  outbox.splice(0, sending.length);
  title = reply.title ?? title;
  let changed = false;
  for (const prompt of reply.prompts ?? []) {
    state.after = Math.max(state.after, prompt.id);
    changed = true;
    remember("person", "text", prompt.text);
    prompts.push(prompt.text);
  }
  if ((reply.interrupts ?? 0) !== state.interrupts) {
    if (reply.interrupts > state.interrupts) harness?.interrupt();
    state.interrupts = reply.interrupts;
    changed = true;
  }
  if (changed) await saveState();
}

async function exchangeForever() {
  let wait = 1000;
  while (true) {
    try {
      await exchange();
      wait = 1000;
    } catch (error) {
      // 401 or 404: the task was deleted, or this key was replaced. Keep asking, slowly; the machine goes with the task.
      wait = error.status === 401 || error.status === 404 ? 30_000 : Math.min(wait * 2, 15_000);
      log("Exchange failed:", error.message);
    }
    await sleep(wait);
  }
}

const shellQuote = (value) => `'${String(value).replaceAll("'", `'\\''`)}'`;

/** The environment's variables, for every shell on the desktop, and a token for git where one is given. */
async function writeVariables(variables) {
  const lines = Object.entries(variables).map(([name, value]) => `export ${name}=${shellQuote(value)}`);
  await writeFile(path.join(dir, "env.sh"), lines.join("\n") + "\n", { mode: 0o600 });
  const token = variables.GITHUB_TOKEN ?? variables.GH_TOKEN;
  if (token) {
    await run("git", ["config", "--global", `url.https://x-access-token:${token}@github.com/.insteadOf`, "https://github.com/"]).catch((error) => log("git config failed:", error.message));
  }
}

/** Run the setup script in a terminal on the desktop, so whoever is watching sees it happen. Answers its exit code. */
async function runSetupScript(script, folder, variables) {
  const file = path.join(dir, "setup.sh");
  const exit = path.join(dir, "setup.exit");
  await writeFile(file, script, { mode: 0o700 });
  await rm(exit, { force: true });
  const command = `cd ${shellQuote(folder)} && bash ${shellQuote(file)} 2>&1 | tee ${shellQuote(path.join(dir, "setup.log"))}; echo \${PIPESTATUS[0]} > ${shellQuote(exit)}; sleep 3`;
  const terminal = spawn("xfce4-terminal", ["--title", "Setup", "--geometry", "110x30", "-x", "bash", "-lc", command], {
    detached: true, stdio: "ignore", env: { ...process.env, ...variables },
  });
  // Without a terminal (no display yet), the script still runs.
  terminal.on("error", () => spawn("bash", ["-lc", command], { detached: true, stdio: "ignore", env: { ...process.env, ...variables } }).unref());
  terminal.unref();
  for (let waited = 0; waited < 60 * 60; waited++) {
    if (existsSync(exit)) return Number((await readFile(exit, "utf8")).trim() || "1");
    await sleep(1000);
  }
  return 124;
}

const start = (command, args, variables) =>
  spawn(command, args, { detached: true, stdio: "ignore", env: { ...process.env, ...variables } }).on("error", (error) => log(`${command} did not start:`, error.message)).unref();

/** Whether a program of this name is running in the session. */
const running = (name) => run("pgrep", ["-x", name]).then(() => true, () => false);

/**
 * XFCE is up: its window manager answers, so a window opened now gets a frame and a place, and its panel is
 * on screen. The agent starts beside XFCE and reaches this point first.
 */
async function sessionReady() {
  for (let waited = 0; waited < 300; waited++) {
    const managed = await run("wmctrl", ["-m"], { timeout: 2000 }).then(() => true, () => false);
    if (managed && (await running("xfce4-panel"))) return;
    await sleep(100);
  }
  log("XFCE did not come up in thirty seconds. Going on without it.");
}

/**
 * The panel shows the environment's apps. The desktop is drawn with the default apps (image/configure.sh);
 * the panel is written again, and started again, only when the environment chose others.
 */
async function showApps(apps) {
  const catalog = JSON.parse(readFileSync(path.join(root, "apps.json"), "utf8"));
  const panel = panelFor(catalog, apps, state.apps);
  if (!panel.changed) return;
  await run(path.join(root, "panel.sh"), panel.apps, { timeout: 20_000 }).catch((error) => log("The panel was not written:", error.message));
  state.apps = panel.apps;
  await saveState();
}

/** Make the desktop what its environment says, once. Answers the folder the agent works in. */
async function prepare(setup) {
  preparing = true;
  try {
    await writeVariables(setup.variables);
    let workspace = path.join(home, "work");
    await mkdir(workspace, { recursive: true });
    if (setup.repository) {
      const name = setup.repository.url.replace(/\/+$/, "").split("/").pop().replace(/\.git$/, "") || "repository";
      const target = path.join(workspace, name);
      if (!existsSync(target)) {
        say("activity", `Cloning ${setup.repository.url}`);
        const args = ["clone", ...(setup.repository.branch ? ["--branch", setup.repository.branch] : []), setup.repository.url, target];
        await run("git", args, { timeout: 15 * 60_000 }).catch((error) =>
          say("text", `I could not clone the repository: ${String(error.stderr || error.message).trim().split("\n").slice(-3).join(" ")}`));
      }
      if (existsSync(target)) workspace = target;
    }
    // Everything from here opens a window.
    await sessionReady();
    await showApps(setup.apps);
    if (setup.setupScript && !state.prepared) {
      say("activity", "Running the setup script");
      const code = await runSetupScript(setup.setupScript, workspace, setup.variables);
      if (code !== 0) say("text", `The setup script stopped with an error (exit ${code}). Its output is in ~/.workstream/setup.log.`);
    }
    state.prepared = true;
    await saveState();
    // The browser is always there: it is the one the agent's browser tools drive.
    start("chromium", ["about:blank"], setup.variables);
    if (setup.apps.includes("vscode")) start("code", [workspace], setup.variables);
    return workspace;
  } finally {
    preparing = false;
  }
}

async function setupFromApi() {
  while (true) {
    try {
      return await call("GET", "/api/desktop/setup");
    } catch (error) {
      log("Could not read the setup:", error.message);
      await sleep(error.status === 401 || error.status === 404 ? 30_000 : 3000);
    }
  }
}

// Exchanging starts before anything else: it tells the API the desktop is up, and brings what the person asked for.
exchangeForever();
// The harness is the slow part to load, so it loads while the desktop is being prepared.
const harnessLoading = import("./harness.mjs");
harnessLoading.catch(() => {});
const setup = await setupFromApi();
title = setup.title;
startWindow({
  conversation,
  status: () => ({ title, working: working || preparing }),
  onSay(text) {
    say("text", text, "person");
    prompts.push(text);
  },
  onInterrupt: () => harness?.interrupt(),
  log,
});
const workspace = await prepare(setup);
const { startHarness } = await harnessLoading;
harness = startHarness({
  setup, key, workspace, state, saveState, say, prompts, log,
  setWorking: (value) => {
    working = value;
  },
});
log(`Ready. Working in ${workspace}.`);
