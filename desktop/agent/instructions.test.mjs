import { test } from "node:test";
import assert from "node:assert/strict";
import { projectTools } from "./instructions.mjs";

test("the agent is told to follow the repository's rules and skills, and that iPhone builds need a Mac", () => {
  const told = projectTools("AuthenticAMD");
  assert.match(told, /AGENTS\.md/);
  assert.match(told, /skills/);
  assert.match(told, /need a Mac/);
});

test("the Android emulator is offered only on an Intel processor", () => {
  assert.match(projectTools("GenuineIntel"), /emulator -avd Phone/);
  assert.doesNotMatch(projectTools("AuthenticAMD"), /emulator -avd Phone/);
  assert.match(projectTools("AuthenticAMD"), /does not run on this machine's processor/);
  assert.match(projectTools(null), /does not run/);
});
