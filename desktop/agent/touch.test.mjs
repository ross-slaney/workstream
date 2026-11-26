import { test } from "node:test";
import assert from "node:assert/strict";
import { thin, typeText } from "./touch.mjs";

/** A pretend phone with one field, which records what was sent to it. */
function pretend({ fields = [{ ref: "e2", role: "textfield", label: "New step", value: "New step", x: 10, y: 10, frame: [0, 0, 20, 20] }], needsField = false } = {}) {
  const sent = [];
  let value = fields[0]?.value;
  return {
    sent,
    device: {
      name: "pretend phone", command: "phone pretend", quiet: 1, every: 1, needsField,
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
