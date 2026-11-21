import assert from "node:assert/strict";
import { test } from "node:test";
import { ago, startedFromLine, tail, usedBytes } from "./prepared.mjs";

const now = new Date("2026-11-21T12:00:00Z");

test("how long ago is said in the one unit that says it", () => {
  assert.equal(ago("2026-11-21T11:59:40Z", now), "just now");
  assert.equal(ago("2026-11-21T11:59:00Z", now), "1 minute ago");
  assert.equal(ago("2026-11-21T09:00:00Z", now), "3 hours ago");
  assert.equal(ago("2026-11-19T10:00:00Z", now), "2 days ago");
  assert.equal(ago("2026-11-20T11:00:00Z", now), "1 day ago");
});

test("a task from a save says when it was prepared and how far the branch has moved", () => {
  const prepared = { preparedAt: "2026-11-19T10:00:00Z", commit: "a1b2c3d4e5f6", branch: "main" };
  assert.equal(startedFromLine({ ...prepared, newer: 14 }, now), "Started from the setup prepared 2 days ago (main at a1b2c3d), 14 commits newer now.");
  assert.equal(startedFromLine({ ...prepared, newer: 1 }, now), "Started from the setup prepared 2 days ago (main at a1b2c3d), 1 commit newer now.");
  assert.equal(startedFromLine({ ...prepared, newer: 0 }, now), "Started from the setup prepared 2 days ago (main at a1b2c3d), nothing newer since.");
  // The pull failed, so how far it moved is not known.
  assert.equal(startedFromLine({ ...prepared, newer: null }, now), "Started from the setup prepared 2 days ago (main at a1b2c3d).");
});

test("a save of a setup script with no repository says only when", () => {
  assert.equal(startedFromLine({ preparedAt: "2026-11-21T09:00:00Z", commit: null, branch: null, newer: null }, now), "Started from the setup prepared 3 hours ago.");
  assert.equal(startedFromLine({ preparedAt: "2026-11-21T09:00:00Z", commit: "abcdef0123", branch: null, newer: 2 }, now), "Started from the setup prepared 3 hours ago (abcdef0), 2 commits newer now.");
});

test("what the setup wrote is kept to its end", () => {
  assert.equal(tail("short"), "short");
  assert.equal(tail("x".repeat(10) + "end", 5), "xxend");
  assert.equal(tail("abcdefgh", 3), "fgh");
});

test("the disk's used bytes are read from df", () => {
  assert.equal(usedBytes("       Used\n 7516192768\n"), 7516192768);
  assert.equal(usedBytes(""), 0);
  assert.equal(usedBytes("Used\nnot a number"), 0);
});
