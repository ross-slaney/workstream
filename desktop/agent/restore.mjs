// Bringing a desktop back after a stop. Stopping a task's desktop releases the machine and keeps its disk, so
// everything the agent left running (a dev server, a watcher) and every window it had open is gone when the
// desktop starts again. The agent keeps what it needs to bring them back in ~/.workstream/state.json:
//
//   background  the long-running commands the agent started, with the folder each ran in (the harness notes
//               every Bash call made with run_in_background, or ending in a lone `&`);
//   tabs        the pages open in the browser, read from Chromium's debugging port (9222/json) as it runs;
//   files       the files the agent opened in VS Code.
//
// On the first start after a stop, the agent reruns the commands in a terminal window, opens VS Code on the
// workspace with those files, opens the pages once the servers they point at answer, and tells the agent in one
// line what it brought back. This file is the plain part of that, so it can be tested without a desktop.

import { existsSync, writeFileSync } from "node:fs";

export const MAX_COMMANDS = 6;
export const MAX_TABS = 12;
export const MAX_FILES = 12;

/** The address of the agent's own window, which Chromium also lists and which opens itself. */
export const AGENT_WINDOW = "http://127.0.0.1:4141";

/** The iPhone's window (phone.mjs), which is a page of the same browser and is opened again with the phone, not as a tab. */
export const IPHONE_WINDOW = "http://127.0.0.1:7979";

/** A window the desktop opens by itself, which is not one of the person's or the agent's pages. */
const ownWindow = (url) => String(url).startsWith(AGENT_WINDOW) || String(url).startsWith(IPHONE_WINDOW);

/** Whether a command leaves something running: its last line ends in a lone `&`, or `& disown`. */
export function endsInBackground(command) {
  const last = String(command ?? "").trim().split("\n").filter((line) => line.trim()).at(-1) ?? "";
  return /(^|[^&>|])&(\s*disown)?\s*$/.test(last.trim());
}

/** A Bash call that started something long-running, as `{ command, cwd }`, or null for any other. */
export function backgroundCommand(input, cwd) {
  const command = String(input?.command ?? "").trim();
  if (!command) return null;
  if (input.run_in_background !== true && !endsInBackground(command)) return null;
  return { command, cwd: cwd || null };
}

/** The list with this entry last, an equal one taken out, and at most `max` kept: the newest. */
export function remembered(list, entry, max, same = (a, b) => JSON.stringify(a) === JSON.stringify(b)) {
  return [...(list ?? []).filter((kept) => !same(kept, entry)), entry].slice(-max);
}

/**
 * The harness's note after a Bash call: when it started something long-running, it is kept for the next start.
 * Answers whether the state changed.
 */
export function noteBackground(state, hookInput) {
  if (hookInput?.tool_name && hookInput.tool_name !== "Bash") return false;
  const entry = backgroundCommand(hookInput?.tool_input, hookInput?.cwd);
  if (!entry) return false;
  state.background = remembered(state.background, entry, MAX_COMMANDS);
  return true;
}

/** A file the agent opened in the editor, kept for the next start. */
export function noteFile(state, file) {
  if (!file) return false;
  state.files = remembered(state.files, String(file), MAX_FILES);
  return true;
}

/** The pages worth opening again, from Chromium's list of targets: web pages and files, not its own or the agent's window. */
export function pagesOf(targets) {
  return (Array.isArray(targets) ? targets : [])
    .filter((target) => target?.type === "page" && /^(https?|file):/i.test(target.url ?? "") && !ownWindow(target.url))
    .map((target) => target.url)
    .filter((url, index, all) => all.indexOf(url) === index)
    .slice(0, MAX_TABS);
}

/** Records the open pages. Answers whether they changed. */
export function noteTabs(state, targets) {
  const tabs = pagesOf(targets);
  if (JSON.stringify(tabs) === JSON.stringify(state.tabs ?? [])) return false;
  state.tabs = tabs;
  return true;
}

/** The port of a page on this desktop (localhost, 127.0.0.1, 0.0.0.0, [::1]), which a rerun command may serve; else null. */
export function localPort(url) {
  try {
    const address = new URL(url);
    if (!["localhost", "127.0.0.1", "0.0.0.0", "[::1]"].includes(address.hostname)) return null;
    return Number(address.port || (address.protocol === "https:" ? 443 : 80));
  } catch {
    return null;
  }
}

/** What to bring back, from what was kept: null when there is nothing. */
export function restorePlan(state) {
  const plan = { commands: [...(state.background ?? [])], tabs: [...(state.tabs ?? [])], files: [...(state.files ?? [])] };
  return plan.commands.length + plan.tabs.length + plan.files.length > 0 ? plan : null;
}

const count = (n, one, many) => `${n} ${n === 1 ? one : many}`;

/** The one line the agent is told about it, so it knows the desktop was stopped and what is running again. */
export function restoredLine(plan, workspace) {
  if (!plan) return null;
  const parts = [];
  if (plan.commands.length) parts.push(`reran ${count(plan.commands.length, "command", "commands")} you had left running, in the terminal window titled "Restored" (${plan.commands.map((x) => `\`${x.command.split("\n")[0].slice(0, 80)}\`${x.cwd ? ` in ${x.cwd}` : ""}`).join("; ")})`);
  if (plan.tabs.length) parts.push(`reopened ${count(plan.tabs.length, "page", "pages")} in the browser (${plan.tabs.join(", ")})`);
  parts.push(`opened VS Code on ${workspace}${plan.files.length ? ` with ${count(plan.files.length, "file", "files")}` : ""}`);
  return `This desktop was stopped and started again, which ends every program. Workstream ${parts.join(", ")}. Check that they are up before you rely on them.`;
}

const shellQuote = (value) => `'${String(value).replaceAll("'", `'\\''`)}'`;

/** A command without the `&` that sent it to the background: the script below runs each in the background itself. */
export const inForeground = (command) => String(command).trim().replace(/\s*&(\s*disown)?\s*$/, "");

/**
 * The script the "Restored" terminal runs: the environment's variables, then each command in its folder, in
 * the background, its output in the window. It waits for them, and the window stays open when they end.
 */
export function restartScript(commands, envFile) {
  const lines = ["#!/usr/bin/env bash", `[ -f ${shellQuote(envFile)} ] && . ${shellQuote(envFile)}`, 'echo "Starting again what was running before this desktop stopped."', ""];
  for (const { command, cwd } of commands) {
    const body = inForeground(command);
    lines.push(`echo; echo ${shellQuote(`$ ${body.split("\n")[0]}`)}`);
    lines.push(`( ${cwd ? `cd ${shellQuote(cwd)} && ` : ""}${body}\n) &`);
  }
  lines.push("", "wait", 'echo; echo "Everything here has stopped."', "exec bash");
  return lines.join("\n") + "\n";
}

/**
 * Whether the machine itself started since the agent last ran: a mark in memory (`/dev/shm`, emptied when the
 * machine or container stops) is missing, and is written now. The agent restarting on a running desktop
 * finds its mark and brings nothing back, since nothing stopped.
 */
export function machineStarted(mark) {
  if (existsSync(mark)) return false;
  try {
    writeFileSync(mark, new Date().toISOString());
  } catch {
    // Nowhere to keep the mark: every start counts as the machine's.
  }
  return true;
}
