import assert from "node:assert/strict";
import test from "node:test";
import { newTurn, note, owed, surfaceOf } from "./handover.mjs";

test("a tool call is known by the surface it uses", () => {
  assert.equal(surfaceOf("mcp__browser__browser_click", {}), "browser");
  assert.equal(surfaceOf("Bash", { command: "phone android tap \"Sign in\"" }), "android");
  assert.equal(surfaceOf("Bash", { command: "cd app && phone ios run --wait" }), "iphone");
  assert.equal(surfaceOf("Bash", { command: "surfaces show" }), "shown");
  assert.equal(surfaceOf("Bash", { command: "npm test && surfaces show" }), "shown");
  assert.equal(surfaceOf("Bash", { command: "grep phone ios.txt" }), null);
  assert.equal(surfaceOf("Bash", { command: "surfaces arrange" }), null);
  assert.equal(surfaceOf("Read", { file_path: "/tmp/phone ios" }), null);
});

test("a turn that used surfaces is asked once to show them", () => {
  const turn = newTurn();
  assert.equal(owed(turn), null);
  note(turn, "mcp__browser__browser_navigate", {});
  note(turn, "Bash", { command: "phone ios tap @e3" });
  note(turn, "Bash", { command: "phone ios tap @e4" });
  const asked = owed(turn);
  assert.match(asked, /you used the browser and the iPhone/);
  assert.match(asked, /surfaces show/);
  assert.equal(owed(turn), null);
});

test("nothing is owed once the surfaces were shown, until one is used again", () => {
  const turn = newTurn();
  note(turn, "Bash", { command: "phone android open app://x" });
  note(turn, "Bash", { command: "surfaces show" });
  assert.equal(owed(turn), null);
  note(turn, "Bash", { command: "phone android tap @e1" });
  assert.match(owed(turn), /you used the Android phone/);
});
