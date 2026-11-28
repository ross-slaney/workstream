// What the agent is running now, for whoever watches the task. A build or a first start takes minutes and may
// print nothing, and "the agent is working" reads the same through all of it. So the desktop says, with each
// exchange, which command is under way and for how many seconds, and the task's page counts on from there.
//
// The harness notes each tool call as it starts and as it ends (harness.mjs, its PreToolUse and PostToolUse
// hooks). Only what can take long is named: a command, a helper, a skill. Reading a file or clicking in the
// browser is over before anyone could read its name. Pure, so it is tested without a desktop.

const short = (value, max = 160) => {
  const text = String(value ?? "").split("\n").map((line) => line.trim()).find(Boolean) ?? "";
  return text.length <= max ? text : text.slice(0, max - 1).trimEnd() + "…";
};

/** The words for a tool call that may take long, or null for one that never does. A command is said as it was written. */
export function runningText(name, input = {}) {
  switch (name) {
    case "Bash":
      // A command sent to the background returns at once: nothing is waited for.
      return input.run_in_background === true ? null : short(input.command) || null;
    case "Task":
    case "Agent":
      return short(input.description ?? input.prompt, 100) || null;
    case "Skill":
      return short(input.skill ?? input.command, 60) || null;
  }
  return null;
}

/** The tool calls under way, by id. */
export const newRunning = () => new Map();

/** A tool call started. */
export function started(running, id, name, input, now = Date.now()) {
  const text = runningText(name, input ?? {});
  if (id && text && !running.has(id)) running.set(id, { text, at: now });
}

/** A tool call ended, however it ended. */
export function ended(running, id) {
  running.delete(id);
}

/**
 * What to say is running: of the calls under way, the one that has run longest, as `{ text, seconds }`.
 * Null when nothing is. The seconds are counted here, so the desktop's clock need not agree with anyone's.
 */
export function longest(running, now = Date.now()) {
  let first = null;
  for (const call of running.values()) if (!first || call.at < first.at) first = call;
  return first ? { text: first.text, seconds: Math.max(0, Math.round((now - first.at) / 1000)) } : null;
}
