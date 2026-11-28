import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import {
  MAX_COMMANDS, backgroundCommand, endsInBackground, inForeground, localPort, machineStarted, noteBackground, noteFile, noteTabs,
  pagesOf, restartScript, restorePlan, restoredLine,
} from "./restore.mjs";

test("a command left running is one sent to the background", () => {
  assert.ok(endsInBackground("npm run dev &"));
  assert.ok(endsInBackground("nohup npm run dev > /tmp/dev.log 2>&1 &"));
  assert.ok(endsInBackground("cd app && npm start & disown"));
  assert.ok(endsInBackground("npm ci\nnpm run dev &\n"));
  assert.ok(!endsInBackground("npm test && echo done"));
  assert.ok(!endsInBackground("npm test 2>&1"));
  assert.ok(!endsInBackground("ls"));
  assert.deepEqual(backgroundCommand({ command: "npm run dev", run_in_background: true }, "/home/work/work/app"), { command: "npm run dev", cwd: "/home/work/work/app" });
  assert.deepEqual(backgroundCommand({ command: "python -m http.server 8000 &" }), { command: "python -m http.server 8000 &", cwd: null });
  assert.equal(backgroundCommand({ command: "npm test" }, "/x"), null);
  assert.equal(backgroundCommand({ command: "  ", run_in_background: true }, "/x"), null);
});

test("the harness keeps the newest background commands, each once", () => {
  const state = {};
  const bash = (command, cwd = "/w", extra = {}) => ({ tool_name: "Bash", tool_input: { command, ...extra }, cwd });
  assert.equal(noteBackground(state, bash("npm test")), false);
  assert.equal(noteBackground(state, { tool_name: "Edit", tool_input: { command: "x &" } }), false);
  assert.ok(noteBackground(state, bash("npm run dev", "/w", { run_in_background: true })));
  assert.ok(noteBackground(state, bash("node api.js &")));
  // Run again, it moves to the end and is not kept twice.
  assert.ok(noteBackground(state, bash("npm run dev", "/w", { run_in_background: true })));
  assert.deepEqual(state.background.map((x) => x.command), ["node api.js &", "npm run dev"]);
  for (let n = 0; n < 10; n++) noteBackground(state, bash(`worker ${n} &`));
  assert.equal(state.background.length, MAX_COMMANDS);
  assert.equal(state.background.at(-1).command, "worker 9 &");
});

test("the browser's pages are kept, not its own or the agent's window", () => {
  const targets = [
    { type: "page", url: "http://localhost:3000/" },
    { type: "page", url: "about:blank" },
    { type: "page", url: "chrome://newtab/" },
    { type: "page", url: "http://127.0.0.1:4141/" },
    { type: "service_worker", url: "https://example.com/sw.js" },
    { type: "page", url: "https://example.com/docs" },
    { type: "page", url: "https://example.com/docs" },
  ];
  assert.deepEqual(pagesOf(targets), ["http://localhost:3000/", "https://example.com/docs"]);
  assert.deepEqual(pagesOf(null), []);
  const state = {};
  assert.ok(noteTabs(state, targets));
  assert.equal(noteTabs(state, targets), false);
  assert.ok(noteFile(state, "/w/src/a.ts"));
  assert.ok(noteFile(state, "/w/src/b.ts"));
  noteFile(state, "/w/src/a.ts");
  assert.deepEqual(state.files, ["/w/src/b.ts", "/w/src/a.ts"]);
});

test("a page on the desktop's own server waits for it", () => {
  assert.equal(localPort("http://localhost:3000/"), 3000);
  assert.equal(localPort("http://127.0.0.1/"), 80);
  assert.equal(localPort("https://[::1]/"), 443);
  assert.equal(localPort("https://example.com:3000/"), null);
  assert.equal(localPort("not a url"), null);
});

test("a desktop with nothing kept brings nothing back and says nothing", () => {
  assert.equal(restorePlan({}), null);
  assert.equal(restoredLine(null, "/w"), null);
});

test("the agent is told in one line what came back", () => {
  const plan = restorePlan({ background: [{ command: "npm run dev", cwd: "/home/work/work/app" }], tabs: ["http://localhost:3000/"], files: ["/w/a.ts"] });
  const line = restoredLine(plan, "/home/work/work/app");
  assert.equal(line.split("\n").length, 1);
  assert.match(line, /stopped and started again/);
  assert.match(line, /reran 1 command .*`npm run dev` in \/home\/work\/work\/app/);
  assert.match(line, /reopened 1 page in the browser \(http:\/\/localhost:3000\/\)/);
  assert.match(line, /opened VS Code on \/home\/work\/work\/app with 1 file/);
  assert.match(line, /Any other program you started is gone/);
});

test("an agent stopped in the middle of a conversation is told, whatever came back", () => {
  assert.equal(restoredLine(null, "/w", false), null);
  const nothing = restoredLine(null, "/w", true);
  assert.equal(nothing.split("\n").length, 1);
  assert.match(nothing, /stopped and started again, which ends every program and empties \/tmp/);
  assert.match(nothing, /nothing you started is running\. Start again what the work needs/);
  const pagesOnly = restoredLine(restorePlan({ tabs: ["http://localhost:3000/"] }), "/w", true);
  assert.match(pagesOnly, /reopened 1 page/);
  assert.match(pagesOnly, /kept no command of yours to run again, so every program you started is gone/);
});

test("the restored terminal runs each command in its folder, in the background, and stays open", () => {
  assert.equal(inForeground("npm run dev &"), "npm run dev");
  assert.equal(inForeground("npm start & disown"), "npm start");
  assert.equal(inForeground("npm run dev"), "npm run dev");
  const script = restartScript([{ command: "npm run dev", cwd: "/home/work/it's" }, { command: "node api.js > api.log 2>&1 &", cwd: null }], "/home/work/.workstream/env.sh");
  assert.match(script, /cd '\/home\/work\/it'\\''s' && npm run dev/);
  assert.match(script, /\( node api\.js > api\.log 2>&1\n\) &/);
  assert.match(script, /\nwait\n/);
  assert.match(script, /exec bash\n$/);
  // It is a script bash reads, and it runs what it was given.
  const folder = mkdtempSync(path.join(os.tmpdir(), "restore-"));
  try {
    const file = path.join(folder, "restore.sh");
    const ran = restartScript([{ command: `echo started > ${path.join(folder, "out")}`, cwd: folder }], path.join(folder, "none.sh")).replace("exec bash\n", "");
    writeFileSync(file, ran);
    execFileSync("bash", ["-n", file]);
    execFileSync("bash", [file]);
    assert.ok(existsSync(path.join(folder, "out")));
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

test("only the first start of the machine finds no mark", () => {
  const folder = mkdtempSync(path.join(os.tmpdir(), "mark-"));
  try {
    const mark = path.join(folder, "workstream-agent");
    assert.equal(machineStarted(mark), true);
    // The agent starting again on the same running machine.
    assert.equal(machineStarted(mark), false);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

test("the iPhone's window is not kept as a page to reopen: it comes back with the phone", () => {
  const state = {};
  noteTabs(state, [
    { type: "page", url: "http://127.0.0.1:7979/0123abcd/" },
    { type: "page", url: "http://127.0.0.1:4141/" },
    { type: "page", url: "http://localhost:8081/" },
  ]);
  assert.deepEqual(state.tabs, ["http://localhost:8081/"]);
});
