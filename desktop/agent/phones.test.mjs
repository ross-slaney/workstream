import { test } from "node:test";
import assert from "node:assert/strict";
import { IPHONE_SETUP, phoneStarts, setupScriptFor } from "./phones.mjs";

test("an environment without the iPhone runs its own setup script, or nothing", () => {
  assert.equal(setupScriptFor({ apps: ["chrome"], setupScript: "npm ci" }), "npm ci");
  assert.equal(setupScriptFor({ apps: ["chrome", "android"], setupScript: null }), null);
  assert.equal(setupScriptFor({ apps: ["chrome"], setupScript: "   " }), null);
});

test("an environment with the iPhone installs the simulator first, then runs its own script", () => {
  const both = setupScriptFor({ apps: ["chrome", "iphone"], setupScript: "set -euo pipefail\nnpm ci" });
  assert.ok(both.indexOf(IPHONE_SETUP) < both.indexOf("npm ci"));
  assert.match(both, /set -euo pipefail\nnpm ci$/);
  // With no script of its own there is still something to run, so the simulator is in the saved setup.
  const alone = setupScriptFor({ apps: ["iphone"], setupScript: null });
  assert.match(alone, /phone ios setup \|\| exit \$\?/);
});

test("the environment's phones start with the desktop, Android first", () => {
  assert.deepEqual(phoneStarts(["chrome", "vscode"]), []);
  assert.deepEqual(phoneStarts(["iphone"]), [["ios", "start"]]);
  assert.deepEqual(phoneStarts(["iphone", "android"]), [["android", "start"], ["ios", "start"]]);
});
