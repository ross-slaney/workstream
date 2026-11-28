import assert from "node:assert/strict";
import { test } from "node:test";
import { ended, longest, newRunning, runningText, started } from "./running.mjs";

test("only what can take long is named", () => {
  assert.equal(runningText("Bash", { command: "npm run build" }), "npm run build");
  assert.equal(runningText("Bash", { command: "\n  cd app\nnpm test" }), "cd app");
  assert.equal(runningText("Bash", { command: "npm run dev", run_in_background: true }), null);
  assert.equal(runningText("Bash", { command: "  " }), null);
  assert.equal(runningText("Agent", { description: "Find the failing test" }), "Find the failing test");
  assert.equal(runningText("Skill", { skill: "release" }), "release");
  assert.equal(runningText("Read", { file_path: "/w/a.ts" }), null);
  assert.equal(runningText("mcp__browser__browser_click", { element: "Save" }), null);
  assert.equal(runningText("Bash", { command: "x".repeat(400) }).length, 160);
});

test("what is said is the call that has run longest, with its seconds", () => {
  const running = newRunning();
  assert.equal(longest(running, 0), null);
  started(running, "a", "Bash", { command: "gradle assemble" }, 1_000);
  started(running, "b", "Bash", { command: "npm ci" }, 5_000);
  started(running, "c", "Read", { file_path: "/w/a.ts" }, 6_000);
  assert.deepEqual(longest(running, 61_400), { text: "gradle assemble", seconds: 60 });
  ended(running, "a");
  assert.deepEqual(longest(running, 61_400), { text: "npm ci", seconds: 56 });
  ended(running, "b");
  ended(running, "c");
  assert.equal(longest(running, 61_400), null);
});

test("a call noted twice keeps its first start, and one with no id is not kept", () => {
  const running = newRunning();
  started(running, "a", "Bash", { command: "make" }, 1_000);
  started(running, "a", "Bash", { command: "make" }, 9_000);
  started(running, undefined, "Bash", { command: "ls" }, 2_000);
  assert.deepEqual(longest(running, 11_000), { text: "make", seconds: 10 });
  assert.equal(running.size, 1);
});
