import { test } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { failureMessage, failureWords, followScript, plain, runScript, setupMinutes, stopGroup, tailOfFile, TIMED_OUT } from "./setup.mjs";
import { setupEnded } from "./instructions.mjs";

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const folder = () => mkdtempSync(path.join(os.tmpdir(), "workstream-setup-"));
const running = (pid) => {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
};

test("the setup script may run for as long as the API says, and 45 minutes when it says nothing", () => {
  assert.equal(setupMinutes({ setupMinutes: 90 }), 90);
  assert.equal(setupMinutes({}), 45);
  assert.equal(setupMinutes({ setupMinutes: 0 }), 45);
  assert.equal(setupMinutes(null), 45);
});

test("what a script wrote is read without colours, and a progress line as it last stood", () => {
  assert.equal(plain("\u001b[32mok\u001b[0m done"), "ok done");
  assert.equal(plain("10%\r50%\r100%\nnext\r\n"), "100%\nnext\n");
  assert.equal(plain("\u001b]0;title\u0007text"), "text");
});

test("the end of a log is read however long the log is, and a log that is not there is empty", () => {
  const dir = folder();
  const file = path.join(dir, "setup.log");
  assert.equal(tailOfFile(file), "");
  writeFileSync(file, `${"x".repeat(100_000)}\nthe last line\n`);
  const end = tailOfFile(file, 200);
  assert.equal(end.length, 200);
  assert.ok(end.endsWith("the last line"));
});

test("a script that ends well answers 0 with what it wrote, from the folder and with the variables it was given", async () => {
  const dir = folder();
  const seen = [];
  const result = await runScript('echo "in $(pwd) with $SECRET"', { cwd: dir, env: { ...process.env, SECRET: "s3cret" }, limitMs: 10_000, onOutput: (output) => seen.push(output) });
  assert.equal(result.code, 0);
  assert.match(result.output, /with s3cret$/);
  assert.ok(result.output.includes(path.basename(dir)));
  assert.ok(seen.length > 0);
});

test("a script that fails answers its exit code and its last lines", async () => {
  const result = await runScript("echo installing; echo 'npm ERR! missing' >&2; exit 3", { cwd: folder(), env: process.env, limitMs: 10_000 });
  assert.equal(result.code, 3);
  assert.match(result.output, /npm ERR! missing/);
});

test("a script that runs past its limit is stopped with everything it started, and answers 124", async () => {
  const dir = folder();
  const child = path.join(dir, "child.pid");
  const started = Date.now();
  // The script starts a program of its own and waits on it, as an install does.
  const result = await runScript(`echo starting; sleep 60 & echo $! > ${child}; wait`, { cwd: dir, env: process.env, limitMs: 400 });
  assert.equal(result.code, TIMED_OUT);
  assert.match(result.output, /starting/);
  assert.ok(Date.now() - started < 8000, "it did not wait for the script");
  await sleep(200);
  assert.equal(running(Number(readFileSync(child, "utf8").trim())), false, "what the script started was stopped with it");
});

test("a script that leaves a program running ends when the script does", async () => {
  const dir = folder();
  const child = path.join(dir, "child.pid");
  const started = Date.now();
  const result = await runScript(`sleep 30 & echo $! > ${child}; echo left it running`, { cwd: dir, env: process.env, limitMs: 20_000 });
  assert.equal(result.code, 0);
  assert.ok(Date.now() - started < 5000, "it did not wait for the program left running");
  process.kill(Number(readFileSync(child, "utf8").trim()), "SIGKILL");
});

/** A script as the terminal window runs it: its process id, its log and its exit code in files (agent.mjs, runSetupScript). */
function inTerminal(dir, script) {
  const files = { exitFile: path.join(dir, "setup.exit"), logFile: path.join(dir, "setup.log"), pidFile: path.join(dir, "setup.pid") };
  const command = `echo $$ > '${files.pidFile}'; bash -c '${script}' 2>&1 | tee '${files.logFile}'; echo \${PIPESTATUS[0]} > '${files.exitFile}'`;
  spawn("bash", ["-c", command], { detached: true, stdio: "ignore" }).unref();
  return files;
}

test("a script in a terminal window is followed to its end, with what it writes handed over on the way", async () => {
  const seen = [];
  const result = await followScript({
    ...inTerminal(folder(), "echo one; sleep 0.5; echo two; exit 2"), limitMs: 10_000, every: 100, poll: 50, onOutput: (output) => seen.push(output),
  });
  assert.equal(result.code, 2);
  assert.equal(result.output, "one\ntwo");
  assert.ok(seen.includes("one"), "what it wrote was handed over while it ran");
});

test("a script in a terminal window that runs past its limit is stopped, and never writes that it ended", async () => {
  const dir = folder();
  const files = inTerminal(dir, "echo waiting for the stack; sleep 60");
  const result = await followScript({ ...files, limitMs: 500, every: 100, poll: 50 });
  assert.equal(result.code, TIMED_OUT);
  assert.equal(result.output, "waiting for the stack");
  await sleep(300);
  assert.equal(running(Number(readFileSync(files.pidFile, "utf8").trim())), false);
  assert.equal(existsSync(files.exitFile), false);
});

test("stopping a script that is already gone does nothing", async () => {
  await stopGroup(NaN);
  await stopGroup(1);
  await stopGroup(2 ** 22 - 3, { grace: 100 });
});

test("a setup that did not end well is said plainly, with its last lines", () => {
  assert.equal(failureWords(0, 45), null);
  assert.equal(failureWords(2, 45), "The setup script stopped with an error (exit 2).");
  assert.equal(failureWords(TIMED_OUT, 45), "The setup script did not finish in 45 minutes and was stopped.");
  const message = failureMessage(1, 45, Array.from({ length: 30 }, (_, index) => `line ${index + 1}`).join("\n"));
  assert.match(message, /^The setup script stopped with an error \(exit 1\)\. Its last lines:/);
  assert.ok(message.includes("line 30") && message.includes("line 19") && !message.includes("line 18\n"));
  assert.match(failureMessage(TIMED_OUT, 60, ""), /did not finish in 60 minutes and was stopped\. It wrote nothing\./);
  assert.equal(failureMessage(0, 45, "fine"), null);
});

test("the agent is told when the setup failed or was stopped, and nothing when it ended well", () => {
  assert.equal(setupEnded(null), "");
  assert.equal(setupEnded({ code: 0, minutes: 45 }), "");
  assert.match(setupEnded({ code: 2, minutes: 45 }), /stopped with an error \(exit 2\).*setup\.log/s);
  assert.match(setupEnded({ code: 124, minutes: 45 }), /did not finish in 45 minutes and was stopped/);
});
