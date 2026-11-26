// What the phone commands share, whichever phone they drive (phone.mjs): waiting until a screen has stopped
// changing, checking that typed text is what a field now holds, and reading an Android screen into the same
// lines the iPhone's are read in. Nothing here touches a phone, so all of it is tested without one.
//
// Why these exist. A tap or a keystroke is sent in milliseconds and shown some time later, longer when the
// desktop is busy. A command that returns as soon as it has sent leaves whoever sent it to guess how long to
// wait, and the next command can land on a screen that is still catching up: on the iPhone, text typed while
// earlier keys were still arriving came out with its digits shifted (`8081` as `*)*!`). So every command that
// changes the screen waits until the screen is still, and says what is on it.

export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Read a screen until it has stopped changing. `before` is the screen as it was before the action.
 *
 * - changed and then the same `quiet` times in a row, with something on it: settled.
 * - never different from `before` for `patience` ms: nothing happened.
 * - still changing after `limit` ms: what is there now, said as not settled.
 */
export async function settle(read, { before = null, quiet = 2, every = 300, patience = 2500, limit = 10_000, empty = () => false, now = Date.now, wait = sleep } = {}) {
  const started = now();
  let last = null;
  let same = 0;
  for (;;) {
    const screen = await read();
    same = screen === last ? same + 1 : 1;
    last = screen;
    const changed = before === null || screen !== before;
    const waited = now() - started;
    // A screen with nothing on it yet is one that is loading: keep waiting for what it will show.
    if (changed && same >= quiet && !empty(screen)) return { screen, changed: before !== null, settled: true };
    if (!changed && waited >= patience) return { screen, changed: false, settled: true };
    if (waited >= limit) return { screen, changed, settled: false };
    await wait(every);
  }
}

/** Whether `after` is `before` with `text` put in at one place. */
export function insertedAt(before, after, text) {
  if (after.length !== before.length + text.length) return false;
  for (let at = 0; at <= before.length; at++) {
    if (before.slice(0, at) + text + before.slice(at) === after) return true;
  }
  return false;
}

const MASKED = /^[•●∙*]+$/u;

/**
 * What a field holds, as far as one reading can say: nothing when it shows only its own name. An empty field
 * that shows other words in grey ("you@company.com") reads as holding them, on the iPhone at least, and
 * nothing in a reading tells those from typed text. So the callers below never trust this to mean a field
 * is empty: typing is right when the field holds exactly the text, and a field whose words do not go when
 * they are deleted is empty.
 */
export const held = (field) => (field.value === undefined || field.value === null || field.value === field.label ? "" : String(field.value));

/** The same field in a later reading: the one with its label nearest to where it was (a keyboard moves a form). */
export function sameField(field, fields) {
  const named = fields.filter((other) => other.label === field.label);
  const among = named.length ? named : fields;
  return among.reduce((best, other) => (best === null || Math.abs(other.y - field.y) < Math.abs(best.y - field.y) ? other : best), null);
}

/**
 * Which field took the text that was typed, from the fields before and after. `ok` when one field now holds
 * exactly the text (it was empty, whatever it showed), or what it held with the text put in, or, for a field
 * that hides what it holds, that many more characters. `exact` is for a field that was just emptied: only
 * the text itself will do. Otherwise the field that changed, so the caller can say what it holds instead.
 */
export function typedInto(before, after, text, { exact = false } = {}) {
  let changed = null;
  const length = [...text].length;
  for (const field of after) {
    const was = sameField(field, before);
    const from = was ? held(was) : "";
    const to = held(field);
    if (to === from) continue;
    if (to === text) return { ok: true, field, value: to };
    if (MASKED.test(to) && [...to].length === (exact || !MASKED.test(from) ? 0 : [...from].length) + length) return { ok: true, field, value: to, hidden: true };
    if (!exact && insertedAt(from, to, text)) return { ok: true, field, value: to };
    changed ??= { ok: false, field, value: to };
  }
  return changed ?? { ok: false, field: null, value: null };
}

// ── An Android screen, from `uiautomator dump` ───────────────────────────────────────────────────────────

const ENTITIES = { amp: "&", lt: "<", gt: ">", quot: '"', apos: "'" };
const decode = (text) => text.replace(/&(#x?[0-9a-fA-F]+|\w+);/g, (whole, name) => {
  if (name[0] !== "#") return ENTITIES[name] ?? whole;
  const code = name[1] === "x" || name[1] === "X" ? parseInt(name.slice(2), 16) : parseInt(name.slice(1), 10);
  return Number.isFinite(code) ? String.fromCodePoint(code) : whole;
});

function attributesOf(tag) {
  const attributes = {};
  for (const [, name, value] of tag.matchAll(/([\w:-]+)="([^"]*)"/g)) attributes[name] = decode(value);
  return attributes;
}

function roleOf(attributes) {
  const kind = (attributes.class ?? "").split(".").pop();
  if (/EditText$/.test(kind)) return "textfield";
  if (/Switch$/.test(kind)) return "switch";
  if (/CheckBox$/.test(kind)) return "checkbox";
  if (/RadioButton$/.test(kind)) return "radio";
  if (/Button$/.test(kind) || attributes.clickable === "true") return "button";
  if (/ImageView$/.test(kind)) return "image";
  return "text";
}

/**
 * What is on an Android screen: one element for each thing with words, each field, and each thing to tap that
 * has nothing inside it. A thing to tap that only wraps its own words (as a React Native button does) is
 * listed once, as the button.
 */
export function parseUi(xml) {
  const elements = [];
  const labels = [];
  let app = null;
  for (const [tag] of xml.matchAll(/<node\b[^>]*>|<\/node>/g)) {
    if (tag === "</node>") {
      labels.pop();
      continue;
    }
    const leaf = tag.endsWith("/>");
    const attributes = attributesOf(tag);
    app ??= attributes.package || null;
    const bounds = /\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]/.exec(attributes.bounds ?? "");
    const role = roleOf(attributes);
    const words = (attributes.text || attributes["content-desc"] || "").trim();
    const inside = labels.filter(Boolean);
    let listed = null;
    const [x1, y1, x2, y2] = bounds ? bounds.slice(1).map(Number) : [0, 0, 0, 0];
    // Something with no size is not on the screen.
    if (x2 > x1 && y2 > y1) {
      const field = role === "textfield";
      const hint = attributes.hint?.trim() || "";
      const repeats = words && inside.includes(words);
      if (field || (words && !repeats) || (leaf && attributes.clickable === "true")) {
        const id = (attributes["resource-id"] ?? "").split("/").pop() || "";
        const element = {
          role,
          label: field ? hint || (attributes["content-desc"] ?? "").trim() || id : words || id,
          x: Math.round((x1 + x2) / 2), y: Math.round((y1 + y2) / 2), frame: [x1, y1, x2 - x1, y2 - y1],
        };
        // A field's text is what it holds, except where Android reports its hint there: an empty field that
        // has a hint says so, and one with none shows the hint as its text.
        if (field) element.value = attributes.text && attributes.text !== hint ? attributes.text : "";
        if (attributes.password === "true") element.hidden = true;
        for (const state of ["focused", "selected", "checked"]) if (attributes[state] === "true") element[state] = true;
        if (attributes.enabled === "false") element.disabled = true;
        elements.push(element);
        listed = element.label;
      }
    }
    if (!leaf) labels.push(listed ?? (words || null));
  }
  elements.forEach((element, index) => (element.ref = `e${index + 1}`));
  return { app, elements };
}

/** A screen as lines, one for each element, written the way the iPhone's are. */
export function linesOf({ app, elements }) {
  const lines = [app ? `app ${app}` : "screen"];
  for (const element of elements) {
    const parts = [`@${element.ref}`, element.role, JSON.stringify(element.label ?? "")];
    if (element.role === "textfield") parts.push(`value=${JSON.stringify(element.hidden ? "•".repeat([...(element.value ?? "")].length) : element.value ?? "")}`);
    for (const state of ["focused", "selected", "checked", "disabled"]) if (element[state]) parts.push(state);
    parts.push(`(${element.x},${element.y})`);
    lines.push(parts.join(" "));
  }
  return lines.join("\n");
}

/**
 * What a command points at: `@e3` from the last reading, a point (`540 1200`), or words, which name the first
 * element that says exactly that, and failing that the first that contains it. Null when nothing matches.
 */
export function targetOf(words, elements) {
  if (words.length === 2 && words.every((word) => /^-?\d+(\.\d+)?$/.test(word))) return { x: Number(words[0]), y: Number(words[1]) };
  const text = words.join(" ");
  const ref = /^@?(e\d+)$/.exec(text);
  if (ref) return elements.find((element) => element.ref === ref[1]) ?? null;
  const wanted = text.toLowerCase();
  const said = (element) => String(element.label ?? "").toLowerCase();
  return elements.find((element) => said(element) === wanted) ?? elements.find((element) => said(element).includes(wanted)) ?? null;
}

/**
 * Text as `adb shell input text` takes it: one word for the phone's shell, with `%s` where a space goes (the
 * command's own way to write one).
 */
export const adbText = (text) => `'${text.replace(/ /g, "%s").replace(/'/g, `'\\''`)}'`;
