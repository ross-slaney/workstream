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
// A machine made to prepare an environment does none of that. The API tells it so (`prepare` in its setup):
// it clones the repository and runs the setup script once, says how that went, and leaves nothing of a task
// on the disk before it says it is done, because the disk is then saved for the environment's tasks to start
// from. A task's machine made from such a save finds ~/.workstream/prepared.json, pulls the branch's newer
// commits and does not run the setup script again.
//
// Everything but the two things it is told comes from the API.

import { spawn, execFile } from "node:child_process";
import { randomUUID } from "node:crypto";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { appendFile, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import { panelFor } from "./panel.mjs";
import { startedFromLine, tail, usedBytes } from "./prepared.mjs";
import { told } from "./told.mjs";
import { startWindow } from "./window.mjs";

const run = promisify(execFile);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const home = os.homedir();
const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const dir = path.join(home, ".workstream");
const stateFile = path.join(dir, "state.json");
const conversationFile = path.join(dir, "conversation.jsonl");
const preparedFile = path.join(dir, "prepared.json");
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
  return response.status === 204 ? null : response.json();
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

/**
 * How long to wait before asking again after the API refused. 404: this desktop has no task yet. It is a
 * spare, kept running so a task can start on it at once, so it asks again twice a second. 401: its key has
 * ended, because its task was deleted or the spare was not needed; the machine goes with it, so it asks slowly.
 */
const again = (error, wait) => (error.status === 404 ? 500 : error.status === 401 ? 30_000 : Math.min(wait * 2, 15_000));

/** False on a machine that prepares an environment: it has no task to exchange with. */
let exchanging = true;

async function exchangeForever() {
  let wait = 1000;
  while (exchanging) {
    try {
      await exchange();
      wait = 1000;
    } catch (error) {
      wait = again(error, wait);
      if (error.status !== 404) log("Exchange failed:", error.message);
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

/** Where the repository is cloned: ~/work/<its name>. */
const repositoryFolder = (repository) =>
  path.join(home, "work", repository.url.replace(/\/+$/, "").split("/").pop().replace(/\.git$/, "") || "repository");

const git = (folder, args, timeout = 60_000) => run("git", ["-C", folder, ...args], { timeout }).then((result) => result.stdout.trim());

/**
 * The desktop was made from the environment's saved setup: the repository is there and the setup script has
 * run. The branch's newer commits are pulled, and the conversation's first line says where it started from.
 */
async function startFromSave(saved, setup, target) {
  let newer = null;
  let branch = saved.branch ?? null;
  if (saved.commit && existsSync(target)) {
    try {
      await run("git", ["-C", target, "pull", "--ff-only"], { timeout: 5 * 60_000 });
      newer = Number(await git(target, ["rev-list", "--count", `${saved.commit}..HEAD`]));
      branch ??= await git(target, ["rev-parse", "--abbrev-ref", "HEAD"]);
    } catch (error) {
      log("Could not pull the newer commits:", String(error.stderr || error.message).trim());
    }
  }
  say("activity", startedFromLine({ preparedAt: saved.preparedAt, commit: saved.commit, branch: branch ?? setup.repository?.branch ?? null, newer }));
}

/** The part of preparing that needs no window: the variables and the repository. Answers the folder the agent works in. */
async function prepareFiles(setup, saved) {
  await writeVariables(setup.variables);
  let workspace = path.join(home, "work");
  await mkdir(workspace, { recursive: true });
  if (saved) await startFromSave(saved, setup, setup.repository ? repositoryFolder(setup.repository) : workspace);
  if (setup.repository) {
    const target = repositoryFolder(setup.repository);
    if (!existsSync(target)) {
      say("activity", `Cloning ${setup.repository.url}`);
      const args = ["clone", ...(setup.repository.branch ? ["--branch", setup.repository.branch] : []), setup.repository.url, target];
      await run("git", args, { timeout: 15 * 60_000 }).catch((error) =>
        say("text", `I could not clone the repository: ${String(error.stderr || error.message).trim().split("\n").slice(-3).join(" ")}`));
    }
    if (existsSync(target)) workspace = target;
  }
  return workspace;
}

/**
 * The part that opens windows, once XFCE is up: the environment's apps on the panel, the setup script in a
 * terminal, then the browser and the editor.
 */
async function prepareWindows(setup, workspace) {
  await sessionReady();
  await showApps(setup.apps);
  if (setup.setupScript && !state.prepared) {
    say("activity", "Running the setup script");
    const code = await runSetupScript(setup.setupScript, workspace, setup.variables);
    if (code !== 0) say("text", `The setup script stopped with an error (exit ${code}). Its output is in ~/.workstream/setup.log.`);
  }
  state.prepared = true;
  await saveState();
  // The browser is always there: it is the one the agent's browser tools drive. A spare opened it while it waited.
  if (!(await browserAnswers())) start("chromium", ["about:blank"], setup.variables);
  if (setup.apps.includes("vscode")) start("code", [workspace], setup.variables);
}

/** Whether the browser's debugging port answers, which is what the agent's browser tools attach to. */
const browserAnswers = () => fetch("http://127.0.0.1:9222/json/version", { signal: AbortSignal.timeout(1000) }).then((response) => response.ok, () => false);

/** The browser is up when that port answers. */
async function browserUp() {
  for (let waited = 0; waited < 300; waited++) {
    if (await browserAnswers()) return;
    await sleep(200);
  }
  log("The browser did not come up in a minute.");
}

/** The agent's own program, the largest file it will start: the one named `claude` among the harness's packages. */
function harnessProgram() {
  const packages = path.join(root, "agent/node_modules/@anthropic-ai");
  try {
    return readdirSync(packages).map((name) => path.join(packages, name, "claude")).find((file) => existsSync(file)) ?? null;
  } catch {
    return null;
  }
}

/**
 * A spare has time before it has a task, and uses it: the agent's program is read off the disk once, so
 * starting it later does not wait on a disk nothing has read yet, and the browser is opened.
 */
let warmed = false;
function warmUp() {
  if (warmed) return;
  warmed = true;
  log("No task yet: this desktop is a spare. Getting ready while it waits.");
  const program = harnessProgram();
  if (program) spawn("sh", ["-c", `cat ${shellQuote(program)} > /dev/null`], { detached: true, stdio: "ignore" }).on("error", () => {}).unref();
  sessionReady().then(() => start("chromium", ["about:blank"], {})).catch(() => {});
}

/** Run a script as the desktop's person, out of sight, keeping what it writes. Answers its exit code and its output. */
function runScript(script, folder, variables, onOutput) {
  return new Promise((resolve) => {
    let output = "";
    const child = spawn("bash", ["-lc", script], { cwd: folder, env: { ...process.env, ...variables }, stdio: ["ignore", "pipe", "pipe"] });
    const keep = (chunk) => {
      output = tail(output + chunk.toString());
      onOutput(output);
    };
    child.stdout.on("data", keep);
    child.stderr.on("data", keep);
    // An hour at most, as on a task's desktop.
    const timer = setTimeout(() => child.kill("SIGKILL"), 60 * 60_000);
    child.on("error", (error) => {
      clearTimeout(timer);
      resolve({ code: 127, output: tail(`${output}\n${error.message}`) });
    });
    child.on("close", (code, signal) => {
      clearTimeout(timer);
      resolve({ code: signal === "SIGKILL" ? 124 : code ?? 1, output });
    });
  });
}

/**
 * This machine prepares an environment: the repository cloned and the setup script run once, for a save
 * that tasks start from. Nothing of a task is ever here, and nothing of the preparing is left behind: before
 * it says it is done, the variables, the git token, the logs and the browser's profile are removed, and the
 * one file left in ~/.workstream says when and at which commit it was prepared.
 */
async function prepareForSave(setup) {
  exchanging = false;
  log(`This desktop prepares the environment ${setup.title}, to be saved.`);
  const report = (body) => call("POST", "/api/desktop/setup", body).catch((error) => log("Could not say how the setup is going:", error.message));
  await writeVariables(setup.variables);
  let workspace = path.join(home, "work");
  await mkdir(workspace, { recursive: true });
  let commit = null;
  let branch = null;
  if (setup.repository) {
    const target = repositoryFolder(setup.repository);
    await rm(target, { recursive: true, force: true });
    const args = ["clone", ...(setup.repository.branch ? ["--branch", setup.repository.branch] : []), setup.repository.url, target];
    await report({ state: "running", output: `git clone ${setup.repository.url}` });
    try {
      await run("git", args, { timeout: 15 * 60_000 });
    } catch (error) {
      await report({ state: "failed", exit: null, output: tail(String(error.stderr || error.message).trim()) });
      return;
    }
    workspace = target;
    commit = await git(target, ["rev-parse", "HEAD"]).catch(() => null);
    branch = await git(target, ["rev-parse", "--abbrev-ref", "HEAD"]).catch(() => null);
  }
  let output = "";
  if (setup.setupScript) {
    let sent = 0;
    const result = await runScript(setup.setupScript, workspace, setup.variables, (written) => {
      output = written;
      // What it writes reaches the environment's page every few seconds.
      if (Date.now() - sent > 5000) {
        sent = Date.now();
        report({ state: "running", output: written });
      }
    });
    output = result.output;
    if (result.code !== 0) {
      await report({ state: "failed", exit: result.code, output });
      return;
    }
  }
  await clearForSave(setup.variables);
  await writeFile(preparedFile, JSON.stringify({ preparedAt: new Date().toISOString(), commit, branch }));
  await run("sync").catch(() => {});
  const bytes = usedBytes(await run("df", ["-B1", "--output=used", "/"]).then((result) => result.stdout, () => ""));
  await report({ state: "done", exit: 0, output, commit, bytes });
  log("Prepared. The machine is about to be stopped and its disk saved.");
}

/** Remove what is the preparing's and nobody's after it: everything in ~/.workstream, the git token, the shell's history. */
async function clearForSave(variables) {
  const token = variables.GITHUB_TOKEN ?? variables.GH_TOKEN;
  if (token) await run("git", ["config", "--global", "--remove-section", `url.https://x-access-token:${token}@github.com/`]).catch(() => {});
  for (const name of existsSync(dir) ? readdirSync(dir) : []) await rm(path.join(dir, name), { recursive: true, force: true });
  await rm(path.join(home, ".bash_history"), { force: true });
}

/** What the machine that prepared the save left behind, when this desktop was made from one. */
function preparedSave() {
  try {
    return existsSync(preparedFile) ? JSON.parse(readFileSync(preparedFile, "utf8")) : null;
  } catch {
    return null;
  }
}

async function setupFromApi() {
  let wait = 1000;
  while (true) {
    try {
      return await call("GET", "/api/desktop/setup");
    } catch (error) {
      wait = again(error, wait);
      if (error.status === 404) warmUp();
      else log("Could not read the setup:", error.message);
      await sleep(wait);
    }
  }
}

// Exchanging starts before anything else: it tells the API the desktop is up, and brings what the person asked for.
exchangeForever();
// The harness is the slow part to load, so it loads while the desktop is being prepared.
const harnessLoading = import("./harness.mjs");
harnessLoading.catch(() => {});
const setup = await setupFromApi();
if (setup.prepare) {
  await prepareForSave(setup);
  // Its work is done: it waits, writing nothing, for the operator to stop it.
  await new Promise(() => {});
}
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
// The desktop is prepared in two parts. The agent waits for the first, which says where it works. It waits
// for the second only when there is a setup script, whose result it must find in place; otherwise it
// starts on what the person asked while XFCE is still drawing itself.
preparing = true;
// Made from a saved setup, and not told so yet: the setup script ran when the save was made.
const saved = state.startedFrom ? null : preparedSave();
if (saved) {
  state.prepared = true;
  state.startedFrom = saved;
  await saveState();
}
const scripted = Boolean(setup.setupScript && !state.prepared);
const workspace = await prepareFiles(setup, saved);
const windows = prepareWindows(setup, workspace).catch((error) => log("Preparing the desktop's windows failed:", error));
if (scripted) await windows;
preparing = false;
const { startHarness } = await harnessLoading;
harness = startHarness({
  setup, key, workspace, state, saveState, say, prompts, log,
  setWorking: (value) => {
    working = value;
  },
  // A browser tool waits for the browser, which may still be opening when the agent first reaches for it.
  browserReady: () => windows.then(browserUp),
});
log(`Ready. Working in ${workspace}.`);
