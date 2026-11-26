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

/**
 * How a phone is used, which is the same on both: the commands, that each waits for the screen and prints it,
 * and what to do about text.
 */
function usingPhones(apps) {
  const which = wantsIphone(apps) ? "`phone ios …` and `phone android …`" : "`phone android …`";
  const example = wantsIphone(apps) ? "phone ios" : "phone android";
  return `- Use a phone the way a person does, with the same commands on each (${which}). \`${example} snapshot\` lists what is on the screen, one line each with a reference. \`${example} tap @e3\` taps one (or \`tap "Mark done"\` by its words, or \`tap X Y\`). \`${example} fill @e3 "text"\` taps a field, empties it and types; \`type "text"\` types into the field that already has the keyboard. \`press enter\` presses Return, and \`type "text\\n"\` types and then presses it. \`scroll down\`, \`open <link>\`, \`wait "words"\`, \`screenshot -o <file>\`, \`launch\`, \`terminate\` and \`logs <app>\` do what they say, and Android also has \`back\` and \`home\`. Every command that changes the screen waits until the screen is still and prints it, with new references: do not sleep after one or read the screen again, and send one command at a time. \`fill\` and \`type\` say what the field holds afterwards, and fail if that is not what you typed. A screen that says it did not change, or shows little, may still be loading: \`wait "words it will show"\` waits for it. The reading lists what the app says is on the screen, which leaves out a system dialog and some overlays: when a reading lists almost nothing, or says nothing changed after a tap you saw land, look at a \`screenshot\` and tap by position. When a link opens the screen you want, \`open\` it instead of tapping there. The phone you are using comes to the front of the desktop by itself, and so does the browser when you use it.`;
}

/** What the agent is told about the phones this desktop has, and about the ones it does not. */
export function phoneInstructions(apps) {
  const lines = [];
  if (wantsIphone(apps)) {
    lines.push(
      "- An iPhone is on this desktop: Apple's iOS simulator, in its own window, which the person can touch too. `phone ios start` boots it and shows it (about half a minute). The project's iPhone app is built on a Mac, which Workstream does for you: `phone ios run <the app's folder>` gets the app, installs it and opens it. Your team keeps one build for each state of the native code, so this takes seconds when nothing native changed, and fifteen to thirty minutes when a native package, a config plugin or native source did. A new build is the slowest thing here, so when the task will need the iPhone, ask for the build before anything else: `phone ios build <the app's folder> --no-wait` sends the source and returns at once, and the Mac works while you write the change. `phone ios run` then waits only for what is left: give it a long timeout. If the build fails, the end of the Mac's log is printed: fix what it says and run it with `--again`.",
      "- An Expo or React Native development build loads its JavaScript from this desktop: start the project's dev server in the background, as the project says to, but never with `CI=1`, which turns off reloading, and never with `--host localhost`, which makes it listen where the phone does not look. Then in the app's launcher tap \"Enter URL manually\", fill the field with the server's address, with `127.0.0.1` for the host (`127.0.0.1:8081` unless the project uses another port), and tap Connect. The first time a development build opens, its developer menu covers the app: tap Continue, then close it. A change to JavaScript then reaches the phone by itself.",
      "- If the iPhone stops answering, `phone ios stop` and `phone ios start` bring it back. Siri does not work on it.",
    );
  } else {
    lines.push("- This desktop has no iPhone. An environment gets one by adding the iPhone to its apps (`iphone` in workstream.yml, on a Large desktop): say so when a task needs it, and do the rest.");
  }
  lines.push(
    `- An Android phone is set up as "Phone", with four processors and 4 GB of memory. \`phone android start\` starts it in its own window, where the person can use it too, and waits until Android has booted: about a minute, and up to three the first time on a new desktop. The Android app is built here, the way the project says to (for an Expo app, \`npx expo run:android\`), which can take ten minutes or more: start it in the background and go on with the other surfaces meanwhile.`,
    usingPhones(apps),
  );
  if (wantsIphone(apps) || wantsAndroid(apps)) {
    lines.push("- When a task changes what people see, check it on every surface this desktop has (the browser and each phone) by using it there: open the screen you changed, tap through it, and say what you saw on each. Leave each one showing the result, then run `surfaces arrange`, which puts the browser and the phones side by side so the person sees all of them at once.");
  }
  return lines.join("\n");
}
