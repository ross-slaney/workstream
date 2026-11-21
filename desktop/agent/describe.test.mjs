import assert from "node:assert/strict";
import { test } from "node:test";
import { describe, spoken } from "./describe.mjs";

test("a command is one line, however long it was", () => {
  assert.equal(describe("Bash", { command: "npm test\n&& echo done" }), "Ran npm test");
  assert.ok(describe("Bash", { command: "x".repeat(500) }).length <= 144);
});

test("a file is named from the workspace", () => {
  assert.equal(describe("Edit", { file_path: "/home/work/work/app/src/a.ts" }, "/home/work/work/app"), "Edited src/a.ts");
  assert.equal(describe("Read", { file_path: "/etc/hosts" }, "/home/work/work/app"), "Read /etc/hosts");
});

test("the browser and the desktop say what they did", () => {
  assert.equal(describe("mcp__browser__browser_navigate", { url: "https://example.com" }), "Opened https://example.com");
  assert.equal(describe("mcp__desktop__click", { x: 10, y: 20 }), "Clicked at 10, 20");
  assert.equal(describe("mcp__desktop__screenshot", {}), "Looked at the screen");
});

test("the agent's own bookkeeping is not a line", () => {
  assert.equal(describe("TodoWrite", { todos: [] }), null);
  assert.equal(describe("mcp__browser__browser_wait_for", {}), null);
});

test("a tool nobody named still gets a line", () => {
  assert.equal(describe("mcp__github__create_issue", {}), "Used github create_issue");
});

test("a refusal from the model gateway is said in its own words", () => {
  assert.equal(spoken("API Error: 400 Your team has used its model allowance for this month. A system administrator can raise it."), "Your team has used its model allowance for this month. A system administrator can raise it.");
  assert.equal(spoken("  The heading is Example Domain.  "), "The heading is Example Domain.");
  assert.equal(spoken("I got an API Error: 400 from the site."), "I got an API Error: 400 from the site.");
});
