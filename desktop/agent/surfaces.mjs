#!/usr/bin/env node
// The surfaces on a Workstream desktop: the places a change is looked at, which are the browser and each
// phone. The `surfaces` command, for the agent and for a person at the desktop.
//
//   surfaces arrange   put the browser and the phones side by side, each fully visible, on top of the rest
//
// A window opens where its program last had it, and the last one opened covers the others. When work is
// handed over, the person should see every surface at once without moving a window. The screen is 1280 wide
// (session.sh) and the panel takes the bottom 45.

import { spawnSync } from "node:child_process";
import { pathToFileURL } from "node:url";

export const SCREEN = { width: 1280, height: 915 };
/** The iPhone's window (phone.mjs opens it this size) and the narrowest a Chromium window goes. */
const IPHONE = { width: 430, height: 866 };
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
 * left, from the left edge. The phones keep their own size. The browser cannot be narrower than Chromium
 * allows, so on a screen with both phones it reaches a little under the Android phone, which is raised
 * after it. The order is the order to raise them in.
 */
export function layoutOf(windows, screen = SCREEN) {
  const of = (kind) => windows.filter((window) => kindOf(window.title) === kind);
  const iphone = of("iphone")[0];
  const android = of("android")[0];
  const browsers = of("browser");
  const placed = [];
  let left = screen.width;
  if (iphone) {
    left = screen.width - IPHONE.width;
    placed.push({ ...iphone, kind: "iphone", x: left, y: 0, width: IPHONE.width, height: IPHONE.height });
  }
  if (android) {
    // Beside the iPhone, its buttons behind the iPhone's window; alone, with room for its buttons.
    const right = iphone ? left + 4 : screen.width - EMULATOR_BAR;
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

function arrange() {
  const wmctrl = (args) => spawnSync("wmctrl", args, { encoding: "utf8" });
  const listing = wmctrl(["-lG"]);
  if (listing.status !== 0) throw new Error("The desktop's windows could not be listed. Is this a Workstream desktop's session?");
  const placed = layoutOf(windowsOf(listing.stdout));
  for (const window of placed) {
    wmctrl(["-i", "-r", window.id, "-b", "remove,maximized_vert,maximized_horz"]);
    wmctrl(["-i", "-r", window.id, "-e", `0,${window.x},${window.y},${window.width},${window.height}`]);
    wmctrl(["-i", "-a", window.id]);
  }
  process.stdout.write(said(placed) + "\n");
  return 0;
}

export function main(argv) {
  if (argv[0] === "arrange") return arrange();
  process.stderr.write("Use:\n  surfaces arrange   put the browser and the phones side by side\n");
  return 2;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    process.env.DISPLAY ??= ":1";
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    process.stderr.write(`surfaces: ${error.message}\n`);
    process.exitCode = 1;
  }
}
