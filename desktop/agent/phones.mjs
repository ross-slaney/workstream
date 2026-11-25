// What a desktop does about phones, from its environment's apps (apps.json: `iphone`, `android`), kept apart
// from the agent so it can be read and tested without starting one. The `phone` command itself is phone.mjs.

export const wantsIphone = (apps) => (apps ?? []).includes("iphone");
export const wantsAndroid = (apps) => (apps ?? []).includes("android");

/** The line that installs the iPhone simulator, once: x86Sim, Apple's iOS runtime and one phone. */
export const IPHONE_SETUP = "phone ios setup || exit $?";

/**
 * The setup a desktop runs: the environment's own script, after the iPhone simulator's install when the
 * environment has the iPhone. The install takes about twenty minutes, so it belongs where the setup script
 * is: done once on the machine that prepares the environment, and kept in its saved setup. Null when there
 * is nothing to run.
 */
export function setupScriptFor(setup) {
  const own = setup?.setupScript?.trim() ? setup.setupScript : null;
  if (!wantsIphone(setup?.apps)) return own;
  const install = `# The iPhone simulator, which this environment's desktops come with.\n${IPHONE_SETUP}\n`;
  return own ? `${install}\n${own}` : install;
}

/** The commands that put the environment's phones on the screen, in the order they are started. */
export function phoneStarts(apps) {
  const starts = [];
  if (wantsAndroid(apps)) starts.push(["android", "start"]);
  if (wantsIphone(apps)) starts.push(["ios", "start"]);
  return starts;
}

/** What the agent is told about the phones this desktop has, and about the ones it does not. */
export function phoneInstructions(apps) {
  const lines = [];
  if (wantsIphone(apps)) {
    lines.push(
      "- An iPhone is on this desktop: Apple's iOS simulator, in its own window, which the person can touch too. `phone ios start` boots it and shows it (about half a minute). The project's iPhone app is built on a Mac, which Workstream does for you: `phone ios run <the app's folder>` gets the app, installs it and opens it. Your team keeps one build for each state of the native code, so this takes seconds when nothing native changed, and fifteen to thirty minutes when a native package, a config plugin or native source did. Give it a long timeout. If the build fails, the end of the Mac's log is printed: fix what it says and run it with `--again`.",
      "- An Expo or React Native development build loads its JavaScript from this desktop: start the project's dev server in the background, as the project says to (never with `CI=1`, which turns off reloading), then in the app's launcher tap \"Enter URL manually\", type the server's address (`localhost:8081` unless the project says otherwise) and tap Connect. A change to JavaScript then reaches the phone by itself.",
      "- Use the iPhone the way a person does. `phone ios snapshot` lists what is on its screen, one line each with a reference; `phone ios tap @e3` taps one, `phone ios type \"text\"` types into the focused field, `phone ios scroll down` scrolls, `phone ios open <link>` opens a link, `phone ios screenshot -o <file>` saves the screen and `phone ios logs <bundle id>` shows the app's log. If the phone stops answering, `phone ios stop` and `phone ios start` bring it back. Siri does not work on it.",
    );
  } else {
    lines.push("- This desktop has no iPhone. An environment gets one by adding the iPhone to its apps (`iphone` in workstream.yml, on a Large desktop): say so when a task needs it, and do the rest.");
  }
  lines.push(
    `- An Android phone is set up as "Phone", with four processors and 4 GB of memory. \`phone android start\` starts it in its own window, where the person can use it too, and waits until Android has booted: about a minute, and up to three the first time on a new desktop. The Android app is built here, the way the project says to (for an Expo app, \`npx expo run:android\`). Read its screen with \`adb shell uiautomator dump /sdcard/ui.xml && adb shell cat /sdcard/ui.xml\`, tap with \`adb shell input tap X Y\`, type with \`adb shell input text\`, and save the screen with \`adb exec-out screencap -p > <file>\`.`,
  );
  if (wantsIphone(apps) || wantsAndroid(apps)) {
    lines.push("- When a task changes what people see, check it on every surface this desktop has (the browser and each phone) by using it there: open the screen you changed, tap through it, and say what you saw on each. Leave each one showing the result, so the person can look and try it.");
  }
  return lines.join("\n");
}
