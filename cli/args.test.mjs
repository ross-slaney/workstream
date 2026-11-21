// node --test cli/*.test.mjs: what the command line understands, before it sends anything anywhere.

import assert from "node:assert/strict";
import { test } from "node:test";
import { UsageError, apiOrigin, choose, describe, parse, taskIdFrom, taskLink } from "./args.mjs";

test("no words, help, -h and --help all ask for the usage", () => {
  for (const argv of [[], ["help"], ["-h"], ["--help"]]) assert.equal(parse(argv).command, "help");
  assert.equal(parse(["--version"]).command, "version");
});

test("an unknown command or option is refused in words", () => {
  assert.throws(() => parse(["launch"]), (error) => error instanceof UsageError && /no command "launch"/.test(error.message));
  assert.throws(() => parse(["start", "--colour", "red", "Hi"]), UsageError);
  assert.throws(() => parse(["logout", "now"]), UsageError);
});

test("login takes the API's address, without a trailing slash, and https when none is said", () => {
  assert.deepEqual(parse(["login", "--api", "https://workstream-api.example.test/"]).options, { api: "https://workstream-api.example.test" });
  assert.equal(parse(["login", "--api", "workstream-api.example.test"]).options.api, "https://workstream-api.example.test");
  assert.equal(parse(["login", "--api", "http://localhost:5280"]).options.api, "http://localhost:5280");
  assert.equal(parse(["login", "--no-browser"]).options["no-browser"], true);
  assert.throws(() => parse(["login", "--api", "ftp://example.test"]), UsageError);
  assert.throws(() => parse(["login", "https://example.test"]), UsageError);
});

test("start takes a team, an environment and the prompt, and waits unless told not to", () => {
  const { command, options } = parse(["start", "--team", "Platform", "--env", "Default", "Fix", "issue #12"]);
  assert.equal(command, "start");
  assert.equal(options.team, "Platform");
  assert.equal(options.env, "Default");
  assert.equal(options.prompt, "Fix issue #12");
  assert.equal(options.wait, true);
  assert.equal(options.timeout, 900);

  const quick = parse(["start", "--no-wait", "--json", "--timeout", "60", "--title", "Issue 12", "Do it"]).options;
  assert.equal(quick.wait, false);
  assert.equal(quick.json, true);
  assert.equal(quick.timeout, 60);
  assert.equal(quick.title, "Issue 12");
  assert.equal(quick.team, undefined);
});

test("start reads the prompt from standard input for -", () => {
  assert.equal(parse(["start", "--team", "t", "-"]).options.prompt, null);
  assert.throws(() => parse(["start", "-", "and more"]), UsageError);
});

test("start needs a prompt, and a timeout that is a number of seconds", () => {
  assert.throws(() => parse(["start", "--team", "t"]), /Say what the task is/);
  assert.throws(() => parse(["start", "  "]), /empty/);
  assert.throws(() => parse(["start", "--timeout", "soon", "Hi"]), /number of seconds/);
  assert.throws(() => parse(["start", "--team"]), UsageError);
});

test("status takes one task, as its id or its link", () => {
  assert.deepEqual(parse(["status", "a1b2"]).positionals, ["a1b2"]);
  assert.deepEqual(parse(["status", "https://web.example.test/t/org_1?open=task-9", "--json"]), { command: "status", options: { json: true }, positionals: ["task-9"] });
  assert.throws(() => parse(["status"]), UsageError);
  assert.throws(() => parse(["status", "a", "b"]), UsageError);
});

test("tasks and envs take an optional team", () => {
  for (const command of ["tasks", "envs"]) {
    assert.deepEqual(parse([command]), { command, options: {}, positionals: [] });
    assert.equal(parse([command, "--team", "Platform", "--json"]).options.team, "Platform");
    assert.throws(() => parse([command, "Platform"]), UsageError);
  }
});

test("a task's id comes from the id or from the web app's link", () => {
  assert.equal(taskIdFrom(" abc "), "abc");
  assert.equal(taskIdFrom("https://web.example.test/t/org_1?open=x,y"), "x");
  assert.throws(() => taskIdFrom("https://web.example.test/t/org_1"), /does not open a task/);
});

test("a task's link is its team's workspace with it open", () => {
  assert.equal(taskLink("https://web.example.test/", { id: "t 1", teamId: "org_1" }), "https://web.example.test/t/org_1?open=t%201");
});

test("apiOrigin refuses what is not an address", () => {
  assert.throws(() => apiOrigin("http://"), UsageError);
});

test("choose finds by id or by name, takes the only one, and lists the rest when it cannot", () => {
  const teams = [{ id: "org_1", name: "Platform" }, { id: "org_2", name: "Growth" }];
  assert.equal(choose(teams, "org_2", "team").name, "Growth");
  assert.equal(choose(teams, "platform", "team").id, "org_1");
  assert.equal(choose([teams[0]], undefined, "team").id, "org_1");
  assert.throws(() => choose(teams, undefined, "team"), (error) => /--team/.test(error.message) && /Growth \(org_2\)/.test(error.message));
  assert.throws(() => choose(teams, "Sales", "team"), /No team is called "Sales"/);
  assert.throws(() => choose([...teams, { id: "org_3", name: "growth" }], "Growth", "team"), /More than one/);
  assert.throws(() => choose([], undefined, "environment"), /no environments/);
  assert.throws(() => choose(teams, undefined, "environment"), /--env/);
});

test("describe says where a desktop is in words", () => {
  assert.equal(describe({ state: "starting", step: "machine" }), "Making the desktop");
  assert.equal(describe({ state: "starting", step: "boot" }), "The desktop is starting up");
  assert.equal(describe({ state: "starting", step: "agent" }), "The desktop is up; the agent is starting");
  assert.equal(describe({ state: "ready", agentWorking: true }), "Ready; the agent is working");
  assert.equal(describe({ state: "failed", failure: "No capacity" }), "Failed: No capacity");
  assert.equal(describe({ state: "stopped" }), "Stopped");
});
