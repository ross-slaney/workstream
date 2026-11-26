import { test } from "node:test";
import assert from "node:assert/strict";
import { noSetup, projectTools, projectWork } from "./instructions.mjs";

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
  assert.match(told, /`phone android snapshot`/);
  assert.match(told, /`phone android fill @e3 "text"`/);
  assert.doesNotMatch(told, /adb shell input/);
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

test("the agent is told to work in the checkout the setup prepared, and against the branch it started from", () => {
  const told = projectWork({ url: "https://github.com/acme/app", branch: "release/2" });
  assert.match(told, /what the environment's setup installed and built is in that folder/);
  assert.match(told, /Do not make a git worktree or a second copy/);
  assert.match(told, /open the pull request against release\/2/);
  const noBranch = projectWork({ url: "https://github.com/acme/app" });
  assert.match(noBranch, /Do not make a git worktree/);
  assert.doesNotMatch(noBranch, /open the pull request against/);
  const noRepository = projectWork(null);
  assert.match(noRepository, /No repository is cloned/);
  assert.doesNotMatch(noRepository, /worktree/);
});

test("the agent is told to start what is slow first and to keep working while it runs", () => {
  assert.match(projectTools(), /Start what takes longest first, and do not sit waiting for it/);
  const told = projectTools(["chrome", "iphone", "android"]);
  assert.match(told, /`phone ios build <the app's folder> --no-wait` sends the source and returns at once/);
  assert.doesNotMatch(projectTools(), /phone ios build/);
});

test("the agent is told about Return, about what a reading leaves out, and that the surface in use comes to the front", () => {
  const told = projectTools(["chrome", "iphone", "android"]);
  assert.match(told, /`press enter` presses Return/);
  assert.match(told, /leaves out a system dialog and some overlays/);
  assert.match(told, /comes to the front of the desktop by itself/);
  assert.match(told, /its developer menu covers the app/);
});
