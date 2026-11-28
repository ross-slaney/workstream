import { test } from "node:test";
import assert from "node:assert/strict";
import { fill, MOST_PRESSES, press, thin, typeText } from "./touch.mjs";

/** A pretend phone with one field, which records what was sent to it. */
function pretend({ fields = [{ ref: "e2", role: "textfield", label: "New step", value: "New step", x: 10, y: 10, frame: [0, 0, 20, 20] }], needsField = false } = {}) {
  const sent = [];
  let value = fields[0]?.value;
  return {
    sent,
    device: {
      name: "pretend phone", command: "phone pretend", quiet: 1, every: 1, needsField, patience: 40,
      look: () => ({
        screen: ["app pretend", ...fields.map((field) => `@${field.ref} textfield "${field.label}" value="${value}"`), '@e9 button "Add"'].join("\n"),
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
  const write = process.stdout.write;
  let text = "";
  process.stdout.write = (chunk) => ((text += chunk), true);
  try {
    return { status: await run(), text };
  } finally {
    process.stdout.write = write;
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
