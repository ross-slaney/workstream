import { test } from "node:test";
import assert from "node:assert/strict";
import { noSetup, projectTools } from "./instructions.mjs";

test("the agent is told to follow the repository's rules and skills, and how a desktop without an iPhone gets one", () => {
  const told = projectTools();
  assert.match(told, /AGENTS\.md/);
  assert.match(told, /skills/);
  assert.match(told, /This desktop has no iPhone/);
  assert.match(told, /`iphone` in workstream\.yml, on a Large desktop/);
  assert.doesNotMatch(told, /phone ios run/);
});

test("the agent is told how to start the Android phone, on every size, and how to use it", () => {
  const told = projectTools();
  assert.match(told, /`phone android start`/);
  assert.match(told, /adb shell input tap/);
  assert.doesNotMatch(told, /processor does not|Intel/);
});

test("on a desktop with the iPhone the agent is told how to get the app built, how to use the phone, and to check every surface", () => {
  const told = projectTools(["chrome", "iphone", "android"]);
  assert.match(told, /`phone ios run <the app's folder>`/);
  assert.match(told, /built on a Mac, which Workstream does for you/);
  assert.match(told, /`phone ios snapshot`/);
  assert.match(told, /`phone ios tap @e3`/);
  assert.match(told, /never with `CI=1`/);
  assert.match(told, /`127\.0\.0\.1:8081`/);
  assert.match(told, /check it on every surface this desktop has/);
  assert.doesNotMatch(told, /This desktop has no iPhone/);
});

test("the agent is told that host.docker.internal is the desktop, from a container", () => {
  assert.match(projectTools(), /`host\.docker\.internal` is this desktop/);
});

test("the agent is told to install the project first when the environment has a repository and no setup script", () => {
  const repository = { url: "https://github.com/acme/app", branch: "main" };
  assert.match(noSetup({ repository, setupScript: null }), /No setup script ran on this desktop.*Install what the task needs/s);
  assert.match(noSetup({ repository, setupScript: "  " }), /No setup script ran/);
  assert.equal(noSetup({ repository, setupScript: "npm ci" }), "");
  assert.equal(noSetup({ repository: null, setupScript: null }), "");
});
