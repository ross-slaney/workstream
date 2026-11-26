import { test } from "node:test";
import assert from "node:assert/strict";
import { adbText, held, insertedAt, linesOf, parseUi, sameField, settle, targetOf, typedInto } from "./screen.mjs";

/** A screen that shows each of `readings` in turn, then the last one for ever, on a clock that moves when waited on. */
function screenOf(readings) {
  let at = 0;
  let clock = 0;
  return {
    read: async () => readings[Math.min(at++, readings.length - 1)],
    now: () => clock,
    wait: async (ms) => void (clock += ms),
    reads: () => at,
  };
}

test("a screen has settled when it changed and then read the same twice", async () => {
  const phone = screenOf(["home", "opening", "opening…", "step", "step"]);
  const result = await settle(phone.read, { before: "home", now: phone.now, wait: phone.wait });
  assert.deepEqual(result, { screen: "step", changed: true, settled: true });
  assert.equal(phone.reads(), 5);
});

test("a screen that never differs from before is said not to have changed, after a short patience", async () => {
  const phone = screenOf(["home"]);
  const result = await settle(phone.read, { before: "home", now: phone.now, wait: phone.wait });
  assert.deepEqual(result, { screen: "home", changed: false, settled: true });
  assert.ok(phone.now() >= 2500 && phone.now() < 4000);
});

test("a screen that keeps changing is given back as it is when the time is up", async () => {
  let at = 0;
  let clock = 0;
  const result = await settle(async () => `frame ${at++}`, { before: "home", now: () => clock, wait: async (ms) => void (clock += ms) });
  assert.equal(result.settled, false);
  assert.equal(result.changed, true);
  assert.ok(clock >= 10_000);
});

test("a screen with nothing on it yet is waited on, because it is loading", async () => {
  const phone = screenOf(["home", "app", "app", "app", "app\n@e1 button \"Mark done\"", "app\n@e1 button \"Mark done\""]);
  const result = await settle(phone.read, { before: "home", empty: (screen) => !screen.includes("\n"), now: phone.now, wait: phone.wait });
  assert.equal(result.screen, "app\n@e1 button \"Mark done\"");
});

test("one reading is enough for a phone that only reads a screen at rest", async () => {
  const phone = screenOf(["home", "step"]);
  const result = await settle(phone.read, { before: "home", quiet: 1, now: phone.now, wait: phone.wait });
  assert.deepEqual(result, { screen: "step", changed: true, settled: true });
  assert.equal(phone.reads(), 2);
});

test("typed text is what a field holds when it is what it held with the text put in", () => {
  assert.ok(insertedAt("", "127.0.0.1:8081", "127.0.0.1:8081"));
  assert.ok(insertedAt("http://", "http://127.0.0.1", "127.0.0.1"));
  assert.ok(insertedAt("ac", "abc", "b"));
  // Digits that arrived with Shift held, as they did on the iPhone.
  assert.ok(!insertedAt("127.0.0.1:", "127.0.0.1:*)*!", "8081"));
  assert.ok(!insertedAt("", "127.", "127.0.0.1:8081"));
});

test("the field that took the text is found by its label, wherever the keyboard moved it", () => {
  const before = [{ ref: "e9", label: "Work email", value: "Work email", y: 400 }, { ref: "e10", label: "Password", value: "Password", y: 470 }];
  const after = [{ ref: "e14", label: "Work email", value: "rowan@example.test", y: 300 }, { ref: "e15", label: "Password", value: "Password", y: 370 }];
  assert.deepEqual(typedInto(before, after, "rowan@example.test"), { ok: true, field: after[0], value: "rowan@example.test" });
  // A field that shows only its own placeholder holds nothing.
  assert.equal(held(before[0]), "");
  assert.equal(sameField(before[1], after).ref, "e15");
});

test("a field that holds something else is said, with what it holds", () => {
  const before = [{ ref: "e9", label: "http://", value: "http://", y: 300 }];
  const after = [{ ref: "e14", label: "http://", value: "127.0.0.1:*)*!", y: 300 }];
  assert.deepEqual(typedInto(before, after, "127.0.0.1:8081"), { ok: false, field: after[0], value: "127.0.0.1:*)*!" });
  assert.deepEqual(typedInto(before, before, "x"), { ok: false, field: null, value: null });
});

test("an empty field that shows words of its own took the text when it holds exactly the text", () => {
  // The iPhone reads an empty field's grey words as what it holds.
  const before = [{ ref: "e7", label: "Work email", value: "you@company.com", y: 312 }];
  const after = [{ ref: "e7", label: "Work email", value: "rowan@example.test", y: 312 }];
  assert.equal(typedInto(before, after, "rowan@example.test").ok, true);
  assert.equal(typedInto(before, after, "rowan@example.test", { exact: true }).ok, true);
});

test("after a field was emptied, only the text itself will do", () => {
  const before = [{ ref: "e7", label: "Work email", value: "old", y: 312 }];
  const after = [{ ref: "e7", label: "Work email", value: "oldnew", y: 312 }];
  assert.equal(typedInto(before, after, "new").ok, true);
  assert.deepEqual(typedInto(before, after, "new", { exact: true }), { ok: false, field: after[0], value: "oldnew" });
});

test("a field that hides what it holds is checked by how much longer it got", () => {
  const before = [{ ref: "e4", label: "Password", value: "Password", y: 300 }];
  const after = [{ ref: "e4", label: "Password", value: "••••••", y: 300 }];
  assert.equal(typedInto(before, after, "s3cret").ok, true);
  assert.equal(typedInto(before, after, "longer-secret").ok, false);
});

const ANDROID = `<?xml version='1.0' encoding='UTF-8' standalone='yes' ?><hierarchy rotation="0">
<node index="0" text="" resource-id="" class="android.widget.FrameLayout" package="com.example.app" content-desc="" clickable="false" enabled="true" focused="false" password="false" selected="false" bounds="[0,0][1080,2400]">
 <node index="0" text="Messages &amp; handover notes read" resource-id="" class="android.widget.TextView" package="com.example.app" content-desc="" clickable="false" enabled="true" focused="false" password="false" selected="false" bounds="[40,300][900,380]" />
 <node index="1" text="" resource-id="com.example.app:id/email" class="android.widget.EditText" package="com.example.app" content-desc="" hint="Work email" clickable="true" enabled="true" focused="true" password="false" selected="false" bounds="[40,500][1040,620]" />
 <node index="2" text="s3cret" resource-id="" class="android.widget.EditText" package="com.example.app" content-desc="" hint="Password" clickable="true" enabled="true" focused="false" password="true" selected="false" bounds="[40,660][1040,780]" />
 <node index="3" text="" resource-id="" class="android.view.ViewGroup" package="com.example.app" content-desc="Block or skip" clickable="true" enabled="true" focused="false" password="false" selected="false" bounds="[40,2180][400,2300]">
  <node index="0" text="Block or skip" resource-id="" class="android.widget.TextView" package="com.example.app" content-desc="" clickable="false" enabled="true" focused="false" password="false" selected="false" bounds="[90,2210][350,2270]" />
 </node>
 <node index="4" text="Mark done" resource-id="" class="android.widget.Button" package="com.example.app" content-desc="" clickable="true" enabled="false" focused="false" password="false" selected="false" bounds="[420,2180][1040,2300]" />
 <node index="5" text="" resource-id="com.example.app:id/close" class="android.widget.ImageButton" package="com.example.app" content-desc="" clickable="true" enabled="true" focused="false" password="false" selected="false" bounds="[960,100][1040,180]" />
 <node index="6" text="Hidden" resource-id="" class="android.widget.TextView" package="com.example.app" content-desc="" clickable="false" enabled="true" focused="false" password="false" selected="false" bounds="[0,0][0,0]" />
</node></hierarchy>UI hierchary dumped to: /dev/tty`;

test("an Android screen is read into the lines an iPhone's is: words, fields and things to tap, each once", () => {
  const screen = parseUi(ANDROID);
  assert.equal(linesOf(screen), [
    "app com.example.app",
    '@e1 text "Messages & handover notes read" (470,340)',
    '@e2 textfield "Work email" value="" focused (540,560)',
    '@e3 textfield "Password" value="••••••" (540,720)',
    '@e4 button "Block or skip" (220,2240)',
    '@e5 button "Mark done" disabled (730,2240)',
    '@e6 button "close" (1000,140)',
  ].join("\n"));
});

test("a command points at a reference, at words or at a point", () => {
  const { elements } = parseUi(ANDROID);
  assert.equal(targetOf(["@e4"], elements).label, "Block or skip");
  assert.equal(targetOf(["e5"], elements).label, "Mark done");
  assert.equal(targetOf(["mark", "done"], elements).ref, "e5");
  assert.equal(targetOf(["handover"], elements).ref, "e1");
  assert.deepEqual(targetOf(["540", "1200"], elements), { x: 540, y: 1200 });
  assert.equal(targetOf(["Nothing like it"], elements), null);
  assert.equal(targetOf(["@e99"], elements), null);
});

test("text for Android's input command is one word for the phone's shell", () => {
  assert.equal(adbText("rowan@example.test"), "'rowan@example.test'");
  assert.equal(adbText("two words"), "'two%swords'");
  assert.equal(adbText("it's $HOME; 50%"), `'it'\\''s%s$HOME;%s50%'`);
});
