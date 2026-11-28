// Using a phone the way a person does, with the same commands for the iPhone and the Android phone
// (phone.mjs): read the screen, tap, type, fill a field, scroll, open a link. Each phone is a small set of
// functions over its own tool (agent-sim for the iPhone, adb for Android); what the commands do with them is
// written once, below, so the two phones behave alike.
//
// Every command that changes the screen waits until the screen is still and prints it (screen.mjs says why),
// so nobody has to sleep and look again, and typing says what the field holds afterwards.

import { spawnSync } from "node:child_process";
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { adbText, held, linesOf, parseUi, sameField, settle, sleep, targetOf, typedInto } from "./screen.mjs";

/** Where the commands print. A test puts its own `write` here, and leaves the test runner's output alone. */
export const printing = { write: (text) => process.stdout.write(text) };
const out = (text) => printing.write(text + "\n");
const run = (command, args, options = {}) => spawnSync(command, args, { encoding: "utf8", maxBuffer: 64 * 1024 * 1024, ...options });
const BACKSPACE = "\b";

// ── The iPhone, through agent-sim ───────────────────────────────────────────────────────────────────────

/** What agent-sim's JSON lists, each with the middle of its frame: where a tap on it goes. */
export function iphoneElements(json) {
  let elements = [];
  try {
    elements = JSON.parse(json).elements ?? [];
  } catch {
    // No screen to read: nothing on it.
  }
  return elements.filter((element) => Array.isArray(element.frame)).map((element) => ({
    ref: element.ref, role: /field|search/i.test(element.role ?? "") ? "textfield" : element.role ?? "", label: element.label ?? "", value: element.value,
    x: element.frame[0] + element.frame[2] / 2, y: element.frame[1] + element.frame[3] / 2, frame: element.frame,
  }));
}

/** Whether the lines and the elements, which are two readings, are of the same screen: they list the same things. */
export function sameReading(screen, elements) {
  const lines = screen.split("\n").filter((line) => line.startsWith("@"));
  return lines.length === elements.length && lines.every((line, index) => line.startsWith(`@${elements[index].ref} `));
}

/** How many of a picture's pixels make one of the points a tap counts in, from the simulator's own record of its phone. */
function iphoneScale() {
  const phones = path.join(os.homedir(), ".local/share/x86sim/var");
  try {
    for (const phone of readdirSync(phones)) {
      const scale = /^SIMULATOR_MAINSCREEN_SCALE=([\d.]+)/m.exec(readFileSync(path.join(phones, phone, "sim.env"), "utf8"));
      if (scale) return Number(scale[1]);
    }
  } catch {
    // Not recorded where it was looked for.
  }
  return null;
}

/** A phone's screen read once: its lines, what is on it with where each thing is, and its text fields. */
export function iphone(env) {
  const sim = (args) => run("agent-sim", args, { env });
  const shown = (args) => spawnSync("agent-sim", args, { stdio: "inherit", env }).status ?? 1;
  return {
    id: "iphone",
    name: "iPhone",
    command: "phone ios",
    // The iPhone's screen can be read while it is still moving, so it is still when two readings agree.
    quiet: 2,
    every: 300,
    look: () => {
      for (let attempt = 0; ; attempt++) {
        const elements = iphoneElements(sim(["snapshot", "--json"]).stdout ?? "");
        const result = sim(["snapshot"]);
        const screen = (result.status === 0 ? result.stdout : result.stderr || result.stdout || "").trim();
        // The lines and the frames come from two readings. A screen that moved between them is read again.
        if (attempt < 2 && result.status === 0 && !sameReading(screen, elements)) continue;
        const unread = result.status !== 0 || /not answering/i.test(screen.split("\n")[0] ?? "") ? "the app in front is not answering it: it is starting, or busy" : null;
        return { screen, elements, fields: elements.filter((element) => element.role === "textfield"), unread };
      }
    },
    picture: (file) => {
      if ((sim(["screenshot", "-o", file]).status ?? 1) !== 0) return null;
      try {
        return readFileSync(file);
      } catch {
        return null;
      }
    },
    // A tap counts in points, and the picture has more pixels than that.
    pixelsEach: iphoneScale,
    tap: (words) => shown(["tap", ...words]),
    tapAt: (x, y) => sim(["tap", String(Math.round(x)), String(Math.round(y))]).status ?? 1,
    type: (text) => sim(["type", text]).status ?? 1,
    // Whether a field would take text typed now. agent-sim puts text into the field that has the keyboard, and
    // when no field has it, or the app is not answering, it presses the letters as keys instead and says so.
    // Typing nothing asks the same question without pressing a key.
    takes: () => {
      const asked = sim(["type", ""]);
      return asked.status === 0 && !/as key presses/i.test(`${asked.stdout ?? ""}${asked.stderr ?? ""}`);
    },
    // Return as a key of its own. Sent in the same call as text, agent-sim types the whole text as key presses.
    enter: () => sim(["type", "--keys", "\n"]).status ?? 1,
    erase: (count) => sim(["type", "--keys", BACKSPACE.repeat(count)]).status ?? 1,
    // The keys `press` knows here. The phone's buttons (home, lock) are agent-sim's own `press`.
    keys: { enter: "\n", return: "\n", delete: BACKSPACE, backspace: BACKSPACE },
    // All the presses in one call: sent one call each, 45 of them took longer than a command may run.
    press: (key, times) => sim(["type", "--keys", key.repeat(times)]).status ?? 1,
    // With no field to take them, typed letters reach the app as key presses, and a development build takes
    // some of those as shortcuts: "d" opens its developer menu and "r" reloads it.
    needsField: true,
  };
}

// ── The Android phone, through adb ──────────────────────────────────────────────────────────────────────

/** What Android says when it gave up waiting for a screen to stop moving before reading it. */
const NEVER_STILL = /could not get idle state/i;

/**
 * The Android screen as it is now. Android reads a screen only once it has stopped moving, and waits ten
 * seconds for that: a refusal in the middle of an animation is asked again, but a screen that never stops
 * (a spinner, a tip that pulses) is asked twice and then said to be `moving`. The first task that met one
 * waited two minutes over a tip for each tap, to be told nothing had changed.
 */
export function readAndroid({ dump = () => run("adb", ["exec-out", "uiautomator", "dump", "/dev/tty"]), once = false } = {}) {
  const enough = once ? 1 : 2;
  let moving = 0;
  for (let attempt = 0; attempt < 4 && moving < enough; attempt++) {
    // The dump goes to the phone's terminal, which is this command's output.
    const result = dump();
    const xml = result.stdout ?? "";
    if (xml.includes("<hierarchy")) return parseUi(xml);
    if (NEVER_STILL.test(xml + (result.stderr ?? ""))) moving++;
    else spawnSync("sleep", ["0.4"]);
  }
  return { app: null, elements: [], moving: moving >= enough };
}

const adb = (...args) => run("adb", ["shell", ...args]).status ?? 1;

/** The keys `phone android press` knows, as Android numbers them. */
export const ANDROID_KEYS = { enter: "66", return: "66", back: "4", home: "3", delete: "67", backspace: "67", tab: "61" };

export function android() {
  const tapAt = (x, y) => adb("input", "tap", String(Math.round(x)), String(Math.round(y)));
  // Many presses of one key go forty to a call, which Android takes at once.
  const press = (key, times) => {
    for (let left = times; left > 0; left -= 40) {
      if (adb("input", "keyevent", ...Array(Math.min(left, 40)).fill(key)) !== 0) return 1;
    }
    return 0;
  };
  return {
    name: "Android phone",
    command: "phone android",
    // Android reads its screen only once it has stopped moving, and takes about two seconds to: one reading
    // that differs from before is the screen after the action.
    quiet: 1,
    every: 100,
    id: "android",
    // `once`: a screen known to keep moving is asked for one time, not two.
    look: ({ once = false } = {}) => {
      const read = readAndroid({ once });
      return {
        screen: linesOf(read), elements: read.elements, fields: read.elements.filter((element) => element.role === "textfield"),
        unread: read.moving ? "the screen never stops moving (something on it is animated), and Android reads a screen only once it is still" : null,
      };
    },
    picture: (file) => {
      const shot = spawnSync("adb", ["exec-out", "screencap", "-p"], { maxBuffer: 64 * 1024 * 1024 });
      if (shot.status !== 0 || !shot.stdout?.length) return null;
      writeFileSync(file, shot.stdout);
      return shot.stdout;
    },
    // A tap counts in the picture's own pixels.
    pixelsEach: () => 1,
    tap: (words, seen) => {
      // Words are looked for on the screen as it is now (a reference is to the last reading printed: `tap`, below).
      const target = targetOf(words, (seen ?? readAndroid()).elements);
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
    // As on the iPhone: with no field to take them, letters reach the app as key presses. A development build
    // took one as the shortcut to its developer menu, which then asked for a permission in Android's settings.
    needsField: true,
    // To the end of the text first, then back over it.
    erase: (count) => (adb("input", "keyevent", "123") !== 0 ? 1 : press("67", count)),
    keys: ANDROID_KEYS,
    press,
  };
}

// ── What the commands do, on either phone ───────────────────────────────────────────────────────────────

/** A reading with next to nothing in it: the app's name and at most one thing. */
export const thin = (screen) => screen.split("\n").filter((line) => line.startsWith("@")).length < 2;

// ── The reading that was printed, which the next command's `@e3` means ──────────────────────────────────

const inMind = new WeakMap();
const keptIn = (device) => path.join(os.homedir(), ".workstream", `${device.id}-screen.json`);

/**
 * Keep the reading that was just printed. A reference is to what its reader saw, not to the screen as it is
 * when the next command starts: a command reads the screen again before it acts, and on a screen that had
 * moved `tap @e18` found nothing, or something else, right after a reading that listed it.
 */
export function remember(device, seen) {
  const kept = { elements: seen.elements ?? [], misplaced: misplaced(seen.elements), unread: seen.unread ?? null, at: Date.now() };
  inMind.set(device, kept);
  if (!device.id) return;
  try {
    mkdirSync(path.dirname(keptIn(device)), { recursive: true });
    writeFileSync(keptIn(device), JSON.stringify(kept));
  } catch {
    // Kept for this command only.
  }
}

/** The last reading that was printed, by this command or the one before it; null when there is none. */
export function remembered(device) {
  if (inMind.has(device)) return inMind.get(device);
  try {
    return device.id ? JSON.parse(readFileSync(keptIn(device), "utf8")) : null;
  } catch {
    return null;
  }
}

// ── A reading that cannot be relied on, and the picture that can ────────────────────────────────────────

/**
 * Whether a reading puts most of what it lists at the very top of the screen. Things are not all there: the
 * reading has each one's place inside whatever holds it, not on the screen. One app's reading on the iPhone
 * was like this, and a tap on "Password" by its reference landed on the field above it.
 */
export function misplaced(elements = []) {
  const placed = elements.filter((element) => Array.isArray(element.frame));
  return placed.length >= 3 && placed.filter((element) => element.frame[1] <= 0).length * 2 > placed.length;
}

/**
 * Why a reading cannot be relied on to say what a person sees, or null when nothing says so. A reading lists
 * what the app says is on the screen. It can be missing (the app does not answer, or the screen never stops
 * moving), misplaced, or without a system dialog or an overlay the app draws: the first task that met those
 * saw a reading with one line while a tip covered the screen.
 */
export function doubt(seen) {
  if (seen.unread) return seen.unread;
  if (misplaced(seen.elements)) return "it puts most of what it lists at the very top of the screen, so its positions are not where things are";
  if (thin(seen.screen)) return "it lists almost nothing, so if the screen is not empty, something the reading cannot see is on it, such as a system dialog or an overlay";
  return null;
}

const byHand = (device) => `\`${device.command} screenshot -o <file>\` saves the picture, and \`${device.command} tap X Y\` taps a point in it`;

/** A PNG's width and height, from its first bytes. */
export const pngSize = (png) => (png?.length >= 24 && png.toString("latin1", 1, 4) === "PNG" ? { width: png.readUInt32BE(16), height: png.readUInt32BE(20) } : null);

/** The picture of the screen once it is still: two in a row the same, or what there is after `limit` ms. */
async function stillPicture(device, file, { limit = 4000 } = {}) {
  let last = null;
  for (const started = Date.now(); ; ) {
    const taken = device.picture(file);
    if (!taken) return last;
    if (last && taken.equals(last)) return taken;
    last = taken;
    if (Date.now() - started > limit) return last;
    await sleep(Math.max(device.every, 150));
  }
}

/** How a point in the picture is tapped: a tap counts in the picture's pixels, or in points of several each. */
function tapping(device, size) {
  const each = device.pixelsEach?.() ?? null;
  const how = `\`${device.command} tap X Y\` taps a point`;
  if (each === 1) return `${how}, counted in the picture's pixels`;
  if (each && size) return `${how}, counted in points of ${each} pixels each: the screen is ${Math.round(size.width / each)} by ${Math.round(size.height / each)} points, so divide a position in the picture by ${each}`;
  return how;
}

/**
 * When the reading cannot be relied on, say why and give the picture instead: saved once the screen is still,
 * so nobody has to take one, guess how long to wait first, and take another.
 */
export async function pictured(device, why, picture) {
  if (!device.picture) {
    out(`\nThis reading cannot be relied on: ${why}. ${byHand(device)}.`);
    return;
  }
  const file = path.join(os.tmpdir(), `${device.id ?? "phone"}-screen.png`);
  picture ??= await stillPicture(device, file);
  if (!picture) {
    out(`\nThis reading cannot be relied on: ${why}. The picture could not be saved either: ${byHand(device)}.`);
    return;
  }
  const size = pngSize(picture);
  out(`\nThis reading cannot be relied on: ${why}. The picture of the screen as it is now is ${file}${size ? ` (${size.width} by ${size.height} pixels)` : ""}: read it. ${tapping(device, size)}. While the reading is like this, each command that changes the screen saves the picture again, so there is no need to take one.`);
}

/** Print a reading and keep it, with the picture when the reading cannot be relied on. */
async function now(device, seen, settled = true) {
  out(settled ? `\nThe ${device.name}'s screen now:` : `\nThe ${device.name}'s screen is still changing. For now:`);
  out(seen.screen);
  remember(device, seen);
  const why = settled ? doubt(seen) : null;
  if (why) await pictured(device, why);
}

/** Print the screen as it is once it has stopped changing after an action. */
async function after(device, before) {
  if (before.unread && device.picture) {
    // Nothing could be read before, and reading again is slow where it fails: wait on the picture, then read once.
    const picture = await stillPicture(device, path.join(os.tmpdir(), `${device.id ?? "phone"}-screen.png`));
    const seen = device.look({ once: true });
    if (!doubt(seen)) return now(device, seen);
    out(`\nThe ${device.name}'s screen as its reading has it:`);
    out(seen.screen);
    remember(device, seen);
    return pictured(device, doubt(seen), picture);
  }
  let seen = before;
  const { changed, settled } = await settle(async () => (seen = device.look()).screen, {
    before: before.screen, quiet: device.quiet, every: device.every, empty: (text) => !text.includes("\n"),
  });
  if (changed) return now(device, seen, settled);
  const why = doubt(seen);
  if (why) return pictured(device, `${why}. It did not change either, which says nothing about the screen`);
  out(`The reading of the ${device.name}'s screen did not change. If something is still loading, \`${device.command} wait "words it will show"\` waits for it. If you tapped something the reading does not list, such as a system dialog or an overlay, the picture may have changed: ${byHand(device)}.`);
}

/** Do something to the phone, then wait for the screen and print it. `wait: false` returns at once. */
export async function acted(device, action, { wait = true } = {}) {
  // Where the last reading, a moment ago, could not be read, reading again first only takes the time it takes to fail.
  const kept = remembered(device);
  const unread = kept?.unread && Date.now() - kept.at < 2 * 60_000 ? { screen: "", elements: [], fields: [], unread: kept.unread } : null;
  const before = wait ? unread ?? device.look() : null;
  const status = await action();
  if (status !== 0 || !wait) return status;
  await after(device, before);
  return 0;
}

/** Read the screen and print it: what `snapshot` does. A reading with nothing in it yet is read again for a few seconds. */
export async function snapshot(device) {
  const seen = await lookFor(device, (read) => read.screen.split("\n").some((line) => line.startsWith("@")), { patience: device.patience ?? 6000 });
  out(seen.screen);
  remember(device, seen);
  const why = doubt(seen);
  if (why) await pictured(device, why);
  return 0;
}

/**
 * Tap what the words point at. A reference is to the last reading that was printed, and is tapped where that
 * reading had it; words and a point are the phone's own to find.
 */
export function tap(device, words) {
  const text = words.join(" ");
  if (words.length === 2 && words.every((word) => /^-?\d+(\.\d+)?$/.test(word))) return device.tap(words);
  if (!/^@?e\d+$/.test(text)) {
    // Words are found on the screen as it is now, where its reading can place them.
    const seen = device.look();
    if (!misplaced(seen.elements)) return device.tap(words, seen);
    out(`The reading of the ${device.name}'s screen puts most of what it lists at the very top of the screen, so where it has ${JSON.stringify(text)} is not where it is, and nothing was tapped. Tap it by its position in the picture: \`${device.command} tap X Y\`.`);
    return 1;
  }
  const kept = remembered(device);
  const target = kept ? targetOf(words, kept.elements) : null;
  if (!target) {
    out(`The last reading of the ${device.name}'s screen that was printed has no ${text}. \`${device.command} snapshot\` reads the screen again.`);
    return 1;
  }
  if (kept.misplaced) {
    out(`The reading that listed ${text} put most of what it listed at the very top of the screen, so where it has ${text} is not where it is, and nothing was tapped. Tap it by its position in the picture: \`${device.command} tap X Y\`.`);
    return 1;
  }
  out(`tapped @${target.ref} ${target.role} ${JSON.stringify(target.label)}`);
  return device.tapAt(target.x, target.y);
}

/** The most times one command presses a key. */
export const MOST_PRESSES = 200;

/**
 * Press one of the phone's keys, once or `times` times in one go, then wait for the screen and print it.
 * Null when the phone has no key of that name, so the caller can try what else the word may mean.
 */
export async function press(device, name, { times = 1, wait = true } = {}) {
  const key = device.keys?.[String(name ?? "").toLowerCase()];
  if (!key) return null;
  if (!Number.isInteger(times) || times < 1 || times > MOST_PRESSES) {
    out(`--times is a whole number from 1 to ${MOST_PRESSES}.`);
    return 2;
  }
  return acted(device, () => device.press(key, times), { wait });
}

const quoted = (field) => (field.label ? `The field ${JSON.stringify(field.label)}` : "The field");
const holds = (result) => (result.hidden ? `holds ${[...result.value].length} hidden characters` : `holds ${JSON.stringify(result.value)}`);

/**
 * Type into the field that has the keyboard, wait until the text is all there, and say what the field holds.
 * `first` is the screen before, when the caller has just read it.
 */
/**
 * A reading that has what is looked for, read again for a few seconds while it does not. Right after a tap,
 * while the keyboard comes up, the iPhone's reading can list nothing: the first agent to meet that was told
 * no field was on a screen that had three.
 */
export async function lookFor(device, wanted, { patience = device.patience ?? 5000 } = {}) {
  // Counted from before the first reading: one that took most of the patience to fail is not asked for again.
  const started = Date.now();
  let seen = device.look();
  while (!wanted(seen) && Date.now() - started < patience) {
    await sleep(Math.max(device.every, 150));
    seen = device.look();
  }
  return seen;
}

/** Whether a field takes text now, asked for up to three seconds. */
async function taken(device, { limit = device.patience ?? 3000 } = {}) {
  const started = Date.now();
  for (;;) {
    if (device.takes()) return true;
    if (Date.now() - started >= limit) return false;
    await sleep(device.every);
  }
}

export async function typeText(device, text, first = device.look(), { exact = false } = {}) {
  const body = text.replace(/\n+$/, "");
  const sends = body !== text;
  // A phone that can say whether a field would take the text is asked first. The keyboard comes up a moment
  // after a tap, so it is asked for a few seconds before the answer is believed.
  if (body && device.takes && !(await taken(device))) {
    out(`No field on the ${device.name} has the keyboard${first.unread ? ", or the app is not answering" : ""}, so nothing was typed. Text typed now would reach the app as key presses, and a development build takes some letters as shortcuts: one reloads the app, which looks like a freeze. Tap the field, then type again.`);
    const why = doubt(first);
    if (why) await pictured(device, why);
    return 1;
  }
  // No field in the reading may be a reading taken too soon: look again before believing it.
  // (A reading that cannot be relied on is not waited on: it is not going to list the field.)
  if (device.needsField && body && first.fields.length === 0 && !doubt(first)) first = await lookFor(device, (seen) => seen.fields.length > 0);
  if (device.needsField && body && first.fields.length === 0 && doubt(first)) {
    // A reading that cannot be relied on cannot say that no field has the keyboard: one app's listed its
    // words and none of its fields. Typed all the same, and shown.
    if (device.type(body) !== 0) return 1;
    if (sends) {
      // The text is put in at once, and drawn a moment later: Return goes after it.
      await sleep(Math.max(device.every, 150));
      if (device.enter() !== 0) return 1;
    }
    out(`Typed${sends ? ", and Return was pressed" : ""}. What the field holds could not be checked.`);
    await pictured(device, doubt(first));
    return 0;
  }
  if (device.needsField && body && first.fields.length === 0) {
    out(`Nothing on the ${device.name}'s screen is a text field, so nothing was typed: with no field to take them, the letters reach the app as key presses. Tap the field first, or use \`${device.command} fill @e3 "text"\`. \`${device.command} type --keys "text"\` sends key presses on purpose.`);
    return 1;
  }
  if (!body || body.includes("\n")) {
    // Return alone, or text of several lines: sent as it is.
    const status = body ? device.type(text) : device.enter();
    if (status !== 0) return status;
    await after(device, first);
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
    await after(device, last);
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
  await now(device, last);
  return failed;
}

/** Tap a field, empty it, type the text, and say what the field holds. */
export async function fill(device, words, text) {
  // A reference is to the reading that was printed: the same field is found again on the screen as it is now.
  const named = /^@?e\d+$/.test(words.join(" ")) ? targetOf(words, remembered(device)?.elements ?? []) : null;
  const meant = named?.role === "textfield" ? (read) => sameField(named, read.fields) : (read) => targetOf(words, read.fields);
  let seen = await lookFor(device, meant);
  const field = meant(seen);
  if (field && misplaced(seen.elements)) {
    out(`The reading of the ${device.name}'s screen puts most of what it lists at the very top of the screen, so where it has the field is not where it is, and nothing was typed. Tap the field by its position in the picture (\`${device.command} tap X Y\`), then \`${device.command} type "text"\`.`);
    await pictured(device, doubt(seen));
    return 1;
  }
  if (!field || field.ref === undefined) {
    out(`No field in the reading of the ${device.name}'s screen matches ${JSON.stringify(words.join(" "))}. \`${device.command} snapshot\` lists fields as textfield.`);
    const why = doubt(seen);
    if (why) await pictured(device, why);
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
  // The field is in the reading again once the keyboard is up.
  seen = await lookFor(device, (read) => read.fields.length > 0);
  // Delete what it holds until a reading says it holds nothing. Words that do not go when they are deleted
  // are the field's own, shown while it is empty (screen.mjs, held).
  // One press first: grey words that stay are the field's own, and the field was empty all along. Then as many
  // presses as there are letters and no more. In one app a press on an empty field closed the field, and the
  // text that followed had no field to go to.
  let own = false;
  for (let round = 0; round < 5 && !own && here() && held(here()); round++) {
    const value = held(here());
    if (round > 0) tapEnd();
    device.erase(round === 0 ? 1 : [...value].length);
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
    await now(device, seen);
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
    const seen = device.look();
    const { screen } = seen;
    if (screen.toLowerCase().includes(wanted) !== gone) {
      out(screen);
      remember(device, seen);
      return 0;
    }
    if (Date.now() - started > seconds * 1000) {
      out(`${JSON.stringify(text)} is ${gone ? "still" : "not"} in the reading of the ${device.name}'s screen after ${seconds} seconds:\n${screen}`);
      remember(device, seen);
      // Words the reading cannot see are not known to be missing.
      const why = doubt(seen);
      if (why) await pictured(device, why);
      return 1;
    }
    await sleep(500);
  }
}
