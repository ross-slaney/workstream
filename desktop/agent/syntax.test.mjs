import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readdirSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));

// The harness and the agent's main file are run only on a desktop, so no other test loads them. A file that
// does not parse would stop every desktop's agent from starting.
test("every file of the agent parses", () => {
  for (const file of readdirSync(here).filter((name) => name.endsWith(".mjs"))) {
    const checked = spawnSync(process.execPath, ["--check", path.join(here, file)], { encoding: "utf8" });
    assert.equal(checked.status, 0, `${file}: ${checked.stderr}`);
  }
});
