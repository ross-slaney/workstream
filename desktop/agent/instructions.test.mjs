import { test } from "node:test";
import assert from "node:assert/strict";
import { projectTools } from "./instructions.mjs";

test("the agent is told to follow the repository's rules and skills, and that iPhone builds need a Mac", () => {
  const told = projectTools();
  assert.match(told, /AGENTS\.md/);
  assert.match(told, /skills/);
  assert.match(told, /need a Mac/);
});

test("the agent is told how to start the Android emulator, on every size", () => {
  assert.match(projectTools(), /`emulator -avd Phone -gpu swiftshader_indirect -no-audio -no-snapshot`/);
});
