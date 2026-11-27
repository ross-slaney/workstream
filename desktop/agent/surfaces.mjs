#!/usr/bin/env node
// The surfaces on a Workstream desktop: the places a change is looked at, which are the browser and each
// phone. The `surfaces` command, for the agent and for a person at the desktop.
//
//   surfaces arrange   put the browser and the phones side by side, each fully visible, on top of the rest
//   surfaces show      the same, then take a picture of each and add the pictures to the task's conversation
//
// `show` is how work is handed over. The person may read the agent's report in an email or hours later, when
// the desktop is paused, so the report comes with a picture of each surface as it was. The agent looks at the
// same pictures before it writes: in the first task that used a phone, the report said the iPhone showed
// something it did not.
//
// While the agent works, the surface it is using comes to the front by itself (`front`, called by the phone
// command and by the harness when the agent uses the browser), so the person watching sees what is being
// used. In the first task that used a phone this way, the browser covered the phone for eleven minutes.
//
// A window opens where its program last had it, and the last one opened covers the others. When work is
// handed over, the person should see every surface at once without moving a window. The screen is 1280 wide
// (session.sh) and the panel takes the bottom 45.

import { spawnSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { existsSync, mkdirSync, openAsBlob, readFileSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { answerOf, api } from "./api.mjs";

export const SCREEN = { width: 1280, height: 915 };
/**
 * The iPhone's window (phone.mjs opens it this size), the empty strip it has at each side of the phone, and
 * the narrowest a Chromium window goes.
 */
const IPHONE = { width: 430, height: 866, margin: 33 };
const BROWSER_MIN = 508;
/** The strip of buttons the Android emulator keeps at its right side, in a window of its own. */
const EMULATOR_BAR = 54;

/** What a window is, from its title: a surface, or nothing this command moves. */
export function kindOf(title) {
  if (title === "agent-sim") return "iphone";
  if (/^Android Emulator\b/.test(title)) return "android";
  if (/ - Chromium$/.test(title) || title === "Chromium") return "browser";
  return null;
}

/** The windows `wmctrl -lG` lists: id, position, size and title. */
export function windowsOf(listing) {
  const windows = [];
  for (const line of listing.split("\n")) {
    const match = /^(0x[0-9a-f]+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(\d+)\s+(\d+)\s+\S+\s+(.*)$/i.exec(line);
    if (!match || match[2] === "-1") continue;
    windows.push({ id: match[1], x: Number(match[3]), y: Number(match[4]), width: Number(match[5]), height: Number(match[6]), title: match[7].trim() });
  }
  return windows;
}

/**
 * Where each surface goes: the iPhone at the right, the Android phone beside it, and the browser in what is
 * left, from the left edge. The phones keep their own size. With both phones the screen is 30 short of
 * holding everything, so the iPhone's window hangs that far off the right edge, which costs only its empty
 * strip, and the emulator's buttons stand over the empty strip at its left: nothing of either phone is
 * covered. The order is the order to raise them in.
 */
export function layoutOf(windows, screen = SCREEN) {
  const of = (kind) => windows.filter((window) => kindOf(window.title) === kind);
  const iphone = of("iphone")[0];
  const android = of("android")[0];
  const browsers = of("browser");
  const placed = [];
  let left = screen.width;
  if (iphone) {
    left = screen.width - IPHONE.width + (android ? IPHONE.margin - 3 : 0);
    placed.push({ ...iphone, kind: "iphone", x: left, y: 0, width: IPHONE.width, height: IPHONE.height });
  }
  if (android) {
    // Beside the iPhone, its buttons over the empty strip at the iPhone's left; alone, with room for them.
    const right = iphone ? left + IPHONE.margin - EMULATOR_BAR : screen.width - EMULATOR_BAR;
    left = Math.max(0, right - android.width);
    placed.unshift({ ...android, kind: "android", x: left, y: 0 });
  }
  if (!iphone && !android) return [];
  const width = Math.max(BROWSER_MIN, left - 1);
  for (const browser of browsers) placed.unshift({ ...browser, kind: "browser", x: 0, y: 0, width, height: screen.height - 10 });
  return placed;
}

const NAMES = { browser: "the browser", android: "the Android phone", iphone: "the iPhone" };

/** In words, what was arranged, left to right. */
export function said(placed) {
  const kinds = [...new Set(placed.map((window) => window.kind))];
  if (!kinds.length) return "This desktop has no phone on screen, so there is nothing to put side by side.";
  const names = kinds.map((kind) => NAMES[kind]);
  const list = names.length > 1 ? `${names.slice(0, -1).join(", ")} and ${names.at(-1)}` : names[0];
  return `Side by side, from the left: ${list}.`;
}

const wmctrl = (args) => spawnSync("wmctrl", args, { encoding: "utf8" });
const frontFile = path.join(os.homedir(), ".workstream", "front");

/**
 * Bring the surface that is being used to the front, once: again only after another surface was used, so a
 * person who moves a window while the agent works on one surface is not fought over it. Never fails.
 */
export function front(kind, { file = frontFile } = {}) {
  try {
    if (!process.env.DISPLAY || (existsSync(file) && readFileSync(file, "utf8").trim() === kind)) return false;
    const listing = wmctrl(["-lG"]);
    const window = listing.status === 0 ? windowsOf(listing.stdout).find((one) => kindOf(one.title) === kind) : null;
    if (!window) return false;
    wmctrl(["-i", "-a", window.id]);
    mkdirSync(path.dirname(file), { recursive: true });
    writeFileSync(file, kind);
    return true;
  } catch {
    return false;
  }
}

/** Put the surfaces side by side. Answers what was placed, each with its kind. */
function place() {
  const listing = wmctrl(["-lG"]);
  if (listing.status !== 0) throw new Error("The desktop's windows could not be listed. Is this a Workstream desktop's session?");
  const windows = windowsOf(listing.stdout);
  const placed = layoutOf(windows);
  for (const window of placed) {
    wmctrl(["-i", "-r", window.id, "-b", "remove,maximized_vert,maximized_horz"]);
    wmctrl(["-i", "-r", window.id, "-e", `0,${window.x},${window.y},${window.width},${window.height}`]);
    wmctrl(["-i", "-a", window.id]);
  }
  // Every surface is in view now, so whichever is used next is brought forward again.
  try {
    writeFileSync(frontFile, "");
  } catch {
    // Nothing to forget.
  }
  return { placed, windows };
}

function arrange() {
  process.stdout.write(said(place().placed) + "\n");
  return 0;
}

/**
 * The surfaces to take a picture of, one of each kind: what was put side by side, or, on a desktop with no
 * phone on screen, the browser where it is.
 */
export function shownOf(placed, windows) {
  const all = placed.length ? placed : windows.filter((window) => kindOf(window.title) === "browser").map((window) => ({ ...window, kind: "browser" }));
  return ["browser", "android", "iphone"].map((kind) => all.findLast((window) => window.kind === kind)).filter(Boolean);
}

/** Where a window's own area is on the screen, from `xwininfo -id`: without its title bar and borders. */
export function areaOf(info) {
  const read = (label) => {
    const match = new RegExp(`${label}:\\s+(-?\\d+)`).exec(info ?? "");
    return match ? Number(match[1]) : null;
  };
  const area = { x: read("Absolute upper-left X"), y: read("Absolute upper-left Y"), width: read("Width"), height: read("Height") };
  return Object.values(area).every((value) => value !== null) ? area : null;
}

/**
 * The part of the screen a surface's picture is taken from: its window's own area, without the empty strip
 * the iPhone's window has at each side, and only what is on the screen. Null when none of it is.
 */
export function pictureOf(kind, area, screen = SCREEN) {
  const inset = kind === "iphone" ? IPHONE.margin : 0;
  const x = Math.max(0, area.x + inset);
  const y = Math.max(0, area.y);
  const width = Math.min(screen.width, area.x + area.width - inset) - x;
  const height = Math.min(screen.height, area.y + area.height) - y;
  return width > 0 && height > 0 ? { x, y, width, height } : null;
}

const CAPTIONS = { browser: "The browser", android: "The Android phone", iphone: "The iPhone" };
const shownFolder = path.join(os.homedir(), ".workstream", "shown");
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** What the agent is told after the pictures are taken: where they are and what to do with them. */
export function shownWords(pictures, { sent, problem }) {
  if (!pictures.length) return "No surface is on this desktop's screen, so there is no picture to take. Open what you are handing over in the browser or on a phone, then run `surfaces show` again.";
  const lines = [
    sent
      ? "A picture of each surface, as it is now, was added to the conversation for the person:"
      : `A picture of each surface was taken, but could not be added to the conversation (${problem}). Tell the person that, and that the surfaces are on the desktop:`,
    ...pictures.map((picture) => `- ${CAPTIONS[picture.kind]}: ${picture.file}`),
    "Read each of these picture files now, and write your report from what they show, not from what you remember doing. If a picture does not show what you mean to hand over, put that right and run `surfaces show` again.",
  ];
  return lines.join("\n");
}

/**
 * Hand-over: every surface side by side, a picture of each kept in a file and added to the task's
 * conversation. The pictures are of the screen itself, so they show exactly what the person would see.
 */
async function show({ send = sendPicture, folder = shownFolder } = {}) {
  const { placed, windows } = place();
  const shown = shownOf(placed, windows);
  // With no phone nothing was moved, so the browser is brought forward here.
  if (!placed.length) for (const window of shown) wmctrl(["-i", "-a", window.id]);
  // The windows are drawn where they were put a moment after they are moved.
  await sleep(1200);
  mkdirSync(folder, { recursive: true });
  const pictures = [];
  for (const window of shown) {
    const area = areaOf(spawnSync("xwininfo", ["-id", window.id], { encoding: "utf8" }).stdout);
    const part = area && pictureOf(window.kind, area);
    if (!part) continue;
    const file = path.join(folder, `${window.kind}.jpg`);
    const shot = spawnSync("scrot", ["--overwrite", "--quality", "85", "--autoselect", `${part.x},${part.y},${part.width},${part.height}`, file], { encoding: "utf8", timeout: 15_000 });
    if (shot.status === 0 && existsSync(file)) pictures.push({ kind: window.kind, file });
  }
  let problem = null;
  for (const picture of pictures) {
    try {
      await send(picture.file, CAPTIONS[picture.kind]);
    } catch (error) {
      problem = error.message;
      break;
    }
  }
  if (placed.length) process.stdout.write(said(placed) + "\n");
  process.stdout.write(shownWords(pictures, { sent: !problem, problem }) + "\n");
  return pictures.length && !problem ? 0 : 1;
}

/** One picture into the task's conversation, under what it is a picture of. */
async function sendPicture(file, caption) {
  await answerOf(await api("PUT", `/api/desktop/pictures/${randomUUID()}?caption=${encodeURIComponent(caption)}`, { file: await openAsBlob(file), type: "image/jpeg" }));
}

export function main(argv) {
  if (argv[0] === "arrange") return arrange();
  if (argv[0] === "show") return show();
  process.stderr.write("Use:\n  surfaces arrange   put the browser and the phones side by side\n  surfaces show      the same, then add a picture of each to the conversation: run it when you hand work over\n");
  return 2;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.stdout.on("error", (error) => {
    if (error.code !== "EPIPE") throw error;
  });
  try {
    process.env.DISPLAY ??= ":1";
    process.exitCode = await main(process.argv.slice(2));
  } catch (error) {
    process.stderr.write(`surfaces: ${error.message}\n`);
    process.exitCode = 1;
  }
}
