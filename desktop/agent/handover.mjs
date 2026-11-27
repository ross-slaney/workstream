// The hand-over: before the agent's turn ends, every surface it used is shown to the person as a picture
// (`surfaces show`), and the agent has looked at the same pictures.
//
// The agent is told to do this (phones.mjs, harness.mjs). This file is what makes sure of it: the harness
// notes which surfaces a turn used and whether they were shown afterwards, and when the agent goes to stop
// with one still owed, it is asked once to show them. A report is read in an email, or hours later with the
// desktop paused; in the first task that used a phone, the report described a screen the phone did not show.

const NAMES = { browser: "the browser", android: "the Android phone", iphone: "the iPhone" };

/** What a tool call does to a surface: uses one, shows them all, or neither (null). */
export function surfaceOf(name, input = {}) {
  if (name.startsWith("mcp__browser__")) return "browser";
  if (name !== "Bash") return null;
  const command = String(input.command ?? "");
  if (/(^|[\s;&|(])surfaces\s+show(?=[\s;&|)]|$)/.test(command)) return "shown";
  const phone = /(^|[\s;&|(])phone\s+(ios|android)(?=[\s;&|)]|$)/.exec(command);
  return phone ? (phone[2] === "ios" ? "iphone" : "android") : null;
}

/** A new turn: nothing used, nothing asked. */
export const newTurn = () => ({ used: [], asked: false });

/** Note one tool call of the turn. */
export function note(turn, name, input) {
  const surface = surfaceOf(name, input);
  if (surface === "shown") turn.used = [];
  else if (surface && !turn.used.includes(surface)) turn.used.push(surface);
  return turn;
}

/**
 * What the agent is asked when it goes to stop: null when nothing is owed, or it was already asked this
 * turn. Asked once, so an agent that cannot show a surface is not held in a loop.
 */
export function owed(turn) {
  if (turn.asked || turn.used.length === 0) return null;
  turn.asked = true;
  const names = turn.used.map((kind) => NAMES[kind]);
  const list = names.length > 1 ? `${names.slice(0, -1).join(", ")} and ${names.at(-1)}` : names[0];
  return `Before you finish: you used ${list} since the person was last shown the surfaces. Run \`surfaces show\`, which adds a picture of each surface to the conversation, and read the picture files it names. Then tell the person in a line for each surface what its picture shows, and correct anything you said before that the pictures do not show.`;
}
