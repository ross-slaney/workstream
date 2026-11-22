// The environment's setup script on a desktop: how long it may run, how it is stopped when it runs too long,
// and what is said about it. Kept apart from agent.mjs so it can be tested without a desktop.
//
// The script runs in two places with one limit, which the API sets (`setupMinutes` in the desktop's setup,
// Workstream:Desktops:SetupMinutes): on a task's own desktop, in a terminal window the person can watch
// (followScript), and on a machine preparing an environment's saved setup, out of sight (runScript). Either
// way the desktop says how it is going (`POST /api/desktop/setup`): what the script has written every few
// seconds, then how it ended.

import { spawn } from "node:child_process";
import { closeSync, existsSync, fstatSync, openSync, readFileSync, readSync } from "node:fs";

/** The exit code of a script stopped for running too long, as `timeout` gives. */
export const TIMED_OUT = 124;
/** As much of the script's output as the API keeps. */
export const OUTPUT_LENGTH = 4000;

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** How long the setup script may run, in minutes: what the API says, or 45 when it says nothing. */
export const setupMinutes = (setup) => (Number.isFinite(setup?.setupMinutes) && setup.setupMinutes > 0 ? setup.setupMinutes : 45);

/**
 * What a program wrote, as a person reads it away from a terminal: without the codes that colour it and move
 * the cursor, and with a line that was written over and over (a progress bar) as it last stood.
 */
export function plain(text) {
  return String(text ?? "")
    // Colours, cursor moves and window titles.
    .replace(/\u001b\[[0-9;?]*[ -/]*[@-~]/g, "")
    .replace(/\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)/g, "")
    .replace(/\u001b[@-Z\\-_]/g, "")
    .replace(/\r\n/g, "\n")
    .split("\n")
    .map((line) => line.slice(line.lastIndexOf("\r") + 1))
    .join("\n");
}

/** The last part of what was written, as much as is kept. */
export const tail = (text, length = OUTPUT_LENGTH) => (text.length <= length ? text : text.slice(-length));

/** The end of a file, read from its end so a log of any size costs the same. Empty when it is not there. */
export function tailOfFile(file, length = OUTPUT_LENGTH) {
  if (!existsSync(file)) return "";
  let handle;
  try {
    handle = openSync(file, "r");
    const size = fstatSync(handle).size;
    // Four bytes a character at most, and room for the codes plain() removes.
    const want = Math.min(size, length * 8);
    const buffer = Buffer.alloc(want);
    readSync(handle, buffer, 0, want, size - want);
    return tail(plain(buffer.toString("utf8")).trimEnd(), length);
  } catch {
    return "";
  } finally {
    if (handle !== undefined) closeSync(handle);
  }
}

const alive = (target) => {
  try {
    process.kill(target, 0);
    return true;
  } catch (error) {
    return error.code === "EPERM";
  }
};

/**
 * Stop a script and everything it started: its whole process group is asked to end, and killed if it has
 * not after a few seconds. The script must lead its group, which one started by a terminal window, or spawned
 * detached, does. Containers it started are Docker's, not its own, and stay.
 */
export async function stopGroup(pid, { grace = 5000 } = {}) {
  if (!Number.isInteger(pid) || pid <= 1) return;
  // The group when there is one of that name; otherwise the one process.
  const target = alive(-pid) ? -pid : pid;
  const signal = (name) => {
    try {
      process.kill(target, name);
    } catch {
      // Already gone.
    }
  };
  signal("SIGTERM");
  for (let waited = 0; waited < grace && alive(target); waited += 100) await sleep(100);
  if (alive(target)) signal("SIGKILL");
}

/**
 * Run a script out of sight, keeping the last of what it writes. Answers its exit code (124 when it ran past
 * the limit and was stopped, with everything it started) and that output.
 * @param {string} script
 * @param {{ cwd: string, env: object, limitMs: number, onOutput?: (output: string) => void }} how
 * @returns {Promise<{ code: number, output: string }>}
 */
export function runScript(script, { cwd, env, limitMs, onOutput = () => {} }) {
  return new Promise((resolve) => {
    let output = "";
    let timedOut = false;
    // Detached, so it leads a process group of its own and the whole of it can be stopped.
    const child = spawn("bash", ["-lc", script], { cwd, env, stdio: ["ignore", "pipe", "pipe"], detached: true });
    const keep = (chunk) => {
      output = tail(plain(output + chunk.toString()));
      onOutput(output);
    };
    child.stdout.on("data", keep);
    child.stderr.on("data", keep);
    const timer = setTimeout(() => {
      timedOut = true;
      stopGroup(child.pid);
    }, limitMs);
    child.on("error", (error) => {
      clearTimeout(timer);
      resolve({ code: 127, output: tail(`${output}\n${error.message}`.trim()) });
    });
    // When the script itself ends, not when its output closes: a program it left running in the background
    // keeps the output open, and the setup is over all the same. A moment is given for its last lines.
    child.on("exit", (code) => {
      clearTimeout(timer);
      setTimeout(() => resolve({ code: timedOut ? TIMED_OUT : code ?? 1, output: output.trimEnd() }), 300);
    });
  });
}

/**
 * Follow a script that runs in a terminal window on the desktop: it writes what it prints to a log, its
 * process id to a file when it starts and its exit code to another when it ends. Every few seconds what it
 * has written is handed over; past the limit it is stopped, with everything it started. Answers as runScript.
 * @param {{ exitFile: string, logFile: string, pidFile: string, limitMs: number, onOutput?: (output: string) => void, every?: number, poll?: number }} how
 * @returns {Promise<{ code: number, output: string }>}
 */
export async function followScript({ exitFile, logFile, pidFile, limitMs, onOutput = () => {}, every = 5000, poll = 1000 }) {
  const started = Date.now();
  let said = started;
  let last = null;
  while (true) {
    if (existsSync(exitFile)) {
      const code = Number(readFileSync(exitFile, "utf8").trim() || "1");
      return { code: Number.isInteger(code) ? code : 1, output: tailOfFile(logFile) };
    }
    if (Date.now() - started >= limitMs) {
      const pid = existsSync(pidFile) ? Number(readFileSync(pidFile, "utf8").trim()) : NaN;
      await stopGroup(pid);
      return { code: TIMED_OUT, output: tailOfFile(logFile) };
    }
    if (Date.now() - said >= every) {
      said = Date.now();
      const output = tailOfFile(logFile);
      if (output !== last) onOutput((last = output));
    }
    await sleep(poll);
  }
}

/** How a setup that did not end well is said, in a sentence. Null when it ended well. */
export function failureWords(code, minutes) {
  if (code === 0) return null;
  return code === TIMED_OUT
    ? `The setup script did not finish in ${minutes} minutes and was stopped.`
    : `The setup script stopped with an error (exit ${code}).`;
}

/** What the agent writes in the conversation when the setup did not end well: the sentence, and the script's last lines. */
export function failureMessage(code, minutes, output, lines = 12) {
  const words = failureWords(code, minutes);
  if (!words) return null;
  const last = String(output ?? "").trimEnd().split("\n").slice(-lines).join("\n").trim();
  return last ? `${words} Its last lines:\n\n\`\`\`\n${last}\n\`\`\`\n\nAll of it is in ~/.workstream/setup.log.` : `${words} It wrote nothing. Its log is ~/.workstream/setup.log.`;
}
