import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { acted, doubt, printing, fill, iphoneElements, lookFor, misplaced, MOST_PRESSES, pngSize, press, readAndroid, remember, remembered, sameReading, snapshot, tap, thin, typeText, waitFor } from "./touch.mjs";

/** A pretend phone with one field, which records what was sent to it. */
function pretend({ fields = [{ ref: "e2", role: "textfield", label: "New step", value: "New step", x: 10, y: 10, frame: [0, 0, 20, 20] }], needsField = false } = {}) {
  const sent = [];
  let value = fields[0]?.value;
  return {
    sent,
    device: {
      name: "pretend phone", command: "phone pretend", quiet: 1, every: 1, needsField, patience: 40,
      look: () => ({
        screen: ["app pretend", ...fields.map((field) => `@${field.ref} textfield "${field.label}" value="${value}"`), '@e8 text "Steps"', '@e9 button "Add"'].join("\n"),
        fields: fields.map((field) => ({ ...field, value })),
      }),
      type: (text) => {
        sent.push(["type", text]);
        value = text;
        return 0;
      },
      enter: () => {
        sent.push(["enter"]);
        value = "New step";
        return 0;
      },
    },
  };
}

/** What a function printed. */
async function printed(run) {
  const write = printing.write;
  let text = "";
  printing.write = (chunk) => ((text += chunk), true);
  try {
    return { status: await run(), text };
  } finally {
    printing.write = write;
  }
}

test("text that ends in a new line is typed, and Return is pressed after it as a key of its own", async () => {
  const phone = pretend();
  const { status, text } = await printed(() => typeText(phone.device, "Wipe down the counters\n"));
  assert.equal(status, 0);
  assert.deepEqual(phone.sent, [["type", "Wipe down the counters"], ["enter"]]);
  assert.match(text, /The field "New step" held "Wipe down the counters", and Return was pressed\./);
});

test("Return alone is one key press", async () => {
  const phone = pretend();
  await printed(() => typeText(phone.device, "\n"));
  assert.deepEqual(phone.sent, [["enter"]]);
});

test("on a phone where stray keys are shortcuts, nothing is typed when no field is on the screen", async () => {
  const phone = pretend({ fields: [], needsField: true });
  const { status, text } = await printed(() => typeText(phone.device, "fridge"));
  assert.equal(status, 1);
  assert.deepEqual(phone.sent, []);
  assert.match(text, /nothing was typed/);
  assert.match(text, /phone pretend type --keys/);
});

test("a reading with the app's name and one thing in it is called thin", () => {
  assert.equal(thin("app com.example\n@e1 element \"screen\" (540,1200)"), true);
  assert.equal(thin("app com.example"), true);
  assert.equal(thin('app com.example\n@e1 button "A" (1,1)\n@e2 button "B" (2,2)'), false);
});

test("fill types into a field the reading leaves out for a moment after the tap", async () => {
  // As on the iPhone while the keyboard comes up: the two readings after a tap list nothing.
  const field = { ref: "e12", role: "textfield", label: "Full name", x: 100, y: 50, frame: [20, 40, 300, 30] };
  let value = "";
  let blank = 0;
  const sent = [];
  const device = {
    name: "pretend phone", command: "phone pretend", quiet: 1, every: 1, needsField: true, patience: 2000,
    look: () => {
      if (blank > 0) {
        blank--;
        return { screen: "app pretend (not answering yet)", fields: [] };
      }
      return { screen: `app pretend\n@e12 textfield "Full name" value="${value}"\n@e13 button "Next"`, fields: [{ ...field, value }] };
    },
    tapAt: () => {
      blank = 2;
      return 0;
    },
    erase: () => 0,
    type: (text) => {
      sent.push(text);
      value = text;
      return 0;
    },
    enter: () => 0,
  };
  const { status, text } = await printed(() => fill(device, ["@e12"], "Ivo Phone"));
  assert.equal(status, 0, text);
  assert.deepEqual(sent, ["Ivo Phone"]);
  assert.match(text, /"Full name" holds "Ivo Phone"/);
});

/** A pretend phone whose screen changes when a key is pressed, and which records the presses. */
function keyed() {
  const sent = [];
  return {
    sent,
    device: {
      name: "pretend phone", command: "phone pretend", quiet: 1, every: 1,
      keys: { delete: "<", enter: "!" },
      look: () => ({ screen: `app pretend\n@e1 textfield "Name" value="${"x".repeat(Math.max(0, 50 - sent.length * 45))}"\n@e2 button "Next"`, fields: [] }),
      press: (key, times) => {
        sent.push([key, times]);
        return 0;
      },
    },
  };
}

test("a key pressed many times is sent to the phone in one go, and the screen is printed once after it", async () => {
  const phone = keyed();
  const { status, text } = await printed(() => press(phone.device, "Delete", { times: 45 }));
  assert.equal(status, 0);
  assert.deepEqual(phone.sent, [["<", 45]]);
  assert.equal(text.match(/screen now:/g).length, 1);
  assert.match(text, /value="xxxxx"/);
});

test("a key the phone does not have is left to the caller, and nothing is pressed", async () => {
  const phone = keyed();
  assert.equal(await press(phone.device, "home"), null);
  assert.deepEqual(phone.sent, []);
});

test("--times outside what one command presses is refused before anything is pressed", async () => {
  const phone = keyed();
  for (const times of [0, 2.5, MOST_PRESSES + 1, Number.NaN]) {
    const { status, text } = await printed(() => press(phone.device, "delete", { times }));
    assert.equal(status, 2);
    assert.match(text, /--times is a whole number/);
  }
  assert.deepEqual(phone.sent, []);
});

// ── References, and readings that cannot be relied on ───────────────────────────────────────────────────

const el = (ref, role, label, x, y, extra = {}) => ({ ref, role, label, x, y, frame: [x - 10, y - 10, 20, 20], ...extra });
const lines = (elements) => ["app pretend", ...elements.map((element) => `@${element.ref} ${element.role} "${element.label}" (${element.x},${element.y})`)].join("\n");
const reading = (elements, extra = {}) => ({ screen: lines(elements), elements, fields: elements.filter((element) => element.role === "textfield"), ...extra });

/** A PNG's first bytes, of a size, with something after them to tell two apart. */
function png(width, height, body = 0) {
  const bytes = Buffer.alloc(32, body);
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]).copy(bytes);
  bytes.writeUInt32BE(width, 16);
  bytes.writeUInt32BE(height, 20);
  return bytes;
}

/** A pretend phone that shows the screens it is given, one after another, and records taps and pictures. */
function showing(screens, { pictures = [png(804, 1748)], pixelsEach = 2 } = {}) {
  const taps = [];
  const taken = [];
  let looks = 0;
  const device = {
    name: "pretend phone", command: "phone pretend", quiet: 1, every: 1, patience: 30,
    look: () => screens[Math.min(looks++, screens.length - 1)],
    tapAt: (x, y) => (taps.push([x, y]), 0),
    tap: (words) => (taps.push(words), 0),
    picture: (file) => (taken.push(file), pictures[Math.min(taken.length - 1, pictures.length - 1)]),
    pixelsEach: () => pixelsEach,
  };
  return { device, taps, taken, looks: () => looks };
}

test("a reference is tapped where the reading that was printed had it, not where the screen has it by now", async () => {
  const first = reading([el("e1", "button", "Welcome", 100, 100), el("e2", "button", "I already have an account", 200, 600)]);
  // By the time the tap is asked for, a button has appeared and everything has moved down one.
  const later = reading([el("e1", "button", "New", 50, 50), el("e2", "button", "Welcome", 100, 100), el("e3", "button", "I already have an account", 200, 600)]);
  const done = reading([el("e1", "textfield", "Work email", 200, 300)]);
  const phone = showing([first, later, done]);
  await printed(() => snapshot(phone.device));
  const { status, text } = await printed(() => acted(phone.device, () => tap(phone.device, ["@e2"])));
  assert.equal(status, 0);
  assert.deepEqual(phone.taps, [[200, 600]]);
  assert.match(text, /tapped @e2 button "I already have an account"/);
});

test("a reference the printed reading does not have is said, and nothing is tapped", async () => {
  const phone = showing([reading([el("e1", "button", "A", 10, 100), el("e2", "button", "B", 10, 200)])]);
  await printed(() => snapshot(phone.device));
  const { status, text } = await printed(async () => tap(phone.device, ["@e15"]));
  assert.equal(status, 1);
  assert.match(text, /has no @e15/);
  assert.deepEqual(phone.taps, []);
});

test("the reading printed after an action is the one the next reference means", async () => {
  const phone = showing([reading([el("e1", "button", "Next", 10, 100), el("e2", "text", "One", 10, 200)]), reading([el("e1", "button", "Back", 10, 100), el("e2", "button", "Finish", 300, 800)])]);
  await printed(() => acted(phone.device, () => 0));
  assert.equal(remembered(phone.device).elements[1].label, "Finish");
});

test("a reading with most of what it lists at the very top of the screen is misplaced", () => {
  // As one app's reading was on the iPhone: each thing's place inside what holds it, not on the screen.
  const wrong = [el("e1", "text", "Genius", 26, 11), el("e2", "heading", "Checklists", 184, 44), el("e3", "text", "Make one", 184, 21), el("e4", "link", "Terms", 164, 7)].map((element) => ({ ...element, frame: [element.x - 20, 0, 40, element.y * 2] }));
  assert.equal(misplaced(wrong), true);
  assert.equal(misplaced([el("e1", "text", "Title", 200, 60), el("e2", "button", "A", 200, 300), el("e3", "button", "B", 200, 500)]), false);
  // One thing at the top, such as a bar across it, is where it says.
  assert.equal(misplaced([{ ...el("e1", "text", "Bar", 200, 20), frame: [0, 0, 400, 40] }, el("e2", "button", "A", 200, 300), el("e3", "button", "B", 200, 500)]), false);
  assert.equal(misplaced([]), false);
});

test("why a reading cannot be relied on: unread, misplaced or thin, and nothing otherwise", () => {
  assert.match(doubt({ screen: "app x (not answering yet)", elements: [], unread: "the app in front is not answering it" }), /not answering/);
  assert.match(doubt({ screen: "app x", elements: [] }), /lists almost nothing/);
  assert.equal(doubt(reading([el("e1", "button", "A", 10, 100), el("e2", "button", "B", 10, 200)])), null);
});

test("a reference in a misplaced reading is not tapped, and neither are words", async () => {
  const wrong = [el("e1", "text", "Genius", 26, 11), el("e2", "textfield", "Work email", 184, 20), el("e3", "textfield", "Password", 184, 20)].map((element) => ({ ...element, frame: [0, 0, 300, 40] }));
  const phone = showing([reading(wrong)]);
  const shown = await printed(() => snapshot(phone.device));
  assert.match(shown.text, /cannot be relied on: it puts most of what it lists at the very top/);
  assert.match(shown.text, /-screen\.png \(804 by 1748 pixels\): read it/);
  assert.match(shown.text, /points of 2 pixels each: the screen is 402 by 874 points/);
  for (const words of [["@e3"], ["Password"]]) {
    const { status, text } = await printed(async () => tap(phone.device, words));
    assert.equal(status, 1);
    assert.match(text, /nothing was tapped/);
  }
  assert.deepEqual(phone.taps, []);
  // A point is the way through, and is tapped as it is.
  assert.equal(tap(phone.device, ["120", "300"]), 0);
  assert.deepEqual(phone.taps, [["120", "300"]]);
});

test("when the reading is missing, a command gives the picture once two in a row are the same", async () => {
  const blind = { screen: "app pretend (not answering yet)", elements: [], fields: [], unread: "the app in front is not answering it" };
  const phone = showing([blind], { pictures: [png(804, 1748, 1), png(804, 1748, 2), png(804, 1748, 3), png(804, 1748, 3)] });
  const { status, text } = await printed(() => acted(phone.device, () => 0));
  assert.equal(status, 0);
  assert.equal(phone.taken.length, 4);
  assert.match(text, /cannot be relied on: the app in front is not answering it/);
  assert.match(text, /read it/);
  assert.match(text, /no need to take one/);
});

test("where the last reading could not be read, the next command does not read first, and reads once after", async () => {
  const blind = { screen: "screen", elements: [], fields: [], unread: "the screen never stops moving" };
  const phone = showing([blind]);
  await printed(() => snapshot(phone.device));
  const before = phone.looks();
  await printed(() => acted(phone.device, () => 0));
  assert.equal(phone.looks() - before, 1);
});

test("a reading that works again after a tap is printed as usual, with no picture named", async () => {
  const blind = { screen: "screen", elements: [], fields: [], unread: "the screen never stops moving" };
  const open = reading([el("e1", "button", "New checklist", 500, 1800), el("e2", "text", "Home", 100, 200)]);
  const phone = showing([blind]);
  await printed(() => snapshot(phone.device));
  // The tap closes what kept the screen moving.
  const { text } = await printed(() => acted(phone.device, () => ((phone.device.look = () => open), 0)));
  assert.match(text, /screen now:\napp pretend\n@e1 button "New checklist"/);
  assert.doesNotMatch(text.split("screen now:")[1], /cannot be relied on/);
});

test("typing goes ahead when the reading is missing, since it cannot say that no field has the keyboard", async () => {
  const blind = { screen: "app pretend (not answering yet)", elements: [], fields: [], unread: "the app in front is not answering it" };
  const phone = showing([blind]);
  const sent = [];
  Object.assign(phone.device, { needsField: true, type: (text) => (sent.push(text), 0), enter: () => (sent.push("\n"), 0) });
  const { status, text } = await printed(() => typeText(phone.device, "127.0.0.1:8081\n"));
  assert.equal(status, 0);
  assert.deepEqual(sent, ["127.0.0.1:8081", "\n"]);
  assert.match(text, /Typed, and Return was pressed\. What the field holds could not be checked\./);
  assert.match(text, /read it/);
});

test("words waited for and not found in a reading that cannot be relied on come with the picture", async () => {
  const blind = { screen: "app pretend (not answering yet)", elements: [], fields: [], unread: "the app in front is not answering it" };
  const phone = showing([blind]);
  const { status, text } = await printed(() => waitFor(phone.device, "Continue", { seconds: 0 }));
  assert.equal(status, 1);
  assert.match(text, /is not in the reading/);
  assert.match(text, /read it/);
});

test("a reading that took the whole patience to fail is not asked for again", async () => {
  let looks = 0;
  const device = { every: 1, look: () => (looks++, spawnSyncSleep(60), { screen: "screen", fields: [] }) };
  await lookFor(device, () => false, { patience: 40 });
  assert.equal(looks, 1);
});

test("an Android screen that never stops moving is asked for twice, or once when that is known, and said to be moving", () => {
  let asked = 0;
  const dump = () => (asked++, { stdout: "", stderr: "ERROR: could not get idle state.\n" });
  assert.deepEqual(readAndroid({ dump }), { app: null, elements: [], moving: true });
  assert.equal(asked, 2);
  asked = 0;
  assert.equal(readAndroid({ dump, once: true }).moving, true);
  assert.equal(asked, 1);
});

test("an Android screen that is read is not moving", () => {
  const read = readAndroid({ dump: () => ({ stdout: '<?xml version="1.0"?><hierarchy rotation="0"><node text="Hello" class="android.widget.TextView" package="com.example" bounds="[0,100][200,160]" /></hierarchy>' }) });
  assert.equal(read.moving, undefined);
  assert.equal(read.elements[0].label, "Hello");
});

test("the iPhone's elements are what its tool lists, each with the middle of its frame", () => {
  const json = JSON.stringify({ app: "com.example", elements: [
    { ref: "e1", role: "button", label: "Sign in", frame: [100, 300, 200, 50] },
    { ref: "e2", role: "secureTextField", label: "Password", value: "••", frame: [20, 200, 360, 40] },
    { ref: "e3", role: "text", label: "No frame" },
  ] });
  const elements = iphoneElements(json);
  assert.deepEqual(elements.map((element) => [element.ref, element.role, element.x, element.y]), [["e1", "button", 200, 325], ["e2", "textfield", 200, 220]]);
  assert.deepEqual(iphoneElements("not json"), []);
});

test("the iPhone's lines and elements are of the same screen when they list the same references in order", () => {
  const elements = [{ ref: "e1" }, { ref: "e2" }];
  assert.equal(sameReading('app x\n@e1 button "A" (1,1)\n@e2 button "B" (2,2)', elements), true);
  assert.equal(sameReading('app x\n@e1 button "A" (1,1)', elements), false);
  assert.equal(sameReading('app x\n@e1 button "A" (1,1)\n@e3 button "B" (2,2)', elements), false);
  assert.equal(sameReading("home screen", []), true);
});

test("a PNG's size is read from its first bytes, and anything else has none", () => {
  assert.deepEqual(pngSize(png(804, 1748)), { width: 804, height: 1748 });
  assert.equal(pngSize(Buffer.from("not a picture at all, just words")), null);
  assert.equal(pngSize(null), null);
});

test("fill by reference finds the field the printed reading meant, wherever it is now", async () => {
  const before = reading([el("e1", "text", "Sign in", 200, 100), el("e9", "textfield", "Work email", 200, 300, { value: "" }), el("e10", "textfield", "Password", 200, 400, { value: "" })]);
  // A banner has appeared: every reference is one later, and the fields are lower.
  let value = "";
  const nowFields = () => [el("e1", "text", "Welcome back", 200, 40), el("e2", "text", "Sign in", 200, 100), el("e10", "textfield", "Work email", 200, 360, { value: "" }), el("e11", "textfield", "Password", 200, 460, { value })];
  const taps = [];
  let first = true;
  const device = {
    name: "pretend phone", command: "phone pretend", quiet: 1, every: 1, patience: 30,
    look: () => (first ? ((first = false), before) : reading(nowFields())),
    tapAt: (x, y) => (taps.push([Math.round(x), Math.round(y)]), 0),
    erase: () => 0,
    type: (text) => ((value = text), 0),
    enter: () => 0,
  };
  await printed(() => snapshot(device));
  const { status, text } = await printed(() => fill(device, ["@e10"], "s3cret"));
  assert.equal(status, 0, text);
  assert.equal(taps[0][1], 460);
  assert.match(text, /"Password" holds "s3cret"/);
});

/** Block for a moment, as a slow reading does. */
function spawnSyncSleep(ms) {
  spawnSync("sleep", [String(ms / 1000)]);
}

test("typing goes ahead when the reading lists words and no fields in places that cannot be right", async () => {
  // As one app's sign-in screen read on the iPhone: its words at the very top, and neither of its fields.
  const wrong = [el("e1", "text", "Genius", 26, 11), el("e2", "heading", "Sign in", 184, 20), el("e3", "text", "Work email", 184, 10)].map((element) => ({ ...element, frame: [0, 0, 300, 40] }));
  const phone = showing([reading(wrong)]);
  const sent = [];
  Object.assign(phone.device, { needsField: true, type: (text) => (sent.push(text), 0), enter: () => 0 });
  const { status, text } = await printed(() => typeText(phone.device, "dana.field@example.test"));
  assert.equal(status, 0);
  assert.deepEqual(sent, ["dana.field@example.test"]);
  assert.match(text, /could not be checked/);
});

test("a reading that cannot be relied on is not read again to look for a field before typing", async () => {
  const blind = { screen: "app pretend (not answering yet)", elements: [], fields: [], unread: "the app in front is not answering it" };
  const phone = showing([blind]);
  Object.assign(phone.device, { needsField: true, patience: 5000, type: () => 0, enter: () => 0 });
  const started = Date.now();
  await printed(() => typeText(phone.device, "hello", blind));
  assert.equal(phone.looks(), 0);
  assert.ok(Date.now() - started < 2000);
});
