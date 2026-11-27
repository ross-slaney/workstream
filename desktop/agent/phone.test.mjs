import { test } from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { ANDROID_KEYS, buildHash, hashFiles, iphoneRunning, isNativeInput, kindOf, simulatorEnvironment, sourceRootOf } from "./phone.mjs";

test("the kind of app is read from what is in its folder", () => {
  assert.equal(kindOf(["package.json", "app.json"], { dependencies: { expo: "^58.0.0", "react-native": "0.88.0" } }), "expo");
  assert.equal(kindOf(["package.json", "ios"], { dependencies: { "react-native": "0.88.0" } }), "react-native");
  assert.equal(kindOf(["pubspec.yaml", "lib"], null), "flutter");
  assert.equal(kindOf(["Acme.xcodeproj", "Acme"], null), "xcode");
  assert.equal(kindOf(["README.md"], { dependencies: { react: "19" } }), null);
});

test("the source starts where the app's packages are installed from, and never above the repository", () => {
  const has = (present) => (file) => present.includes(file);
  // An app with its own lockfile is its own source.
  assert.equal(sourceRootOf("/repo/mobile", "/repo", has(["/repo/mobile/package-lock.json", "/repo/package-lock.json"])), "/repo/mobile");
  // An app in a workspace is installed from the workspace's root.
  assert.equal(sourceRootOf("/repo/apps/mobile", "/repo", has(["/repo/pnpm-lock.yaml"])), "/repo");
  // No lockfile anywhere: the app's own folder, and nothing above the repository is looked at.
  assert.equal(sourceRootOf("/repo/apps/mobile", "/repo", has(["/yarn.lock"])), "/repo/apps/mobile");
});

test("what a native build is made from leaves out the app's JavaScript and what is built or installed", () => {
  for (const file of ["package.json", "package-lock.json", "app.json", "app.config.js", "ios/Podfile", "ios/Acme/AppDelegate.swift", "plugins/with-widgets.js", "modules/timer/ios/Timer.swift", "patches/expo+58.0.0.patch", "pubspec.lock"]) {
    assert.ok(isNativeInput(file), file);
  }
  for (const file of ["src/home/home-screen/index.tsx", "app/index.tsx", "lib/sync.ts", "node_modules/expo/package.json", "ios/Pods/Sentry/Sentry.h", "ios/build/Acme.app/Info.plist", "README.md", "locales/en.json"]) {
    assert.ok(!isNativeInput(file), file);
  }
});

test("the hash changes with a native file, with the recipe's inputs and with the folder, and not with the order files are listed in", () => {
  const contents = { "package.json": "a", "ios/Podfile": "b" };
  const read = (file) => Buffer.from(contents[file]);
  const inputs = hashFiles(["package.json", "ios/Podfile"], read);
  assert.equal(inputs, hashFiles(["ios/Podfile", "package.json"], read));
  assert.notEqual(inputs, hashFiles(["package.json", "ios/Podfile"], (file) => Buffer.from(file === "ios/Podfile" ? "changed" : contents[file])));
  const hash = buildHash({ kind: "expo", directory: ".", inputs });
  assert.match(hash, /^[0-9a-f]{64}$/);
  assert.notEqual(hash, buildHash({ kind: "expo", directory: "mobile", inputs }));
  assert.notEqual(hash, buildHash({ kind: "react-native", directory: ".", inputs }));
});

test("the simulator is reached through the person's own systemd, whatever session asks", () => {
  const env = simulatorEnvironment({ PATH: "/usr/bin", XDG_RUNTIME_DIR: "/run/workstream", DBUS_SESSION_BUS_ADDRESS: "unix:path=/tmp/session" }, 1000);
  assert.equal(env.XDG_RUNTIME_DIR, "/run/user/1000");
  assert.equal(env.DBUS_SESSION_BUS_ADDRESS, "unix:path=/run/user/1000/bus");
  assert.ok(env.PATH.endsWith(":/usr/bin"));
  assert.ok(env.PATH.startsWith(path.join(process.env.HOME ?? "", ".local/bin")));
});

test("an iPhone that is on is not started again", () => {
  assert.equal(iphoneRunning("p\trunning\tiPhone18,1\n"), true);
  assert.equal(iphoneRunning("p\tstopped\tiPhone18,1\n"), false);
  assert.equal(iphoneRunning(""), false);
  assert.equal(iphoneRunning(undefined), false);
});

test("Return and the other keys are ones Android knows by number", () => {
  assert.equal(ANDROID_KEYS.enter, "66");
  assert.equal(ANDROID_KEYS.return, "66");
  assert.equal(ANDROID_KEYS.back, "4");
});
