// Using a phone the way a person does, with the same commands for the iPhone and the Android phone
// (phone.mjs): read the screen, tap, type, fill a field, scroll, open a link. Each phone is a small set of
// functions over its own tool (agent-sim for the iPhone, adb for Android); what the commands do with them is
// written once, below, so the two phones behave alike.
//
// Every command that changes the screen waits until the screen is still and prints it (screen.mjs says why),
// so nobody has to sleep and look again, and typing says what the field holds afterwards.

import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { adbText, held, linesOf, parseUi, sameField, settle, sleep, targetOf, typedInto } from "./screen.mjs";

const out = (text) => process.stdout.write(text + "\n");
const run = (command, args, options = {}) => spawnSync(command, args, { encoding: "utf8", maxBuffer: 64 * 1024 * 1024, ...options });
const BACKSPACE = "\b";

// ── The iPhone, through agent-sim ───────────────────────────────────────────────────────────────────────

/** A phone's screen read once: its lines, and its text fields with what each holds and where it is. */
export function iphone(env) {
  const sim = (args) => run("agent-sim", args, { env });
  const shown = (args) => spawnSync("agent-sim", args, { stdio: "inherit", env }).status ?? 1;
  const fieldsOf = (json) => {
    let elements = [];
    try {
      elements = JSON.parse(json).elements ?? [];
    } catch {
      // No screen to read: no fields.
    }
    return elements.filter((element) => /field|search/i.test(element.role ?? "") && Array.isArray(element.frame)).map((element) => ({
      ref: element.ref, role: "textfield", label: element.label ?? "", value: element.value,
      x: element.frame[0] + element.frame[2] / 2, y: element.frame[1] + element.frame[3] / 2, frame: element.frame,
    }));
  };
  return {
    name: "iPhone",
    command: "phone ios",
    // The iPhone's screen can be read while it is still moving, so it is still when two readings agree.
    quiet: 2,
    every: 300,
    look: () => {
      const fields = fieldsOf(sim(["snapshot", "--json"]).stdout ?? "");
      // The lines are read last: `@e3` in the next command means what it means in them.
      const result = sim(["snapshot"]);
      return { screen: (result.status === 0 ? result.stdout : result.stderr || result.stdout || "").trim(), fields };
    },
    tap: (words) => shown(["tap", ...words]),
    tapAt: (x, y) => sim(["tap", String(Math.round(x)), String(Math.round(y))]).status ?? 1,
    type: (text) => sim(["type", text]).status ?? 1,
    // Return as a key of its own. Sent in the same call as text, agent-sim types the whole text as key presses.
    enter: () => sim(["type", "--keys", "\n"]).status ?? 1,
    erase: (count) => sim(["type", "--keys", BACKSPACE.repeat(count)]).status ?? 1,
    // With no field to take them, typed letters reach the app as key presses, and a development build takes
    // some of those as shortcuts: "d" opens its developer menu and "r" reloads it.
    needsField: true,
  };
}

// ── The Android phone, through adb ──────────────────────────────────────────────────────────────────────

const lastScreen = path.join(os.homedir(), ".workstream", "android-screen.json");

/** The Android screen as it is now, kept so that `@e3` in the next command means what it meant here. */
export function readAndroid() {
  for (let attempt = 0; attempt < 4; attempt++) {
    // The dump goes to the phone's terminal, which is this command's output. Android refuses while the screen
    // is in the middle of an animation, so a refusal is asked again.
    const result = run("adb", ["exec-out", "uiautomator", "dump", "/dev/tty"]);
    const xml = result.stdout ?? "";
    if (xml.includes("<hierarchy")) {
      const screen = parseUi(xml);
      mkdirSync(path.dirname(lastScreen), { recursive: true });
      writeFileSync(lastScreen, JSON.stringify(screen));
      return screen;
    }
    spawnSync("sleep", ["0.4"]);
  }
  return { app: null, elements: [] };
}

function rememberedAndroid() {
  try {
    return existsSync(lastScreen) ? JSON.parse(readFileSync(lastScreen, "utf8")) : readAndroid();
  } catch {
    return readAndroid();
  }
}

const adb = (...args) => run("adb", ["shell", ...args]).status ?? 1;

export function android() {
  const tapAt = (x, y) => adb("input", "tap", String(Math.round(x)), String(Math.round(y)));
  return {
    name: "Android phone",
    command: "phone android",
    // Android reads its screen only once it has stopped moving, and takes about two seconds to: one reading
    // that differs from before is the screen after the action.
    quiet: 1,
    every: 100,
    look: () => {
      const read = readAndroid();
      return { screen: linesOf(read), fields: read.elements.filter((element) => element.role === "textfield") };
    },
    tap: (words) => {
      // A reference is to the last reading; words are looked for on the screen as it is now.
      const named = /^@?e\d+$/.test(words.join(" "));
      const target = targetOf(words, (named ? rememberedAndroid() : readAndroid()).elements);
      if (!target) {
        out(`Nothing on the Android screen matches ${JSON.stringify(words.join(" "))}. \`phone android snapshot\` lists what is there.`);
        return 1;
      }
      if (target.ref) out(`tapped @${target.ref} ${target.role} ${JSON.stringify(target.label)}`);
      return tapAt(target.x, target.y);
    },
    tapAt,
    type: (text) => {
      const lines = text.split("\n");
      for (const [index, line] of lines.entries()) {
        if (line && adb("input", "text", adbText(line)) !== 0) return 1;
        if (index < lines.length - 1 && adb("input", "keyevent", "66") !== 0) return 1;
      }
      return 0;
    },
    enter: () => adb("input", "keyevent", "66"),
    erase: (count) => {
      // To the end of the text first, then back over it.
      if (adb("input", "keyevent", "123") !== 0) return 1;
      for (let left = count; left > 0; left -= 40) {
        if (adb("input", "keyevent", ...Array(Math.min(left, 40)).fill("67")) !== 0) return 1;
      }
      return 0;
    },
  };
}

// ── What the commands do, on either phone ───────────────────────────────────────────────────────────────

/** A reading with next to nothing in it: the app's name and at most one thing. */
export const thin = (screen) => screen.split("\n").filter((line) => line.startsWith("@")).length < 2;

/**
 * What to do when the reading cannot be what a person sees. A reading lists what the app says is on the
 * screen. A system dialog, and some overlays an app draws, are not in it: the first task that met them saw a
 * reading with one line while a tip covered the screen, and was told three times that nothing had changed
 * when a dialog had closed.
 */
const picture = (device) => `\`${device.command} screenshot -o <file>\` saves the picture, and \`${device.command} tap X Y\` taps a point in it`;

const now = (device, screen, settled = true) => {
  out(settled ? `\nThe ${device.name}'s screen now:` : `\nThe ${device.name}'s screen is still changing. For now:`);
  out(screen);
  if (settled && thin(screen)) out(`\nThis reading lists almost nothing. If the screen is not empty, something the reading cannot see is on it, such as a system dialog or an overlay: ${picture(device)}.`);
};

/** Print the screen as it is once it has stopped changing after an action. */
async function after(device, before) {
  const { screen, changed, settled } = await settle(async () => device.look().screen, {
    before, quiet: device.quiet, every: device.every, empty: (text) => !text.includes("\n"),
  });
  if (changed) now(device, screen, settled);
  else out(`The reading of the ${device.name}'s screen did not change. If something is still loading, \`${device.command} wait "words it will show"\` waits for it. If you tapped something the reading does not list, such as a system dialog or an overlay, the picture may have changed: ${picture(device)}.`);
}

/** Do something to the phone, then wait for the screen and print it. `wait: false` returns at once. */
export async function acted(device, action, { wait = true } = {}) {
  const before = wait ? device.look().screen : null;
  const status = await action();
  if (status !== 0 || !wait) return status;
  await after(device, before);
  return 0;
}

const quoted = (field) => (field.label ? `The field ${JSON.stringify(field.label)}` : "The field");
const holds = (result) => (result.hidden ? `holds ${[...result.value].length} hidden characters` : `holds ${JSON.stringify(result.value)}`);

/**
 * Type into the field that has the keyboard, wait until the text is all there, and say what the field holds.
 * `first` is the screen before, when the caller has just read it.
 */
export async function typeText(device, text, first = device.look(), { exact = false } = {}) {
  const body = text.replace(/\n+$/, "");
  const sends = body !== text;
  if (device.needsField && body && first.fields.length === 0) {
    out(`Nothing on the ${device.name}'s screen is a text field, so nothing was typed: with no field to take them, the letters reach the app as key presses. Tap the field first, or use \`${device.command} fill @e3 "text"\`. \`${device.command} type --keys "text"\` sends key presses on purpose.`);
    return 1;
  }
  if (!body || body.includes("\n")) {
    // Return alone, or text of several lines: sent as it is.
    const status = body ? device.type(text) : device.enter();
    if (status !== 0) return status;
    await after(device, first.screen);
    return 0;
  }
  const status = device.type(body);
  if (status !== 0) return status;
  // Keys arrive one at a time, slower when the desktop is busy: wait for all of them before anything else is sent.
  const patience = Math.max(6000, 400 * body.length);
  const started = Date.now();
  let result;
  let last;
  do {
    await sleep(device.every);
    last = device.look();
    result = typedInto(first.fields, last.fields, body, { exact });
  } while (!result.ok && Date.now() - started < patience);
  if (sends) {
    // Return goes after the text is all there, as a key of its own.
    if (device.enter() !== 0) return 1;
    out(result.ok ? `${quoted(result.field)} ${holds(result).replace(/^holds/, "held")}, and Return was pressed.` : "Typed, and Return was pressed.");
    await after(device, last.screen);
    return 0;
  }
  let failed = 0;
  if (result.ok) out(`${quoted(result.field)} ${holds(result)}.`);
  else if (result.field) {
    out(exact
      ? `${quoted(result.field)} holds ${JSON.stringify(result.value)}, not ${JSON.stringify(body)}.`
      : `${quoted(result.field)} holds ${JSON.stringify(result.value)}, which is not what it held with ${JSON.stringify(body)} added. Empty it and type again with \`${device.command} fill @${result.field.ref} ${JSON.stringify(body)}\`.`);
    failed = 1;
  } else if (first.fields.length + last.fields.length === 0) out("Typed. Nothing on the screen is a text field, so what was typed could not be checked.");
  else {
    out(`No field on the screen took the text. Tap the field first, or name it: \`${device.command} fill @${(last.fields[0] ?? first.fields[0]).ref} ${JSON.stringify(body)}\`.`);
    failed = 1;
  }
  now(device, last.screen);
  return failed;
}

/** Tap a field, empty it, type the text, and say what the field holds. */
export async function fill(device, words, text) {
  let seen = device.look();
  const field = targetOf(words, seen.fields);
  if (!field || field.ref === undefined) {
    out(`No field on the ${device.name}'s screen matches ${JSON.stringify(words.join(" "))}. \`${device.command} snapshot\` lists fields as textfield.`);
    return 1;
  }
  const here = () => sameField(field, seen.fields);
  // The end of the field, so the cursor is after what it holds.
  const tapEnd = () => {
    const at = here() ?? field;
    return device.tapAt(at.frame[0] + at.frame[2] - 8, at.frame[1] + at.frame[3] / 2);
  };
  if (tapEnd() !== 0) return 1;
  await sleep(600);
  seen = device.look();
  // Delete what it holds until a reading says it holds nothing. Words that do not go when they are deleted
  // are the field's own, shown while it is empty (screen.mjs, held).
  let own = false;
  for (let round = 0; round < 4 && !own && here() && held(here()); round++) {
    const value = held(here());
    if (round > 0) tapEnd();
    device.erase([...value].length + 2);
    const erased = await settle(async () => {
      seen = device.look();
      return here() ? held(here()) : "";
    }, { before: value, quiet: device.quiet, every: device.every, patience: 1800, limit: 6000 });
    own = !erased.changed;
  }
  if (!own && here() && held(here())) {
    out(`${quoted(field)} could not be emptied: it still holds ${JSON.stringify(held(here()))}.`);
    return 1;
  }
  if (!text) {
    out(`${quoted(field)} is empty.`);
    now(device, seen.screen);
    return 0;
  }
  // Emptied, so the field must end up holding the text and nothing else.
  return typeText(device, text, seen, { exact: true });
}

/** Wait until words are on the screen, or with `gone` until they are not. */
export async function waitFor(device, text, { gone = false, seconds = 20 } = {}) {
  const wanted = text.toLowerCase();
  const started = Date.now();
  for (;;) {
    const { screen } = device.look();
    if (screen.toLowerCase().includes(wanted) !== gone) {
      out(screen);
      return 0;
    }
    if (Date.now() - started > seconds * 1000) {
      out(`${JSON.stringify(text)} is ${gone ? "still" : "not"} on the ${device.name}'s screen after ${seconds} seconds:\n${screen}`);
      return 1;
    }
    await sleep(500);
  }
}
