#!/usr/bin/env node
// Phones on a Workstream desktop: the `phone` command, for the agent and for a person at the desktop.
//
//   phone ios setup            install the iPhone simulator (the environment's setup does it, once)
//   phone ios start            boot the iPhone and show it on the desktop
//   phone ios build [folder]   get the project's iPhone app: the build the team already has, or a new one made on a Mac
//   phone ios run [folder]     build, install and open the app on the iPhone
//   phone ios stop             shut the iPhone down
//   phone android start        start the Android phone and wait until it has booted
//
// Using either phone, with the same words (touch.mjs):
//
//   phone <ios|android> snapshot                 what is on the screen, one line each, with a reference
//   phone <ios|android> tap @e3 | "words" | X Y   tap, wait until the screen is still, and print it
//   phone <ios|android> type "text"              type into the focused field and say what it holds
//   phone <ios|android> fill @e3 "text"          tap a field, empty it, type, and say what it holds
//   phone <ios|android> scroll up|down, swipe X1 Y1 X2 Y2, open <link>, wait "words" [--gone]
//   phone <ios|android> screenshot [-o file], launch <app>, terminate <app>, logs <app>, install <file>
//   phone android back | home;  phone ios press home
//   phone ios <anything else>                    agent-sim's own command
//
// An iPhone app is built with Xcode, which a Linux desktop does not have, so `build` sends the app's source to
// Workstream, which has it built on a Mac and keeps the result for the team (api/Builds). What decides whether
// a new build is needed is a hash of everything the native app is made from: a change to JavaScript or Dart
// alone takes the build that is already there.
//
// The simulator is Apple's own iOS runtime run by x86Sim, driven through agent-sim.

import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { createWriteStream, existsSync, mkdirSync, openAsBlob, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";
import { pathToFileURL } from "node:url";
import { toldNow } from "./told.mjs";
import { acted, android, fill, iphone, readAndroid, typeText, waitFor } from "./touch.mjs";
import { linesOf } from "./screen.mjs";

/** Changes when the Mac's recipe changes what it builds (builder/ios.sh), so old builds are not taken for new ones. */
export const RECIPE = "1";

const home = os.homedir();
const dir = path.join(home, ".workstream");
const say = (text) => process.stderr.write(text + "\n");
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// ── What kind of app, where its source starts, and what its native build is made from ─────────────────────

const LOCKFILES = ["package-lock.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", "bun.lockb", "pubspec.lock"];

/** The kind of app in a folder, from what is in it; null when there is none. */
export function kindOf(files, packageJson) {
  const packages = { ...packageJson?.dependencies, ...packageJson?.devDependencies };
  if (packages.expo) return "expo";
  if (packages["react-native"]) return "react-native";
  if (files.includes("pubspec.yaml")) return "flutter";
  if (files.some((file) => /\.(xcworkspace|xcodeproj)$/.test(file))) return "xcode";
  return null;
}

/**
 * Where the source to send starts: the nearest folder at or above the app's that has a lockfile, because
 * that is where its packages are installed from (an app in a workspace is installed from the workspace's
 * root), and never above the repository. The app's own folder when there is none.
 */
export function sourceRootOf(appFolder, top, has = (file) => existsSync(file)) {
  for (let folder = appFolder; ; folder = path.dirname(folder)) {
    if (LOCKFILES.some((file) => has(path.join(folder, file)))) return folder;
    if (folder === top || folder === path.dirname(folder)) return appFolder;
  }
}

/**
 * The files a native build is made from, for an app with no tool of its own to say: what names the packages,
 * what configures the app, and the native folders. JavaScript, TypeScript and Dart under the app's own
 * source are left out, since they reach the phone from the desktop's own server.
 */
export function isNativeInput(file) {
  const name = path.basename(file);
  if (/(^|\/)(node_modules|build|Pods|\.gradle|\.dart_tool|DerivedData)\//.test(file)) return false;
  if (/^(package\.json|app\.json|app\.config\.(js|ts|mjs|cjs)|eas\.json|Podfile|Podfile\.lock|Gemfile|Gemfile\.lock|pubspec\.yaml|pubspec\.lock|metro\.config\.(js|cjs)|babel\.config\.(js|cjs))$/.test(name)) return true;
  if (LOCKFILES.includes(name)) return true;
  if (/(^|\/)(ios|plugins|modules|patches|native)\//.test(file)) return true;
  return /\.(xcodeproj|xcworkspace|xcconfig|entitlements|plist|swift|m|mm|h|podspec|storyboard|xib|xcassets|xcstrings)(\/|$)/.test(file);
}

/** One hash over files' names and contents, whatever order they were listed in. */
export function hashFiles(files, read = (file) => readFileSync(file)) {
  const hash = createHash("sha256");
  for (const file of [...files].sort()) {
    hash.update(file + "\0");
    hash.update(createHash("sha256").update(read(file)).digest());
  }
  return hash.digest("hex");
}

/** The build's hash: the kind, the recipe, the app's folder and what the native build is made from. */
export const buildHash = ({ kind, directory, inputs }) =>
  createHash("sha256").update(JSON.stringify({ recipe: RECIPE, platform: "ios", kind, directory, inputs })).digest("hex");

const run = (command, args, options = {}) => spawnSync(command, args, { encoding: "utf8", maxBuffer: 256 * 1024 * 1024, ...options });

function git(folder, args) {
  const result = run("git", ["-C", folder, ...args]);
  if (result.status !== 0) throw new Error(`git ${args[0]} failed in ${folder}: ${(result.stderr || "").trim().split("\n").pop()}`);
  return result.stdout;
}

/** Every file of the source that git knows or would add, so nothing ignored (packages, builds, secrets) is sent. */
const filesOf = (folder) => git(folder, ["ls-files", "-co", "--exclude-standard", "-z"]).split("\0").filter(Boolean);

/** What the project's own tool says its native build is made from, for an Expo app: its fingerprint. */
function expoFingerprint(appFolder) {
  const cli = path.join(appFolder, "node_modules/@expo/fingerprint/bin/cli.js");
  if (!existsSync(cli)) return null;
  const result = run(process.execPath, [cli, "fingerprint:generate", "--platform", "ios"], { cwd: appFolder });
  try {
    return result.status === 0 ? JSON.parse(result.stdout).hash ?? null : null;
  } catch {
    return null;
  }
}

/** Everything about the app in a folder that a build is asked with. */
export function describeApp(folder) {
  const appFolder = path.resolve(folder);
  if (!existsSync(appFolder)) throw new Error(`There is no folder ${folder}.`);
  const top = git(appFolder, ["rev-parse", "--show-toplevel"]).trim();
  const packageFile = path.join(appFolder, "package.json");
  const here = run("ls", ["-A", appFolder]).stdout.split("\n").filter(Boolean);
  const kind = kindOf(here, existsSync(packageFile) ? JSON.parse(readFileSync(packageFile, "utf8")) : null);
  if (!kind) throw new Error(`No iPhone app found in ${folder}: no Expo or React Native package, no pubspec.yaml and no Xcode project. Name the app's folder.`);
  const root = sourceRootOf(appFolder, top);
  const directory = path.relative(root, appFolder) || ".";
  const files = filesOf(root);
  const fingerprint = kind === "expo" ? expoFingerprint(appFolder) : null;
  const inputs = fingerprint ?? hashFiles(files.filter(isNativeInput), (file) => readFileSync(path.join(root, file)));
  return { kind, root, directory, files, hash: buildHash({ kind, directory, inputs }) };
}

// ── Workstream's API, with the desktop's key ────────────────────────────────────────────────────────────

async function api(method, route, { body, file } = {}) {
  const { api: origin, key } = await toldNow();
  if (!origin || !key) throw new Error("This desktop was not told its Workstream API, so it cannot ask for a build.");
  const headers = { authorization: `Bearer ${key}` };
  if (body) headers["content-type"] = "application/json";
  // A file goes as a blob, which has a length: the API takes a build's source only with its length said.
  if (file) headers["content-type"] = "application/gzip";
  const response = await fetch(`${origin}${route}`, {
    method, headers, body: file ?? (body ? JSON.stringify(body) : undefined),
    signal: AbortSignal.timeout(file ? 30 * 60_000 : 60_000),
  });
  return response;
}

/** The answer's JSON, or an error with the API's own words. */
async function answerOf(response) {
  if (response.ok) return response.json();
  const problem = await response.json().catch(() => null);
  throw new Error(typeof problem?.title === "string" && problem.title ? problem.title : `Workstream answered ${response.status}.`);
}

/** The source as one archive: the files git knows of, from the source's root. */
function archive(app, file) {
  // Run from the source's root, where the names are relative to. GNU tar refuses a -C that follows the names.
  const result = run("tar", ["--null", "-T", "-", "-czf", file], { input: app.files.join("\0"), cwd: app.root });
  if (result.status !== 0) throw new Error(`The source could not be archived: ${(result.stderr || "").trim().split("\n").pop()}`);
  return statSync(file).size;
}

const megabytes = (bytes) => `${(bytes / 1024 / 1024).toFixed(1)} MB`;

/**
 * The project's iPhone app, built: the folder of the `.app` on this desktop and its bundle identifier.
 * Takes what the team already has for this hash, and otherwise sends the source and waits for the Mac.
 */
export async function build(folder, { again = false } = {}) {
  const app = describeApp(folder);
  const kept = path.join(dir, "builds", app.hash);
  const marker = path.join(kept, "build.json");
  if (!again && existsSync(marker)) {
    const known = JSON.parse(readFileSync(marker, "utf8"));
    if (existsSync(known.app)) {
      say(`The iPhone app for this native code is already on this desktop (${app.hash.slice(0, 12)}).`);
      return known;
    }
  }
  let asked = await answerOf(await api("POST", "/api/desktop/builds", { body: { platform: "ios", hash: app.hash, directory: app.directory, again } }));
  if (asked.state === "waiting") {
    mkdirSync(kept, { recursive: true });
    const source = path.join(kept, "source.tar.gz");
    const bytes = archive(app, source);
    say(`Sending the source to be built on a Mac: ${app.files.length} files, ${megabytes(bytes)} (${app.kind}, ${app.hash.slice(0, 12)}).`);
    asked = await answerOf(await api("PUT", `/api/desktop/builds/${asked.id}/source`, { file: await openAsBlob(source) }));
    rmSync(source, { force: true });
  } else if (asked.state !== "done" && asked.state !== "failed") {
    say(`Your team's build of this native code is already under way (${app.hash.slice(0, 12)}). Waiting for it.`);
  }
  const started = Date.now();
  let said = "";
  while (asked.state !== "done" && asked.state !== "failed") {
    const line = asked.state === "building" ? "The Mac is building the app." : "Waiting for a Mac.";
    if (line !== said) say((said = line));
    await sleep(10_000);
    asked = await answerOf(await api("GET", `/api/desktop/builds/${asked.id}`));
    if (Date.now() - started > 100 * 60_000) throw new Error("The build did not end. Try again later.");
  }
  if (asked.state === "failed") {
    const log = await api("GET", `/api/desktop/builds/${asked.id}/log`);
    const text = log.ok ? await log.text() : "";
    mkdirSync(kept, { recursive: true });
    const logFile = path.join(kept, "build.log");
    writeFileSync(logFile, text);
    throw Object.assign(new Error(`The iPhone app did not build: ${asked.failure ?? "the log says why."}`), {
      // A build that never reached a Mac has no log, and nothing in the project to fix.
      log: text.trim() ? text.split("\n").slice(-60).join("\n") : null, logFile,
      hint: text.trim() ? "Fix what the log says, then run it again with --again." : "Once that is put right, run it again with --again.",
    });
  }
  mkdirSync(kept, { recursive: true });
  const download = await api("GET", `/api/desktop/builds/${asked.id}/app`);
  if (!download.ok) await answerOf(download);
  const tarball = path.join(kept, "app.tar.gz");
  await pipeline(Readable.fromWeb(download.body), createWriteStream(tarball));
  const unpack = run("tar", ["-xzf", tarball, "-C", kept]);
  if (unpack.status !== 0) throw new Error("The built app could not be unpacked.");
  rmSync(tarball, { force: true });
  const name = run("ls", [kept]).stdout.split("\n").find((entry) => entry.endsWith(".app"));
  const known = { app: path.join(kept, name), bundleId: asked.bundleId, hash: app.hash, macSeconds: asked.macSeconds };
  writeFileSync(marker, JSON.stringify(known));
  say(`The iPhone app is ready: ${known.app}${asked.macSeconds ? ` (built on a Mac in ${Math.round(asked.macSeconds / 60)} minutes)` : ""}.`);
  return known;
}

// ── The iPhone simulator ────────────────────────────────────────────────────────────────────────────────

const PHONE_PORT = 7979;
const tokenFile = path.join(dir, "iphone-token");

/**
 * agent-sim keeps the phone as a service of the person's own systemd, which the desktop's session is not
 * part of (it is one service of the machine's). These are that systemd's, so the same phone is reached from
 * the session, from a terminal and from the agent.
 */
export function simulatorEnvironment(env = process.env, uid = os.userInfo().uid) {
  const runtime = `/run/user/${uid}`;
  return {
    ...env,
    PATH: `${path.join(home, ".local/bin")}:${env.PATH ?? ""}`,
    XDG_RUNTIME_DIR: runtime,
    DBUS_SESSION_BUS_ADDRESS: `unix:path=${runtime}/bus`,
  };
}

const simulator = (args, options = {}) => spawnSync("agent-sim", args, { stdio: "inherit", env: simulatorEnvironment(), ...options });

function requireSimulator() {
  if (!existsSync(path.join(home, ".local/share/x86sim/var"))) {
    throw new Error("The iPhone simulator is not installed on this desktop. Add the iPhone app to the environment, or run `phone ios setup` (about twenty minutes, once).");
  }
}

/** Install the simulator: x86Sim, Apple's iOS runtime (from Apple, about 8 GB) and one phone. About twenty minutes, and 30 GB. */
function setupIphone() {
  if (existsSync(path.join(home, ".local/share/x86sim/var"))) {
    say("The iPhone simulator is already installed.");
    return 0;
  }
  const memoryGb = os.totalmem() / 1024 ** 3;
  if (memoryGb < 24) say(`This desktop has ${Math.round(memoryGb)} GB of memory. The iPhone simulator wants a Large desktop: it may be too slow to use here.`);
  // The person's own systemd outlives the session, so the phone's service has somewhere to run.
  run("sudo", ["loginctl", "enable-linger", os.userInfo().username]);
  const setup = simulator(["setup"]);
  if (setup.status !== 0) return setup.status ?? 1;
  // The first boot is the slow one. Shut down again: a saved setup keeps a disk, not what was running.
  simulator(["down"]);
  return 0;
}

const windows = () => (run("wmctrl", ["-l"]).stdout ?? "").split("\n");
const windowOpen = (title) => windows().some((line) => line.includes(title));

/**
 * Put a window where a phone belongs: at the right of the screen, beside the browser and the editor, and no
 * larger than a phone. A program asked for a size opens at the size it last had, so the window is moved once
 * it is there. The screen is 1280 wide (session.sh), and the panel takes the bottom 45.
 */
export const PLACES = { iphone: { title: "agent-sim", at: "0,850,0,430,866" }, android: { title: "Android Emulator", at: "0,430,0,-1,-1" } };

async function place({ title, at }) {
  if (!process.env.DISPLAY) return;
  for (let waited = 0; waited < 40 && !windowOpen(title); waited++) await sleep(250);
  if (windowOpen(title)) run("wmctrl", ["-r", title, "-e", at]);
}

/** Boot the phone, stream it on this desktop only, and open its window where the person can touch it. */
async function startIphone() {
  requireSimulator();
  const up = simulator(["up"]);
  if (up.status !== 0) return up.status ?? 1;
  mkdirSync(dir, { recursive: true });
  if (!existsSync(tokenFile)) writeFileSync(tokenFile, createHash("sha256").update(String(Math.random()) + Date.now()).digest("hex").slice(0, 32), { mode: 0o600 });
  const token = readFileSync(tokenFile, "utf8").trim();
  const url = `http://127.0.0.1:${PHONE_PORT}/${token}/`;
  const serving = await fetch(url, { signal: AbortSignal.timeout(1500) }).then((response) => response.ok, () => false);
  if (!serving) {
    const log = createWriteStream(path.join(dir, "iphone.log"), { flags: "a" });
    await new Promise((resolve) => log.once("open", resolve));
    spawn("agent-sim", ["serve", "--listen", `127.0.0.1:${PHONE_PORT}`, "--token", token], { detached: true, stdio: ["ignore", log, log], env: simulatorEnvironment() }).unref();
    for (let waited = 0; waited < 40; waited++) {
      await sleep(250);
      if (await fetch(url, { signal: AbortSignal.timeout(1000) }).then((response) => response.ok, () => false)) break;
    }
  }
  if (process.env.DISPLAY && !windowOpen(PLACES.iphone.title)) {
    // A browser of its own, with its own profile and no part in the desktop's browser: the agent's browser
    // tools drive that one, and would otherwise find this window among its pages and take it somewhere else.
    spawn("chromium", [`--user-data-dir=${path.join(dir, "iphone-window")}`, "--remote-debugging-port=0", `--app=${url}`, "--window-size=430,890", "--window-position=850,0"],
      { detached: true, stdio: "ignore" }).unref();
    await place(PLACES.iphone);
  }
  say("The iPhone is on. Read its screen with `phone ios snapshot`, and tap what it lists with `phone ios tap @e3`.");
  return 0;
}

async function runIphone(folder, options) {
  requireSimulator();
  const built = await build(folder, options);
  const started = await startIphone();
  if (started !== 0) return started;
  const installed = simulator(["install", built.app]);
  if (installed.status !== 0) return installed.status ?? 1;
  if (!built.bundleId) {
    say("Installed. Start it from its icon, or with `phone ios launch <bundle id>`.");
    return 0;
  }
  const launched = simulator(["launch", built.bundleId]);
  say(`Opened ${built.bundleId} on the iPhone.`);
  return launched.status ?? 0;
}

// ── The Android phone ───────────────────────────────────────────────────────────────────────────────────

const androidBooted = () => run("adb", ["shell", "getprop", "sys.boot_completed"]).stdout.trim() === "1";

/** Start the desktop's Android phone, in its own window, and wait until Android says it has booted. */
async function startAndroid() {
  if (!existsSync(path.join(home, ".android/avd/Phone.avd"))) throw new Error("This desktop has no Android phone.");
  if (!androidBooted()) {
    if (run("pgrep", ["-f", "qemu-system.*-avd Phone"]).status !== 0) {
      const log = createWriteStream(path.join(dir, "android.log"), { flags: "a" });
      await new Promise((resolve) => log.once("open", resolve));
      // From the person's home: the emulator stops at once when started from a folder it cannot read.
      spawn("emulator", ["-avd", "Phone", "-gpu", "swiftshader_indirect", "-no-audio", "-no-snapshot", "-no-metrics", "-no-nested-warnings"],
        { detached: true, stdio: ["ignore", log, log], cwd: home }).unref();
    }
    say("Starting the Android phone: about a minute, and up to three the first time on a new desktop.");
    for (let waited = 0; waited < 150 && !androidBooted(); waited++) await sleep(2000);
    if (!androidBooted()) throw new Error(`The Android phone did not boot in five minutes. Its log is ${path.join(dir, "android.log")}.`);
    await place(PLACES.android);
  }
  say("The Android phone is on. Read its screen with `phone android snapshot`, and tap what it lists with `phone android tap @e3`.");
  return 0;
}

const adbShell = (...args) => spawnSync("adb", ["shell", ...args], { stdio: "inherit" }).status ?? 1;

/** The Android screen's size in pixels, which a scroll is measured against. */
function androidSize() {
  const size = /(\d+)x(\d+)/.exec(run("adb", ["shell", "wm", "size"]).stdout ?? "");
  return size ? [Number(size[1]), Number(size[2])] : [1080, 2400];
}

/** The commands for using the Android phone, in the iPhone's words. */
async function useAndroid(command, rest, { wait }) {
  if (!androidBooted()) throw new Error("The Android phone is not on. Start it with `phone android start`.");
  const phone = android();
  const flags = rest.filter((argument) => argument.startsWith("--") || argument === "-o");
  const words = rest.filter((argument, index) => !argument.startsWith("--") && argument !== "-o" && rest[index - 1] !== "-o" && rest[index - 1] !== "--timeout" && rest[index - 1] !== "--ms");
  const valueOf = (flag) => (rest.includes(flag) ? rest[rest.indexOf(flag) + 1] : undefined);
  switch (command) {
    case "snapshot":
      process.stdout.write(linesOf(readAndroid()) + "\n");
      return 0;
    case "tap":
      if (!words.length) throw new Error("Say what to tap: a reference (@e3), words on the screen, or a point (X Y).");
      return acted(phone, () => phone.tap(words), { wait });
    case "type":
      return typeText(phone, words.join(" "));
    case "fill":
      if (words.length < 2) throw new Error('Say the field and the text: `phone android fill @e3 "text"`.');
      return fill(phone, words.slice(0, -1), words.at(-1));
    case "scroll": {
      const [width, height] = androidSize();
      const up = words[0] === "up";
      return acted(phone, () => adbShell("input", "swipe", String(width / 2), String(Math.round(height * (up ? 0.35 : 0.7))), String(width / 2), String(Math.round(height * (up ? 0.7 : 0.35))), "300"), { wait });
    }
    case "swipe":
      if (words.length !== 4) throw new Error("A swipe is four numbers: `phone android swipe X1 Y1 X2 Y2`.");
      return acted(phone, () => adbShell("input", "swipe", ...words, valueOf("--ms") ?? "300"), { wait });
    case "back":
      return acted(phone, () => adbShell("input", "keyevent", "4"), { wait });
    case "home":
      return acted(phone, () => adbShell("input", "keyevent", "3"), { wait });
    case "open":
      if (!words[0]) throw new Error("Say the link to open.");
      return acted(phone, () => (run("adb", ["shell", "am", "start", "-a", "android.intent.action.VIEW", "-d", `'${words[0].replace(/'/g, "")}'`]).status ?? 1), { wait });
    case "wait":
      return waitFor(phone, words.join(" "), { gone: flags.includes("--gone"), seconds: Number(valueOf("--timeout") ?? 20) });
    case "screenshot": {
      const file = valueOf("-o") ?? "/tmp/android-screen.png";
      const shot = spawnSync("adb", ["exec-out", "screencap", "-p"], { maxBuffer: 64 * 1024 * 1024 });
      if (shot.status !== 0 || !shot.stdout?.length) throw new Error("The Android phone's screen could not be saved.");
      writeFileSync(file, shot.stdout);
      process.stdout.write(file + "\n");
      return 0;
    }
    case "launch":
      if (!words[0]) throw new Error("Say the app's package, such as com.example.app.");
      return acted(phone, () => (run("adb", ["shell", "monkey", "-p", words[0], "-c", "android.intent.category.LAUNCHER", "1"]).status ?? 1), { wait });
    case "terminate":
      return adbShell("am", "force-stop", words[0] ?? "");
    case "install":
      return spawnSync("adb", ["install", "-r", words[0] ?? ""], { stdio: "inherit" }).status ?? 1;
    case "logs": {
      const pid = run("adb", ["shell", "pidof", words[0] ?? ""]).stdout.trim().split(/\s+/)[0];
      return spawnSync("adb", pid ? ["logcat", "-d", "-t", "300", `--pid=${pid}`] : ["logcat", "-d", "-t", "300"], { stdio: "inherit" }).status ?? 1;
    }
    default:
      say(USAGE);
      return 2;
  }
}

/** The commands for using the iPhone that wait for the screen; the rest are agent-sim's own. */
async function useIphone(command, rest, { wait }) {
  requireSimulator();
  const phone = iphone(simulatorEnvironment());
  const words = rest.filter((argument) => !argument.startsWith("--"));
  switch (command) {
    case "tap":
    case "scroll":
    case "swipe":
    case "press":
    case "open":
    case "launch":
      return acted(phone, () => simulator([command, ...rest]).status ?? 1, { wait });
    case "type":
      // Key presses are for apps that watch each one; they are sent as they are, and the screen is waited for.
      if (rest.includes("--keys")) return acted(phone, () => simulator([command, ...rest]).status ?? 1, { wait });
      return typeText(phone, words.join(" "));
    case "fill":
      if (words.length < 2) throw new Error('Say the field and the text: `phone ios fill @e3 "text"`.');
      return fill(phone, words.slice(0, -1), words.at(-1));
    default:
      return simulator([command, ...rest]).status ?? 1;
  }
}

// ── The command ─────────────────────────────────────────────────────────────────────────────────────────

const USAGE = `Use:
  phone ios setup | start | stop
  phone ios build [folder] [--again]
  phone ios run [folder] [--again]
  phone android start

  On either phone (phone ios …, phone android …):
  snapshot                              what is on the screen, one line each, with a reference
  tap @e3 | "words" | X Y               tap, wait until the screen is still, and print it
  type "text"                           type into the focused field and say what it holds
  fill @e3 "text"                       tap a field, empty it, type, and say what it holds
  scroll up|down, swipe X1 Y1 X2 Y2, open <link>, wait "words" [--gone] [--timeout 20]
  screenshot [-o file], launch <app>, terminate <app>, logs <app>, install <file>
  phone android back | home; phone ios press home
  --no-wait                             return as soon as the tap is sent`;

export async function main(argv) {
  const [platform, command, ...rest] = argv;
  const again = rest.includes("--again");
  const folder = rest.find((argument) => !argument.startsWith("--")) ?? ".";
  const touch = { wait: !rest.includes("--no-wait") };
  const given = rest.filter((argument) => argument !== "--no-wait");
  if (platform === "android" && command === "start") return startAndroid();
  if (platform === "android" && command) return useAndroid(command, given, touch);
  if (platform !== "ios" || !command) {
    say(USAGE);
    return 2;
  }
  switch (command) {
    case "setup": return setupIphone();
    case "start": return startIphone();
    case "stop": return simulator(["down"]).status ?? 0;
    case "build": {
      const built = await build(folder, { again });
      process.stdout.write(built.app + "\n");
      return 0;
    }
    case "run": return runIphone(folder, { again });
    default:
      return useIphone(command, given, touch);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  // Whoever reads the output may stop early (`phone ios tap … | head`). The command still finishes what it
  // was doing to the phone, and ends with its own result.
  process.stdout.on("error", (error) => {
    if (error.code !== "EPIPE") throw error;
  });
  try {
    process.exitCode = await main(process.argv.slice(2));
  } catch (error) {
    say(`phone: ${error.message}`);
    if (error.log) say(`\nThe end of the Mac's log (all of it is in ${error.logFile}):\n${error.log}`);
    if (error.hint) say(`\n${error.hint}`);
    process.exitCode = 1;
  }
}
