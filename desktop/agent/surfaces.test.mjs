import { test } from "node:test";
import assert from "node:assert/strict";
import { kindOf, layoutOf, said, windowsOf } from "./surfaces.mjs";

const LISTING = `0x00c00017 -1 0    0    1280 960  desktop Desktop
0x00a00003 -1 0    915  1280 45   desktop xfce4-panel
0x01000002  0 10   10   1050 895  desktop Genius - Chromium
0x02000003  0 0    15   1280 900  desktop ChecklistSquad - Visual Studio Code
0x01e0000a  0 776  24   54   506  desktop Emulator
0x01e00007  0 431  48   345  767  desktop Android Emulator - Phone:5554
0x03200002  0 851  48   430  866  desktop agent-sim`;

test("a window is a surface by its title", () => {
  assert.equal(kindOf("agent-sim"), "iphone");
  assert.equal(kindOf("Android Emulator - Phone:5554"), "android");
  assert.equal(kindOf("Genius - Chromium"), "browser");
  // The emulator's strip of buttons, the editor and the agent's own window are not surfaces.
  assert.equal(kindOf("Emulator"), null);
  assert.equal(kindOf("ChecklistSquad - Visual Studio Code"), null);
  assert.equal(kindOf("Workstream"), null);
});

test("the desktop's own windows are left out of what is listed", () => {
  assert.deepEqual(windowsOf(LISTING).map((window) => window.title), ["Genius - Chromium", "ChecklistSquad - Visual Studio Code", "Emulator", "Android Emulator - Phone:5554", "agent-sim"]);
});

test("with both phones: the browser at the left, the Android phone in the middle, the iPhone at the right, raised in that order", () => {
  const placed = layoutOf(windowsOf(LISTING));
  assert.deepEqual(placed.map((window) => [window.kind, window.x, window.y, window.width, window.height]), [
    ["browser", 0, 0, 508, 905],
    ["android", 509, 0, 345, 767],
    ["iphone", 850, 0, 430, 866],
  ]);
  assert.equal(said(placed), "Side by side, from the left: the browser, the Android phone and the iPhone.");
});

test("with one phone the browser takes all the room beside it", () => {
  const iphone = layoutOf(windowsOf(LISTING).filter((window) => !/Emulator/.test(window.title)));
  assert.deepEqual(iphone.map((window) => [window.kind, window.x, window.width]), [["browser", 0, 849], ["iphone", 850, 430]]);
  const android = layoutOf(windowsOf(LISTING).filter((window) => window.title !== "agent-sim"));
  // Room is left at the right for the emulator's strip of buttons.
  assert.deepEqual(android.map((window) => [window.kind, window.x, window.width]), [["browser", 0, 880], ["android", 881, 345]]);
});

test("with no phone on screen nothing is moved", () => {
  const placed = layoutOf(windowsOf(LISTING).filter((window) => /Chromium|Code/.test(window.title)));
  assert.deepEqual(placed, []);
  assert.match(said(placed), /no phone on screen/);
});
